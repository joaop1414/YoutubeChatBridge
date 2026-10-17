using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

// Interpolação visual local. Os clientes detectam o rainbow observando as
// mudanças regulares de cor recebidas pela rede; nenhum RPC customizado é enviado
// a clientes vanilla ou a hosts que talvez não conheçam o mod.
internal static class RainbowColorTransition
{
    private const float DURACAO_TRANSICAO = 0.5f;
    private const float INTERVALO_RAINBOW_MIN = 0.25f;
    private const float INTERVALO_RAINBOW_MAX = 0.85f;
    private const float TEMPO_PARA_PARAR = 1.25f;
    private const string BODY_COLOR = "_BodyColor";
    private const string BACK_COLOR = "_BackColor";

    private sealed class Estado
    {
        public int CorAlvo = -1;
        public Color CorAtual;
        public Color CorInicial;
        public Color CorDestino;
        public Color SombraAtual;
        public Color SombraInicial;
        public Color SombraDestino;
        public float Inicio;
        public bool Inicializado;

        public int UltimaCorObservada = -1;
        public float HoraUltimaMudanca;
        public int MudancasRegulares;
        public bool DetectadoAutomaticamente;
    }

    private static readonly HashSet<byte> AtivosForcados = new();
    private static readonly HashSet<byte> Ativos = new();
    private static readonly HashSet<byte> PendenteRestaurarVisual = new();
    private static readonly Dictionary<byte, Estado> Estados = new();

    // IDs de jogadores que estão transformados. Durante esse período não
    // interpolamos o material: mantemos a cor exata que o jogo registrou.
    private static readonly HashSet<byte> MetamorfosTransformados = new();

    internal static void DefinirMetamorfoTransformado(byte playerId, bool transformado)
    {
        if (transformado)
        {
            MetamorfosTransformados.Add(playerId);
            // Retira o jogador do loop visual durante a transformação. A lista
            // será reativada explicitamente quando ele voltar à forma original.
            Ativos.Remove(playerId);
            AtivosForcados.Remove(playerId);
            PendenteRestaurarVisual.Remove(playerId);
            Estados.Remove(playerId);
            Estado estado = ObterEstado(playerId);
            estado.CorAlvo = -1;
            estado.Inicializado = false;
            estado.UltimaCorObservada = -1;
            estado.MudancasRegulares = 0;
        }
        else
        {
            MetamorfosTransformados.Remove(playerId);
            ReiniciarTransicao(playerId);
        }
    }

    internal static void DefinirAtivo(byte playerId, bool ativo)
    {
        if (ativo)
        {
            AtivosForcados.Add(playerId);
            Ativos.Add(playerId);
            PendenteRestaurarVisual.Remove(playerId);
            ObterEstado(playerId);
        }
        else
        {
            AtivosForcados.Remove(playerId);
            Ativos.Remove(playerId);
            Estados.Remove(playerId);
        }
    }

    // O Shapeshifter pode reutilizar o renderer do alvo durante a transição.
    // Descarta a interpolação antiga para que a próxima atualização use a cor
    // registrada no DefaultOutfit depois da transformação/reversão.
    internal static void ReiniciarTransicao(byte playerId)
    {
        if (!Ativos.Contains(playerId) && !AtivosForcados.Contains(playerId))
        {
            Estados.Remove(playerId);
            PendenteRestaurarVisual.Add(playerId);
            return;
        }

        Estado estado = ObterEstado(playerId);
        estado.CorAlvo = -1;
        estado.Inicializado = false;
        estado.UltimaCorObservada = -1;
        estado.MudancasRegulares = 0;
        estado.DetectadoAutomaticamente = false;
    }

    private static Estado ObterEstado(byte playerId)
    {
        if (!Estados.TryGetValue(playerId, out Estado estado))
        {
            estado = new Estado();
            Estados[playerId] = estado;
        }

        return estado;
    }

    internal static void Atualizar()
    {
        if (PlayerControl.AllPlayerControls == null)
            return;

        float agora = Time.realtimeSinceStartup;

        // Detecta o arco-íris no cliente sem depender de handshake ou RPC extra:
        // a cor do jogador muda repetidamente em intervalos de aproximadamente 0,5 s.
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            byte playerId = jogador.PlayerId;
            if (MetamorfosTransformados.Contains(playerId))
            {
                // Não detecta nem reativa o rainbow pela sequência de cores
                // enquanto o jogador estiver transformado.
                Estado pausado = ObterEstado(playerId);
                pausado.UltimaCorObservada = -1;
                pausado.MudancasRegulares = 0;
                continue;
            }

            int idCor = (int)jogador.Data.DefaultOutfit.ColorId;
            Estado estado = ObterEstado(playerId);

            if (estado.UltimaCorObservada != idCor)
            {
                if (estado.UltimaCorObservada >= 0)
                {
                    float intervalo = agora - estado.HoraUltimaMudanca;
                    if (intervalo >= INTERVALO_RAINBOW_MIN &&
                        intervalo <= INTERVALO_RAINBOW_MAX)
                        estado.MudancasRegulares++;
                    else
                        estado.MudancasRegulares = 1;
                }
                else
                {
                    estado.MudancasRegulares = 0;
                }

                estado.UltimaCorObservada = idCor;
                estado.HoraUltimaMudanca = agora;

                // Duas mudanças seguidas no intervalo do rainbow confirmam o efeito.
                if (!AtivosForcados.Contains(playerId) &&
                    estado.MudancasRegulares >= 2)
                {
                    estado.DetectadoAutomaticamente = true;
                    Ativos.Add(playerId);
                }
            }
            else if (estado.DetectadoAutomaticamente &&
                     !AtivosForcados.Contains(playerId) &&
                     agora - estado.HoraUltimaMudanca > TEMPO_PARA_PARAR)
            {
                Ativos.Remove(playerId);
                estado.DetectadoAutomaticamente = false;
                estado.MudancasRegulares = 0;
                estado.Inicializado = false;
            }
        }

        foreach (byte playerId in new List<byte>(Ativos))
        {
            PlayerControl jogador = null;
            foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
            {
                if (candidato != null && candidato.PlayerId == playerId)
                {
                    jogador = candidato;
                    break;
                }
            }

            if (jogador == null || jogador.Data == null || jogador.cosmetics == null)
                continue;

            try
            {
                var corpo = jogador.cosmetics.currentBodySprite;
                if (corpo == null || corpo.BodySprite == null)
                    continue;

                Renderer renderer = corpo.BodySprite;
                int totalCores = Palette.PlayerColors?.Length ?? 0;
                if (totalCores == 0)
                    continue;

                int idCor = (int)jogador.Data.DefaultOutfit.ColorId;
                if (idCor < 0 || idCor >= totalCores)
                    continue;

                Estado estado = ObterEstado(playerId);
                Color corRede = Palette.PlayerColors[idCor];
                Color sombraRede = Palette.ShadowColors[idCor];

                // Enquanto transformado, o jogo pode copiar a cor do alvo e
                // reutilizar o renderer. Corrige o material para a cor oficial
                // atual e suspende a interpolação até a destransformação.
                if (MetamorfosTransformados.Contains(playerId))
                {
                    renderer.material.SetColor(BODY_COLOR, corRede);
                    renderer.material.SetColor(BACK_COLOR, sombraRede);
                    estado.CorAlvo = idCor;
                    estado.CorAtual = corRede;
                    estado.CorInicial = corRede;
                    estado.CorDestino = corRede;
                    estado.SombraAtual = sombraRede;
                    estado.SombraInicial = sombraRede;
                    estado.SombraDestino = sombraRede;
                    estado.Inicializado = false;
                    continue;
                }

                if (!estado.Inicializado)
                {
                    estado.CorAlvo = idCor;
                    estado.CorAtual = corRede;
                    estado.CorInicial = corRede;
                    estado.CorDestino = corRede;
                    estado.SombraAtual = sombraRede;
                    estado.SombraInicial = sombraRede;
                    estado.SombraDestino = sombraRede;
                    estado.Inicio = agora;
                    estado.Inicializado = true;
                }
                else if (estado.CorAlvo != idCor)
                {
                    estado.CorInicial = estado.CorAtual;
                    estado.CorDestino = corRede;
                    estado.SombraInicial = estado.SombraAtual;
                    estado.SombraDestino = sombraRede;
                    estado.CorAlvo = idCor;
                    estado.Inicio = agora;
                }

                float progresso = Mathf.Clamp01(
                    (agora - estado.Inicio) / DURACAO_TRANSICAO);
                estado.CorAtual = Color.Lerp(estado.CorInicial, estado.CorDestino, progresso);
                estado.SombraAtual = Color.Lerp(
                    estado.SombraInicial, estado.SombraDestino, progresso);

                // Interpola a cor principal e a sombra no material deste corpo.
                renderer.material.SetColor(BODY_COLOR, estado.CorAtual);
                renderer.material.SetColor(BACK_COLOR, estado.SombraAtual);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    $"Rainbow visual: falha ao interpolar PlayerId={playerId}: {ex.Message}");
            }
        }

        // Ao terminar um rainbow temporário, a animação pode ter deixado o material
        // do corpo com a cor copiada do alvo. Restaure a cor de rede mesmo que o
        // jogador já não esteja na lista Ativos.
        foreach (byte playerId in new List<byte>(PendenteRestaurarVisual))
        {
            PlayerControl jogador = null;
            foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
            {
                if (candidato != null && candidato.PlayerId == playerId)
                {
                    jogador = candidato;
                    break;
                }
            }

            if (jogador == null || jogador.Data == null || jogador.cosmetics == null)
                continue;

            try
            {
                var corpo = jogador.cosmetics.currentBodySprite;
                int idCor = (int)jogador.Data.DefaultOutfit.ColorId;
                if (corpo != null && corpo.BodySprite != null &&
                    idCor >= 0 && idCor < (Palette.PlayerColors?.Length ?? 0) &&
                    idCor < (Palette.ShadowColors?.Length ?? 0))
                {
                    corpo.BodySprite.material.SetColor(BODY_COLOR, Palette.PlayerColors[idCor]);
                    corpo.BodySprite.material.SetColor(BACK_COLOR, Palette.ShadowColors[idCor]);
                    PendenteRestaurarVisual.Remove(playerId);
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    $"Rainbow visual: falha ao restaurar cor após shapeshift PlayerId={playerId}: {ex.Message}");
            }
        }

        // Limpa o estado de observação de jogadores que saíram da sala.
        var idsPresentes = new HashSet<byte>();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null)
                idsPresentes.Add(jogador.PlayerId);

        foreach (byte id in new List<byte>(Estados.Keys))
        {
            if (idsPresentes.Contains(id))
                continue;
            Estados.Remove(id);
            Ativos.Remove(id);
            AtivosForcados.Remove(id);
            PendenteRestaurarVisual.Remove(id);
            MetamorfosTransformados.Remove(id);
        }
    }
}

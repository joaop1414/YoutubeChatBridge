using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private const float IntervaloEntreSnapTo = 0f;
    private const float AtrasoAntesDasEntradas = 0.20f;
    private const float IntervaloEntreEntradas = 0f;
    private const float AtrasoRestaurarPosicao = 0.60f;
    private const float AtrasoSaida = 0.5f;

    private static bool ativo;
    private static bool armado;

    // Jogadores que foram colocados no duto para produzir a dessincronização visual.
    private static readonly Dictionary<byte, int> jogadoresNoDuto = new();

    // A saída é agendada para acontecer depois que o EnterVent já foi replicado.
    private sealed class SaidaPendente
    {
        public byte PlayerId;
        public int VentId;
        public Vector2 PosicaoOriginal;
        public float RestaurarPosicaoEm;
        public float ExecutarEm;
        public bool SaidaProcessada;
    }

    private static readonly List<SaidaPendente> saidasPendentes = new();

    private static float proximaVerificacaoMortos;

    // Invisibilidade exclusivamente local no host: o ExitVent é recebido primeiro,
    // depois os renderers/nome são ocultados apenas na tela do host.
    private sealed class EstadoVisualHost
    {
        public readonly List<Renderer> Renderers = new();
        public readonly List<bool> RenderersAtivos = new();
        public GameObject ObjetoNome;
        public bool NomeAtivo;
    }

    private static readonly Dictionary<byte, EstadoVisualHost> visuaisOcultosHost = new();

    private sealed class JogadorPreparacao
    {
        public byte PlayerId;
        public Vector2 PosicaoOriginal;
        public bool SnapToConcluido;
    }

    private enum EtapaPreparacao
    {
        Nenhuma,
        SnapTo,
        Entradas
    }

    private static readonly List<JogadorPreparacao> preparacaoPendente = new();
    private static EtapaPreparacao etapaPreparacao = EtapaPreparacao.Nenhuma;
    private static int indicePreparacao;
    private static int ventPreparacaoId = -1;
    private static Vector2 posicaoVentPreparacao;
    private static float proximoProcessamentoPreparacao;

    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = true;
        armado = true;
        jogadoresNoDuto.Clear();
        saidasPendentes.Clear();
        proximaVerificacaoMortos = Time.realtimeSinceStartup + 0.10f;

        if (ShipStatus.Instance != null)
            AplicarInvisibilidade("ativação do evento");
    }

    internal static void IniciarPartida()
    {
        if (ativo && armado && ShipStatus.Instance != null)
            AplicarInvisibilidade("início da partida");
    }

    private static void AplicarInvisibilidade(string motivo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            PlayerControl.AllPlayerControls == null ||
            AmongUsClient.Instance == null ||
            etapaPreparacao != EtapaPreparacao.Nenhuma)
            return;

        Vent ventGlobal = EncontrarVentGlobal();
        if (ventGlobal == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Invisibilidade] Não existe duto com ID 1 ou ID 0 para posicionar os jogadores.");
            return;
        }

        preparacaoPendente.Clear();
        saidasPendentes.Clear();
        jogadoresNoDuto.Clear();

        foreach (PlayerControl alvo in PlayerControl.AllPlayerControls)
        {
            if (!PodeFicarInvisivel(alvo) ||
                alvo.MyPhysics == null ||
                alvo.NetTransform == null)
                continue;

            preparacaoPendente.Add(new JogadorPreparacao
            {
                PlayerId = alvo.PlayerId,
                PosicaoOriginal = alvo.GetTruePosition()
            });
        }

        if (preparacaoPendente.Count == 0)
        {
            armado = false;
            return;
        }

        armado = false;
        ventPreparacaoId = ventGlobal.Id;
        posicaoVentPreparacao = (Vector2)ventGlobal.transform.position;
        indicePreparacao = 0;
        etapaPreparacao = EtapaPreparacao.SnapTo;
        proximoProcessamentoPreparacao = Time.realtimeSinceStartup;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Preparação iniciada. Motivo={motivo}; duto fixo={ventPreparacaoId}; " +
            $"jogadores={preparacaoPendente.Count}; intervalo entre SnapTo={IntervaloEntreSnapTo:0.00}s; " +
            $"espera antes das entradas={AtrasoAntesDasEntradas:0.00}s.");
    }

    private static void ProcessarPreparacao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            etapaPreparacao == EtapaPreparacao.Nenhuma ||
            Time.realtimeSinceStartup < proximoProcessamentoPreparacao)
            return;

        float agora = Time.realtimeSinceStartup;

        if (etapaPreparacao == EtapaPreparacao.SnapTo)
        {
            // Todos recebem o SnapTo no mesmo ciclo, sem intervalo por jogador.
            foreach (JogadorPreparacao item in preparacaoPendente)
            {
                PlayerControl alvo = EncontrarJogador(item.PlayerId);
                if (!PodeFicarInvisivel(alvo) || alvo.NetTransform == null)
                    continue;

                try
                {
                    EnviarSnapToParaTripulantes(alvo, posicaoVentPreparacao);
                    item.SnapToConcluido = true;
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha no SnapTo de PlayerId={item.PlayerId}: " +
                        ex.GetBaseException().Message);
                }
            }

            etapaPreparacao = EtapaPreparacao.Entradas;
            proximoProcessamentoPreparacao = agora + AtrasoAntesDasEntradas;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Invisibilidade] SnapTo enviado para todos no mesmo ciclo. " +
                $"Entradas simultâneas em {AtrasoAntesDasEntradas:0.00}s.");
            return;
        }

        if (etapaPreparacao == EtapaPreparacao.Entradas)
        {
            // Todos os RPCs EnterVent são disparados juntos no mesmo ciclo.
            foreach (JogadorPreparacao item in preparacaoPendente)
            {
                if (!item.SnapToConcluido)
                    continue;

                PlayerControl alvo = EncontrarJogador(item.PlayerId);
                if (!PodeFicarInvisivel(alvo) ||
                    alvo.MyPhysics == null || alvo.NetTransform == null)
                    continue;

                try
                {
                    alvo.MyPhysics.RpcEnterVent(ventPreparacaoId);
                    jogadoresNoDuto[alvo.PlayerId] = ventPreparacaoId;
                    saidasPendentes.Add(new SaidaPendente
                    {
                        PlayerId = alvo.PlayerId,
                        VentId = ventPreparacaoId,
                        PosicaoOriginal = item.PosicaoOriginal,
                        RestaurarPosicaoEm = 0f,
                        ExecutarEm = agora + AtrasoSaida,
                        SaidaProcessada = false
                    });
                }
                catch (Exception ex)
                {
                    try
                    {
                        EnviarSnapToParaTripulantes(alvo, item.PosicaoOriginal);
                    }
                    catch { }

                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha ao entrar no duto com PlayerId={item.PlayerId}: " +
                        ex.GetBaseException().Message);
                }
            }

            etapaPreparacao = EtapaPreparacao.Nenhuma;
            preparacaoPendente.Clear();
            indicePreparacao = 0;
            ventPreparacaoId = -1;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Invisibilidade] RPCs de entrada enviados simultaneamente para todos os jogadores.");
        }
    }

    private static Vent EncontrarVentGlobal()
    {
        if (ShipStatus.Instance?.AllVents == null)
            return null;

        Vent ventId1 = null;
        Vent ventId0 = null;

        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            if (vent == null)
                continue;

            if (vent.Id == 1)
                ventId1 = vent;
            else if (vent.Id == 0)
                ventId0 = vent;
        }

        // Prioriza o duto 1. Se não existir, usa o duto 0.
        return ventId1 ?? ventId0;
    }

    private static bool PodeFicarInvisivel(PlayerControl jogador) =>
        jogador != null &&
        jogador.Data != null &&
        !jogador.Data.Disconnected &&
        !jogador.Data.IsDead &&
        jogador.MyPhysics != null &&
        jogador.NetTransform != null;

    private static bool EstaVivo(PlayerControl jogador) =>
        jogador != null &&
        jogador.Data != null &&
        !jogador.Data.Disconnected &&
        !jogador.Data.IsDead &&
        jogador.OwnerId >= 0;

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        if (PlayerControl.AllPlayerControls == null)
            return null;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        }

        return null;
    }

    private static void OcultarJogadorSomenteNoHost(PlayerControl jogador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer?.Data?.Role?.IsImpostor == true ||
            jogador == null ||
            jogador.AmOwner ||
            visuaisOcultosHost.ContainsKey(jogador.PlayerId))
            return;

        try
        {
            EstadoVisualHost estado = new();

            Renderer[] renderers = jogador.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                estado.Renderers.Add(renderer);
                estado.RenderersAtivos.Add(renderer.enabled);
                renderer.enabled = false;
            }

            if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
            {
                estado.ObjetoNome = jogador.cosmetics.nameText.gameObject;
                estado.NomeAtivo = estado.ObjetoNome.activeSelf;
                estado.ObjetoNome.SetActive(false);
            }

            visuaisOcultosHost[jogador.PlayerId] = estado;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Invisibilidade] PlayerId={jogador.PlayerId} ocultado somente na tela do host.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha ao ocultar PlayerId={jogador.PlayerId} no host: " +
                ex.GetBaseException().Message);
        }
    }

    private static void RestaurarVisuaisHost()
    {
        foreach (KeyValuePair<byte, EstadoVisualHost> entrada in visuaisOcultosHost)
        {
            EstadoVisualHost estado = entrada.Value;
            if (estado == null)
                continue;

            for (int i = 0; i < estado.Renderers.Count; i++)
            {
                Renderer renderer = estado.Renderers[i];
                if (renderer == null)
                    continue;

                try
                {
                    renderer.enabled = estado.RenderersAtivos[i];
                }
                catch { }
            }

            try
            {
                if (estado.ObjetoNome != null)
                    estado.ObjetoNome.SetActive(estado.NomeAtivo);
            }
            catch { }
        }

        int total = visuaisOcultosHost.Count;
        visuaisOcultosHost.Clear();

        if (total > 0)
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Invisibilidade] Visuais restaurados no host: {total} jogador(es).");
    }

    // Envia SnapTo diretamente a cada cliente tripulante, no mesmo formato
    // GameDataTo usado pelo EHR. RpcSnapTo pelo host não atualizava de forma
    // consistente os transforms que pertencem a outros clientes.
    private static int EnviarSnapToParaTripulantes(PlayerControl alvo, Vector2 posicao)
    {
        if (alvo == null || alvo.NetTransform == null ||
            AmongUsClient.Instance == null || PlayerControl.AllPlayerControls == null)
            return 0;

        int enviados = 0;
        int hostClientId = AmongUsClient.Instance.ClientId;
        var clientes = new HashSet<int>();
        ushort sequence = (ushort)(alvo.NetTransform.lastSequenceId + 2);

        // O host precisa atualizar sua própria cópia sem enviar RPC para si.
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local != null && local.Data != null && !local.Data.IsDead &&
            !local.Data.Disconnected &&
            (alvo.AmOwner || local.Data.Role?.IsImpostor == false))
        {
            try
            {
                alvo.NetTransform.SnapTo(posicao, sequence);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] SnapTo local falhou para PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
            }
        }

        foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
        {
            if (espectador == null || espectador.Data == null ||
                espectador.Data.Disconnected || espectador.Data.IsDead ||
                espectador.Data.Role?.IsImpostor != false ||
                espectador.OwnerId < 0 || espectador.OwnerId == hostClientId ||
                !clientes.Add(espectador.OwnerId))
                continue;

            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(26); // GameData
                writer.WritePacked(AmongUsClient.Instance.GameId);
                writer.StartMessage(6); // GameDataTo
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(espectador.OwnerId);

                writer.StartMessage(2); // RPC
                writer.WritePacked(alvo.NetTransform.NetId);
                writer.Write((byte)RpcCalls.SnapTo);
                writer.Write(posicao.x);
                writer.Write(posicao.y);
                writer.Write(sequence);
                writer.EndMessage();

                writer.EndMessage(); // GameDataTo
                writer.EndMessage(); // GameData
                AmongUsClient.Instance.SendOrDisconnect(writer);
                enviados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] SnapTo direcionado falhou. OwnerId={espectador.OwnerId}; " +
                    $"PlayerId={alvo.PlayerId}; erro={ex.GetBaseException().Message}");
            }
            finally
            {
                writer?.Recycle();
            }
        }

        return enviados;
    }

    // Equivalente ao RpcExitVentDesync do EHR:
    // executa CoExitVent localmente quando o destinatário é o host;
    // caso contrário, envia o RPC nativo ExitVent somente ao OwnerId indicado.
    private static bool EnviarSaidaDutoDesync(PlayerControl alvo, int ventId, PlayerControl espectador)
    {
        if (AmongUsClient.Instance == null ||
            alvo == null ||
            alvo.MyPhysics == null ||
            espectador == null ||
            espectador.OwnerId < 0)
            return false;

        int clientId = espectador.OwnerId;

        try
        {
            if (AmongUsClient.Instance.ClientId == clientId)
            {
                alvo.MyPhysics.StopAllCoroutines();
                alvo.MyPhysics.StartCoroutine(alvo.MyPhysics.CoExitVent(ventId));
                return true;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                alvo.MyPhysics.NetId,
                (byte)RpcCalls.ExitVent,
                SendOption.Reliable,
                clientId
            );

            writer.WritePacked(ventId);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha ao sincronizar saída do duto. Alvo={alvo.PlayerId}; " +
                $"espectador={espectador.PlayerId}; erro={ex.GetBaseException().Message}");
            return false;
        }
    }

    private static void ProcessarSaidasPendentes()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        float agora = Time.realtimeSinceStartup;
        ProcessarMortesDentroDosDutos(agora);

        if (saidasPendentes.Count == 0)
            return;

        for (int i = saidasPendentes.Count - 1; i >= 0; i--)
        {
            SaidaPendente pendente = saidasPendentes[i];
            PlayerControl alvo = EncontrarJogador(pendente.PlayerId);

            // Primeiro processa a saída do duto. Restaurar antes da saída permitia
            // que a animação/estado do vent sobrescrevesse o SnapTo original.
            if (!pendente.SaidaProcessada)
            {
                if (agora < pendente.ExecutarEm)
                    continue;

                alvo = EncontrarJogador(pendente.PlayerId);
                if (alvo == null || alvo.Data == null || alvo.Data.Disconnected)
                {
                    saidasPendentes.RemoveAt(i);
                    jogadoresNoDuto.Remove(pendente.PlayerId);
                    continue;
                }

                bool morreu = alvo.Data.IsDead;
                foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
                {
                    if (espectador == null || espectador.Data == null ||
                        espectador.Data.Disconnected || espectador.OwnerId < 0)
                        continue;

                    bool ehHostLocal =
                        espectador.OwnerId == AmongUsClient.Instance.ClientId;

                    bool enviarSaida = morreu ||
                        ehHostLocal ||
                        espectador.PlayerId == alvo.PlayerId ||
                        (!espectador.Data.IsDead && espectador.Data.Role?.IsImpostor == true);

                    if (enviarSaida)
                    {
                        bool saidaEnviada = EnviarSaidaDutoDesync(
                            alvo, pendente.VentId, espectador);

                        if (saidaEnviada && ehHostLocal)
                        {
                            // O host recebe o ExitVent; a ocultação é aplicada
                            // depois da restauração de posição da preparação.
                            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                                $"[Invisibilidade] ExitVent direcionado ao host para PlayerId={alvo.PlayerId}.");
                        }
                    }
                }

                if (morreu)
                    jogadoresNoDuto.Remove(pendente.PlayerId);

                pendente.SaidaProcessada = true;
                pendente.RestaurarPosicaoEm = agora + AtrasoRestaurarPosicao;
                continue;
            }

            if (agora < pendente.RestaurarPosicaoEm)
                continue;

            saidasPendentes.RemoveAt(i);
            alvo = EncontrarJogador(pendente.PlayerId);
            if (alvo == null || alvo.Data == null || alvo.Data.Disconnected)
            {
                jogadoresNoDuto.Remove(pendente.PlayerId);
                continue;
            }

            try
            {
                if (alvo.NetTransform != null)
                    EnviarSnapToParaTripulantes(alvo, pendente.PosicaoOriginal);

                // Após sair do duto e voltar à posição original, o host oculta
                // localmente o jogador. Nenhum estado visual é enviado aos demais.
                OcultarJogadorSomenteNoHost(alvo);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] Falha na restauração final de PlayerId={pendente.PlayerId}: {ex.GetBaseException().Message}");
            }
        }
    }


    private static void ProcessarMortesDentroDosDutos(float agora)
    {
        if (agora < proximaVerificacaoMortos || jogadoresNoDuto.Count == 0)
            return;

        proximaVerificacaoMortos = agora + 0.10f;

        foreach (KeyValuePair<byte, int> entrada in new List<KeyValuePair<byte, int>>(jogadoresNoDuto))
        {
            PlayerControl alvo = EncontrarJogador(entrada.Key);
            if (alvo == null || alvo.Data == null || alvo.Data.Disconnected)
            {
                jogadoresNoDuto.Remove(entrada.Key);
                continue;
            }

            if (!alvo.Data.IsDead)
                continue;

            int saidas = 0;
            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (espectador == null || espectador.Data == null ||
                    espectador.Data.Disconnected || espectador.OwnerId < 0)
                    continue;

                // A morte desfaz a dessincronização para todos: vivos, fantasmas
                // e o próprio dono do jogador que morreu.
                if (EnviarSaidaDutoDesync(alvo, entrada.Value, espectador))
                    saidas++;
            }

            jogadoresNoDuto.Remove(entrada.Key);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Invisibilidade] Jogador morreu durante o evento; saída do duto enviada " +
                $"para {saidas} cliente(s). PlayerId={entrada.Key}; ventId={entrada.Value}.");
        }
    }

    // Usa o mesmo ponto de interceptação do evento F7:
    // CheckMurder é cancelado antes que o assassinato original seja executado.
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
    private static class AssassinatoInvisibilidadePatch
    {
        private static bool Prefix(
            PlayerControl __instance,
            [HarmonyArgument(0)] PlayerControl target)
        {
            if (!ativo ||
                !YouTubeVoteBridgePlugin.EhHost() ||
                __instance == null || target == null ||
                __instance.Data == null || target.Data == null ||
                __instance.Data.Role?.IsImpostor != true ||
                __instance.Data.IsDead || __instance.Data.Disconnected ||
                target.Data.IsDead || target.Data.Disconnected ||
                MeetingHud.Instance != null || ShipStatus.Instance == null ||
                AmongUsClient.Instance == null)
                return true;

            // Cancela o assassinato original e executa a sequência do evento F7,
            // acrescentando apenas o SnapTo do impostor até a posição da vítima.
            EnviarAssassinatoDessinc(__instance, target);
            return false;
        }
    }


    // Envia o SnapTo da vítima para todos os clientes (tripulantes, impostores e fantasmas).
    private static int EnviarSnapToParaTodos(PlayerControl alvo, Vector2 posicao)
    {
        if (alvo == null || alvo.NetTransform == null ||
            AmongUsClient.Instance == null || PlayerControl.AllPlayerControls == null)
            return 0;

        int enviados = 0;
        int hostClientId = AmongUsClient.Instance.ClientId;
        var clientes = new HashSet<int>();
        ushort sequence = (ushort)(alvo.NetTransform.lastSequenceId + 2);

        if (alvo.AmOwner || alvo.OwnerId == hostClientId)
        {
            try { alvo.NetTransform.SnapTo(posicao, sequence); }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] SnapTo local da vítima falhou: {ex.GetBaseException().Message}");
            }
        }

        foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
        {
            if (espectador == null || espectador.Data == null ||
                espectador.Data.Disconnected || espectador.OwnerId < 0 ||
                espectador.OwnerId == hostClientId ||
                !clientes.Add(espectador.OwnerId))
                continue;

            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(26);
                writer.WritePacked(AmongUsClient.Instance.GameId);
                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(espectador.OwnerId);
                writer.StartMessage(2);
                writer.WritePacked(alvo.NetTransform.NetId);
                writer.Write((byte)RpcCalls.SnapTo);
                writer.Write(posicao.x);
                writer.Write(posicao.y);
                writer.Write(sequence);
                writer.EndMessage();
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;
                enviados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] SnapTo para cliente {espectador.OwnerId} falhou: {ex.GetBaseException().Message}");
            }
            finally { writer?.Recycle(); }
        }

        return enviados;
    }

    // O RPC de entrada continua executando CoEnterVent no cliente de origem,
    // mas os clientes remotos ignoram a mensagem recebida durante este evento.
    [HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.HandleRpc))]
    private static class BloquearAnimacaoEntradaDutoPatch
    {
        private static bool Prefix(PlayerPhysics __instance, byte callId)
        {
            return !ativo || callId != 19;
        }
    }

    private static Vector2 ObterPosicaoOriginalVitima(PlayerControl vitima)
    {
        foreach (SaidaPendente pendente in saidasPendentes)
        {
            if (pendente.PlayerId == vitima.PlayerId)
                return pendente.PosicaoOriginal;
        }

        return vitima.GetTruePosition();
    }

    private static void PrepararVitimaAntesDoAssassinato(PlayerControl vitima)
    {
        if (vitima == null || !jogadoresNoDuto.TryGetValue(vitima.PlayerId, out int ventId))
            return;

        Vector2 posicaoOriginal = ObterPosicaoOriginalVitima(vitima);
        int saidas = 0;

        // Primeiro manda todos os clientes executarem a saída do duto.
        foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
        {
            if (espectador == null || espectador.Data == null ||
                espectador.Data.Disconnected || espectador.OwnerId < 0)
                continue;

            if (EnviarSaidaDutoDesync(vitima, ventId, espectador))
                saidas++;
        }

        // Depois envia a posição original para todos, sem filtrar por função.
        int snaps = EnviarSnapToParaTodos(vitima, posicaoOriginal);

        jogadoresNoDuto.Remove(vitima.PlayerId);
        saidasPendentes.RemoveAll(p => p.PlayerId == vitima.PlayerId);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Vítima retirada do duto antes da morte. PlayerId={vitima.PlayerId}; " +
            $"ExitVent={saidas}; SnapTo para clientes={snaps}; posição={posicaoOriginal}.");
    }

    // O SnapTo do assassinato é enviado somente ao próprio impostor.
    // Os tripulantes não recebem a posição do impostor, preservando a invisibilidade.
    private static void TeleportarImpostorSomenteLocal(PlayerControl assassino, Vector2 posicao)
    {
        if (assassino == null || assassino.NetTransform == null ||
            AmongUsClient.Instance == null)
            return;

        int ownerId = assassino.OwnerId;
        ushort sequence = (ushort)(assassino.NetTransform.lastSequenceId + 2);

        try
        {
            if (assassino.AmOwner || ownerId == AmongUsClient.Instance.ClientId)
            {
                // Se o impostor for o host, atualiza somente a cópia local dele.
                assassino.NetTransform.SnapTo(posicao, sequence);
                return;
            }

            // Se for cliente remoto, direciona o SnapTo exclusivamente ao impostor.
            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(26); // GameData
                writer.WritePacked(AmongUsClient.Instance.GameId);
                writer.StartMessage(6); // GameDataTo
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(ownerId);

                writer.StartMessage(2); // RPC
                writer.WritePacked(assassino.NetTransform.NetId);
                writer.Write((byte)RpcCalls.SnapTo);
                writer.Write(posicao.x);
                writer.Write(posicao.y);
                writer.Write(sequence);
                writer.EndMessage();

                writer.EndMessage(); // GameDataTo
                writer.EndMessage(); // GameData
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;
            }
            finally
            {
                writer?.Recycle();
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Teleporte local do impostor falhou. " +
                $"PlayerId={assassino.PlayerId}; erro={ex.GetBaseException().Message}");
        }
    }

    private static void EnviarAssassinatoDessinc(PlayerControl assassino, PlayerControl vitima)
    {
        if (assassino == null || vitima == null ||
            assassino.Data == null || vitima.Data == null ||
            assassino.NetTransform == null || vitima.NetTransform == null ||
            AmongUsClient.Instance == null)
            return;

        try
        {
            Vector2 posicaoVitima = vitima.GetTruePosition();

            // Se a vítima estiver na dessincronização do duto, todos os clientes
            // recebem ExitVent e o SnapTo da posição original antes da morte.
            PrepararVitimaAntesDoAssassinato(vitima);

            // Apenas o cliente do impostor recebe o teleporte.
            TeleportarImpostorSomenteLocal(assassino, posicaoVitima);

            // Usa o RPC nativo de assassinato: o próprio jogo cria o corpo real,
            // configura o fantasma e aplica as permissões normais de atravessar paredes.
            // Não chamar Die() nem criar corpo falso neste fluxo.
            assassino.RpcMurderPlayer(vitima, MurderResultFlags.Succeeded);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Invisibilidade] RPC nativo de assassinato enviado. " +
                $"Impostor={assassino.PlayerId}; vítima={vitima.PlayerId}; " +
                $"teleporte enviado somente ao impostor; posição={posicaoVitima}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Invisibilidade] Falha ao executar RPC nativo de assassinato de " +
                $"PlayerId={vitima.PlayerId}: {ex}");
        }
    }

    internal static void EncerrarNaReuniao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = false;
        armado = false;
        etapaPreparacao = EtapaPreparacao.Nenhuma;
        preparacaoPendente.Clear();
        indicePreparacao = 0;
        ventPreparacaoId = -1;
        saidasPendentes.Clear();

        int restaurados = 0;
        int saidasEnviadas = 0;

        foreach (KeyValuePair<byte, int> entrada in jogadoresNoDuto)
        {
            PlayerControl alvo = EncontrarJogador(entrada.Key);
            if (alvo == null || alvo.Data == null ||
                alvo.Data.Disconnected || alvo.MyPhysics == null)
                continue;

            // Na reunião, todos os clientes precisam receber ExitVent.
            // Isso desfaz a dessincronização criada durante o evento.
            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (espectador == null || espectador.Data == null ||
                    espectador.Data.Disconnected || espectador.OwnerId < 0)
                    continue;

                if (EnviarSaidaDutoDesync(alvo, entrada.Value, espectador))
                    saidasEnviadas++;
            }

            restaurados++;
        }

        jogadoresNoDuto.Clear();
        RestaurarVisuaisHost();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Evento encerrado na reunião. Jogadores restaurados={restaurados}; " +
            $"saídas direcionadas={saidasEnviadas}.");
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        etapaPreparacao = EtapaPreparacao.Nenhuma;
        preparacaoPendente.Clear();
        indicePreparacao = 0;
        ventPreparacaoId = -1;
        jogadoresNoDuto.Clear();
        saidasPendentes.Clear();

        RestaurarVisuaisHost();
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    private static class ReuniaoInvisibilidadePatch
    {
        private static void Postfix() => EncerrarNaReuniao();
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
    private static class AtualizarSaidasInvisibilidadePatch
    {
        private static void Postfix()
        {
            if (ativo)
            {
                ProcessarPreparacao();
                ProcessarSaidasPendentes();
            }
        }
    }
}

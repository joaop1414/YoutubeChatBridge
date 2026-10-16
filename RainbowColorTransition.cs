using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Animação puramente visual: o host continua sincronizando as cores normais
// pela rede, enquanto cada cliente com o mod interpola o corpo entre elas.
internal static class RainbowColorTransition
{
    private const float DURACAO_TRANSICAO = 0.5f;
    private const string BODY_COLOR = "_BodyColor";

    private sealed class Estado
    {
        public int CorAlvo = -1;
        public Color CorAtual;
        public Color CorInicial;
        public Color CorDestino;
        public float Inicio;
        public bool Inicializado;
    }

    private static readonly HashSet<byte> Ativos = new();
    private static readonly Dictionary<byte, Estado> Estados = new();

    internal static void DefinirAtivo(byte playerId, bool ativo)
    {
        if (ativo)
        {
            Ativos.Add(playerId);
            if (!Estados.ContainsKey(playerId))
                Estados[playerId] = new Estado();
        }
        else
        {
            Ativos.Remove(playerId);
            Estados.Remove(playerId);
        }
    }

    internal static void Atualizar()
    {
        if (Ativos.Count == 0 || PlayerControl.AllPlayerControls == null)
            return;

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

                Estado estado = Estados[playerId];
                Color corRede = Palette.PlayerColors[idCor];

                if (!estado.Inicializado)
                {
                    estado.CorAlvo = idCor;
                    estado.CorAtual = corRede;
                    estado.CorInicial = corRede;
                    estado.CorDestino = corRede;
                    estado.Inicio = Time.realtimeSinceStartup;
                    estado.Inicializado = true;
                }
                else if (estado.CorAlvo != idCor)
                {
                    // Começa a próxima interpolação da cor que está na tela,
                    // não da cor anterior da paleta, evitando saltos visuais.
                    estado.CorInicial = estado.CorAtual;
                    estado.CorDestino = corRede;
                    estado.CorAlvo = idCor;
                    estado.Inicio = Time.realtimeSinceStartup;
                }

                float progresso = Mathf.Clamp01(
                    (Time.realtimeSinceStartup - estado.Inicio) / DURACAO_TRANSICAO);
                estado.CorAtual = Color.Lerp(estado.CorInicial, estado.CorDestino, progresso);

                // O shader do corpo usa _BodyColor. O material é o material
                // individual do SpriteRenderer, então a animação não altera
                // a paleta global nem os outros jogadores.
                renderer.material.SetColor(BODY_COLOR, estado.CorAtual);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    $"Rainbow visual: falha ao interpolar PlayerId={playerId}: {ex.Message}");
            }
        }
    }

    internal static void Limpar()
    {
        Ativos.Clear();
        Estados.Clear();
    }
}

// RPC 249 é usado somente entre clientes que possuem este mod.
// Os clientes vanilla nunca recebem esse RPC personalizado.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class RainbowColorTransitionRpcPatch
{
    [HarmonyPrefix]
    private static bool Prefix(PlayerControl __instance, byte callId, Hazel.MessageReader reader)
    {
        if (callId != 249)
            return true;

        try
        {
            byte playerId = reader.ReadByte();
            bool ativo = reader.ReadBoolean();
            RainbowColorTransition.DefinirAtivo(playerId, ativo);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Rainbow visual: RPC de estado inválido: " + ex.Message);
        }

        // O RPC personalizado já foi tratado; não deixe o jogo tentar
        // interpretá-lo como uma chamada vanilla desconhecida.
        return false;
    }
}

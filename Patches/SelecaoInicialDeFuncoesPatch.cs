using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Reatribui as funções-base logo após o seletor nativo terminar.
/// Mantemos o fluxo de inicialização do jogo para não pular etapas que
/// a intro/tela de carregamento esperam; a distribuição final é feita pelo mod.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SelecaoInicialDeFuncoesPatch
{
    [HarmonyPostfix]
    private static void Postfix()
    {
        // Só o host pode distribuir funções para a partida inteira.
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        try
        {
            if (GameOptionsManager.Instance == null ||
                GameOptionsManager.Instance.CurrentGameOptions == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[FuncoesIniciais] Configurações da partida indisponíveis; não alterei as funções.");
                return;
            }

            List<PlayerControl> jogadores = new();
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador?.Data == null || jogador.Data.Disconnected)
                    continue;

                jogadores.Add(jogador);
            }

            if (jogadores.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[FuncoesIniciais] Nenhum jogador válido; não alterei as funções.");
                return;
            }

            int impostoresConfigurados =
                GameOptionsManager.Instance.CurrentGameOptions.NumImpostors;
            int quantidadeImpostores = Math.Clamp(impostoresConfigurados, 0, jogadores.Count);

            // Sorteio feito pelo mod: não depende de PlayerId nem da ordem da lista.
            for (int i = jogadores.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (jogadores[i], jogadores[j]) = (jogadores[j], jogadores[i]);
            }

            // O vanilla concluiu a inicialização normal; agora o mod determina
            // a distribuição final das funções-base e envia a alteração pelo RPC.
            for (int i = 0; i < jogadores.Count; i++)
            {
                RoleTypes funcao = i < quantidadeImpostores
                    ? RoleTypes.Impostor
                    : RoleTypes.Crewmate;

                jogadores[i].RpcSetRole(funcao, true);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[FuncoesIniciais] Distribuição final definida pelo mod: " +
                $"{quantidadeImpostores} impostor(es), " +
                $"{jogadores.Count - quantidadeImpostores} tripulante(s), " +
                $"total={jogadores.Count}. O fluxo nativo de início foi preservado.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[FuncoesIniciais] Erro ao aplicar a distribuição final de funções: " + ex);
        }
    }
}

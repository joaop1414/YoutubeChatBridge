using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Substitui a seleção inicial de funções do vanilla por uma seleção controlada
/// pelo host do mod. A quantidade de impostores vem das configurações da sala.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SelecaoInicialDeFuncoesPatch
{
    [HarmonyPrefix]
    private static bool Prefix()
    {
        try
        {
            // A seleção é autoritativa no host. Clientes não devem tentar
            // escolher suas próprias funções localmente.
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
                return false;

            if (GameOptionsManager.Instance == null ||
                GameOptionsManager.Instance.CurrentGameOptions == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[FuncoesIniciais] Configurações da partida indisponíveis; seleção cancelada.");
                return false;
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
                    "[FuncoesIniciais] Nenhum jogador válido; seleção cancelada.");
                return false;
            }

            // O mod controla quem recebe a função de Impostor. O número é lido
            // das configurações atuais do lobby, em vez de ser fixado no código.
            int impostoresConfigurados =
                GameOptionsManager.Instance.CurrentGameOptions.NumImpostors;
            int quantidadeImpostores = Math.Clamp(impostoresConfigurados, 0, jogadores.Count);

            // Primeiro embaralha a lista para evitar favorecer PlayerId/ordem de entrada.
            for (int i = jogadores.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (jogadores[i], jogadores[j]) = (jogadores[j], jogadores[i]);
            }

            // O host atribui explicitamente a função-base a todos os jogadores.
            // canOverrideRole=true permite que o RPC substitua a atribuição padrão.
            for (int i = 0; i < jogadores.Count; i++)
            {
                RoleTypes funcao = i < quantidadeImpostores
                    ? RoleTypes.Impostor
                    : RoleTypes.Crewmate;

                jogadores[i].RpcSetRole(funcao, true);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[FuncoesIniciais] Seleção do mod concluída: {quantidadeImpostores} impostor(es), " +
                $"{jogadores.Count - quantidadeImpostores} tripulante(s), total={jogadores.Count}. " +
                "A seleção original de RoleManager.SelectRoles foi bloqueada.");

            // Não executa o seletor vanilla: as funções foram escolhidas pelo mod.
            return false;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[FuncoesIniciais] Erro durante a seleção inicial: " + ex);

            // Não chama o seletor vanilla após uma falha parcial: isso poderia
            // sobrescrever ou duplicar as funções que o mod já distribuiu.
            return false;
        }
    }
}

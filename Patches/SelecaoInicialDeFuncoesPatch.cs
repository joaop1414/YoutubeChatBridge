using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Mantém a inicialização original do Among Us intacta e altera as funções
/// somente depois que o jogo vanilla executa RoleManager.SelectRoles().
/// Substituir CoStartGameHost exige reproduzir toda a sequência de loading,
/// criação do mapa, intro e início das tarefas; fazer isso parcialmente causava
/// a tela preta observada.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SelecaoInicialDeFuncoesPatch
{
    private static bool funcoesAplicadas;

    /// <summary>
    /// Reseta o bloqueio quando o jogo começa a preparar uma nova partida.
    /// Não substitui nem bloqueia CoStartGame/CoStartGameHost.
    /// </summary>
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
    [HarmonyPostfix]
    private static void AoPrepararPartida()
    {
        funcoesAplicadas = false;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[FuncoesIniciais] Nova partida: pronto para aplicar as funções após a seleção vanilla.");
    }

    [HarmonyPostfix]
    private static void PosSelecionarFuncoes()
    {
        AmongUsClient client = AmongUsClient.Instance;
        if (client == null || !client.AmHost)
            return;

        if (funcoesAplicadas)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[FuncoesIniciais] SelectRoles foi chamado novamente nesta partida; ignorando atribuição duplicada.");
            return;
        }

        funcoesAplicadas = true;

        try
        {
            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            if (options == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[FuncoesIniciais] CurrentGameOptions está nulo; mantendo as funções escolhidas pelo jogo.");
                return;
            }

            List<PlayerControl> players = new();
            foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            {
                if (player != null && player.Data != null &&
                    !player.Data.Disconnected && !player.Data.IsDead)
                {
                    players.Add(player);
                }
            }

            if (players.Count < 2)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[FuncoesIniciais] Apenas {players.Count} jogador(es) válido(s); mantendo as funções vanilla.");
                return;
            }

            int impostors = Math.Clamp(options.NumImpostors, 0, players.Count - 1);

            // Fisher-Yates: escolhe os impostores sem favorecer a ordem dos PlayerIds.
            for (int i = players.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (players[i], players[j]) = (players[j], players[i]);
            }

            int sucessos = 0;
            for (int i = 0; i < players.Count; i++)
            {
                PlayerControl player = players[i];
                RoleTypes role = i < impostors ? RoleTypes.Impostor : RoleTypes.Crewmate;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[FuncoesIniciais] Aplicando: PlayerId={player.PlayerId}, OwnerId={player.OwnerId}, função={role}.");

                if (RoleSwitchAPI.TrocarFuncao(player, role))
                    sucessos++;
                else
                    YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                        $"[FuncoesIniciais] Falha ao atribuir {role} a PlayerId={player.PlayerId}.");
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[FuncoesIniciais] Resultado: {sucessos}/{players.Count} atribuições solicitadas; " +
                $"{impostors} impostor(es), {players.Count - impostors} tripulante(s). " +
                "O fluxo original de CoStartGameHost foi preservado.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[FuncoesIniciais] Exceção após RoleManager.SelectRoles: " + ex);
        }
    }
}

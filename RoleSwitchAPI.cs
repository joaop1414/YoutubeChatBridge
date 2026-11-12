using System;
using System.Collections.Generic;
using AmongUs.GameOptions;

namespace YouTubeVoteBridge;

/// <summary>
/// API para trocas manuais de função usadas pelos eventos e pelo menu de testes.
/// A atribuição inicial continua sendo inteiramente controlada pelo Among Us vanilla.
/// As chamadas RpcSetRole passam pelo patch ForceCanOverrideRolePatch, que força
/// canOverrideRole=true sem substituir o transporte original do jogo.
/// </summary>
internal static class RoleSwitchAPI
{
    internal static bool TrocarFuncao(PlayerControl jogador, string nomeFuncao)
    {
        if (string.IsNullOrWhiteSpace(nomeFuncao) ||
            !Enum.TryParse(typeof(RoleTypes), nomeFuncao, true, out object valor))
        {
            LogAviso($"RoleTypes não contém a função '{nomeFuncao}'.");
            return false;
        }

        return TrocarFuncao(jogador, (RoleTypes)valor);
    }

    internal static bool TrocarFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            LogAviso("Não foi possível trocar a função: jogador inválido ou desconectado.");
            return false;
        }

        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost)
        {
            LogAviso("A troca manual de funções exige que o cliente seja o host.");
            return false;
        }

        try
        {
            // Usa o RPC vanilla. ForceCanOverrideRolePatch altera somente
            // o segundo argumento para true antes da execução original.
            jogador.RpcSetRole(funcao);
            LogInfo($"RpcSetRole vanilla solicitado: PlayerId={jogador.PlayerId}, função={funcao}, CanOverrideRole=true.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"Falha ao solicitar troca de função para PlayerId={jogador.PlayerId}: {ex}");
            return false;
        }
    }

    internal static bool TrocarFuncoes(
        IEnumerable<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes)
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost)
        {
            LogAviso("A troca em lote de funções exige que o cliente seja o host.");
            return false;
        }

        if (atribuicoes == null)
        {
            LogAviso("A lista de atribuições está nula.");
            return false;
        }

        int solicitadas = 0;
        int falhas = 0;
        HashSet<byte> jogadoresProcessados = new();

        foreach (KeyValuePair<PlayerControl, RoleTypes> item in atribuicoes)
        {
            PlayerControl jogador = item.Key;
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
            {
                falhas++;
                continue;
            }

            if (!jogadoresProcessados.Add(jogador.PlayerId))
                continue;

            solicitadas++;
            if (!TrocarFuncao(jogador, item.Value))
                falhas++;
        }

        LogInfo($"Troca em lote finalizada: solicitações={solicitadas}, falhas={falhas}.");
        return solicitadas > 0 && falhas == 0;
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleSwitchAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[RoleSwitchAPI] " + mensagem);
}

/// <summary>
/// Força o parâmetro canOverrideRole nas chamadas vanilla de RpcSetRole.
/// Não intercepta nem substitui a seleção inicial de funções.
/// </summary>
[HarmonyLib.HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
[HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
internal static class ForceCanOverrideRolePatch
{
    private static void Prefix(
        [HarmonyLib.HarmonyArgument(1)] ref bool canOverrideRole)
    {
        canOverrideRole = true;
    }
}

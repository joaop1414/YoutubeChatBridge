using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Mantém a seleção e a distribuição de funções do Among Us vanilla.
/// A única alteração é forçar canOverrideRole=true em toda chamada
/// de PlayerControl.RpcSetRole, sem capturar nem reenviar RPCs.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
[HarmonyPriority(Priority.First)]
internal static class ForceCanOverrideRolePatch
{
    private static void Prefix(
        [HarmonyArgument(1)] ref bool canOverrideRole)
    {
        canOverrideRole = true;
    }
}

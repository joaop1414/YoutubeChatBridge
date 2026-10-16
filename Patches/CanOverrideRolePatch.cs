using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// O Among Us pode chamar RpcSetRole com canOverrideRole=false durante
/// determinados modos/eventos. Forçamos o argumento a true antes da execução
/// original, seguindo a abordagem usada pelo Endless Host Roles.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class CanOverrideRolePatch
{
    [HarmonyPrefix]
    private static void Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] ref RoleTypes roleType,
        [HarmonyArgument(1)] ref bool canOverrideRole)
    {
        bool valorAnterior = canOverrideRole;
        canOverrideRole = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[CanOverrideRole] RpcSetRole: PlayerId={__instance?.PlayerId}, " +
            $"função={roleType}, canOverrideRole {valorAnterior} -> true.");
    }
}

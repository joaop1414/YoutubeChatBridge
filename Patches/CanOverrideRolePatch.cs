using AmongUs.GameOptions;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Permite que atribuições nativas de função substituam a função atual.
/// Segue a abordagem usada pelo EHR: altera o argumento antes do método original.
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
        if (!canOverrideRole)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[CanOverrideRole] Forçando override: PlayerId={__instance?.PlayerId}, função={roleType}.");
        }

        canOverrideRole = true;
    }
}

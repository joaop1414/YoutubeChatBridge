using System.Reflection;
using AmongUs.GameOptions;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Garante que qualquer atribuição de função feita pelo jogo ou pelo mod
/// permita sobrescrever a função atual. O EHR usa o mesmo princípio.
/// </summary>
[HarmonyPatch]
internal static class RpcSetRoleCanOverridePatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(PlayerControl),
            "RpcSetRole",
            new[] { typeof(RoleTypes), typeof(bool) }
        );
    }

    private static void Prefix([HarmonyArgument(1)] ref bool canOverrideRole)
    {
        if (!canOverrideRole)
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[RoleSelection] RpcSetRole interceptado: canOverrideRole alterado de false para true.");

        canOverrideRole = true;
    }
}

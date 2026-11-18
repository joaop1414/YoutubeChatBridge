using HarmonyLib;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// Ativa o Host Authority oficial do Among Us para salas hospedadas pelo mod.
/// O protocolo +25 é o mecanismo documentado pela Innersloth para mods host-only.
/// </summary>
[HarmonyPatch]
internal static class HostAuthorityPatch
{
    private const int HostAuthorityFlag = 25;

    private static bool Enabled => AmongUsClient.Instance != null &&
                                   AmongUsClient.Instance.NetworkMode == NetworkModes.OnlineGame;

    [HarmonyPatch(typeof(Constants), nameof(Constants.GetBroadcastVersion))]
    [HarmonyPostfix]
    private static void GetBroadcastVersionPostfix(ref int __result)
    {
        if (!Enabled)
            return;

        // O protocolo usa blocos de 50 versões. O +25 ativa Host Authority.
        // Evita adicionar a flag novamente caso ela já esteja presente.
        int revision = __result % 50;
        if (revision < HostAuthorityFlag)
            __result += HostAuthorityFlag;
    }

    [HarmonyPatch(typeof(Constants), nameof(Constants.IsVersionModded))]
    [HarmonyPrefix]
    private static bool IsVersionModdedPrefix(ref bool __result)
    {
        if (!Enabled)
            return true;

        __result = true;
        return false;
    }
}

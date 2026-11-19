using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>Helper de extensão usado pela RoleSwitchAPI para iniciar CoSetRole.</summary>
internal static class RoleSwitchEhrExtensions
{
    internal static void SetRole(this PlayerControl player, RoleTypes role, bool canOverrideRole = true)
    {
        if (player == null)
            return;
        player.StartCoroutine(player.CoSetRole(role, canOverrideRole));
    }
}

/// <summary>Teste: libera roleAssigned no início de CoSetRole quando override é solicitado.</summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CoSetRole))]
internal static class RoleSwitchCoSetRolePatch
{
    private static void Prefix(PlayerControl __instance, RoleTypes role, bool canOverrideRole)
    {
        if (__instance == null || !canOverrideRole)
            return;
        try
        {
            bool antes = __instance.roleAssigned;
            __instance.roleAssigned = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSwitchEHR] Prefix CoSetRole: PlayerId={__instance.PlayerId}, role={role}, canOverrideRole={canOverrideRole}, roleAssigned antes={antes}, depois={__instance.roleAssigned}.");
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchEHR] Prefix falhou ao liberar roleAssigned: " + ex);
        }
    }

    private static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] RoleTypes role)
    {
        if (__instance == null || __instance.Data == null || __instance.Data.Disconnected)
            return;
        try
        {
            string roleBehaviour = "(nulo)";
            string roleReal = "(nulo)";
            try
            {
                roleReal = __instance.Data.Role?.Role.ToString() ?? "(nulo)";
                roleBehaviour = __instance.Data.Role?.GetType().FullName ?? "(nulo)";
            }
            catch (System.Exception ex)
            {
                roleBehaviour = "<erro:" + ex.GetType().Name + ">";
            }
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSwitchEHR] CoSetRole Postfix: PlayerId={__instance.PlayerId}, solicitada={role}, Data.Role.Role={roleReal}, RoleBehaviour={roleBehaviour}, ownerId={__instance.OwnerId}.");
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchEHR] Falha ao sincronizar CoSetRole: " + ex);
        }
    }
}

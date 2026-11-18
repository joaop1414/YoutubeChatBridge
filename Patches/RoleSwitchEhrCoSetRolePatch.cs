using System;
using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Equivalente ao helper SetRole do EHR: inicia a coroutine vanilla CoSetRole
/// no PlayerControl de destino, preservando o parâmetro canOverrideRole.
/// </summary>
internal static class RoleSwitchEhrExtensions
{
    internal static void SetRole(this PlayerControl player, RoleTypes role, bool canOverrideRole = true)
    {
        if (player == null)
            return;

        player.StartCoroutine(player.CoSetRole(role, canOverrideRole));
    }
}

/// <summary>
/// Equivalente adaptado ao PlayerControlLocalSetRolePatch do EHR.
/// Sincroniza o registro local do mod com a RoleType solicitada no fluxo
/// CoSetRole, sem inventar uma função personalizada nem substituir a HUD.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CoSetRole))]
internal static class RoleSwitchCoSetRolePatch
{
    private static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] RoleTypes role)
    {
        if (__instance == null || __instance.Data == null || __instance.Data.Disconnected)
            return;

        try
        {
            var atual = RegistroFuncoesJogadores.Obter(__instance.PlayerId);
            RegistroFuncoesJogadores.Registrar(
                __instance,
                role,
                atual?.FuncaoPersonalizada);

            string roleBehaviour = "(nulo)";
            string roleReal = "(nulo)";
            try
            {
                roleReal = __instance.Data.Role?.Role.ToString() ?? "(nulo)";
                roleBehaviour = __instance.Data.Role?.GetType().FullName ?? "(nulo)";
            }
            catch (Exception ex)
            {
                roleBehaviour = "<erro:" + ex.GetType().Name + ">";
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSwitchEHR] CoSetRole Postfix: PlayerId={__instance.PlayerId}, " +
                $"solicitada={role}, Data.Role.Role={roleReal}, " +
                $"RoleBehaviour={roleBehaviour}, ownerId={__instance.OwnerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleSwitchEHR] Falha ao sincronizar CoSetRole: " + ex);
        }
    }
}

using System.Collections;
using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils;
using HarmonyLib;
using UnityEngine;

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
    private static void Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] RoleTypes role,
        [HarmonyArgument(1)] bool canOverrideRole)
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

            // O EHR abre o painel de função do HUD depois da introdução.
            // Reutilizamos esse mesmo ponto de entrada somente no cliente dono
            // do jogador alterado. Não chamamos IntroCutscene.ShowRole(),
            // portanto não reproduzimos a animação central nem a descrição
            // da introdução durante a partida.
            if (!__instance.AmOwner || AmongUsClient.Instance == null ||
                AmongUsClient.Instance.AmHost)
                return;

            __instance.StartCoroutine(AbrirPainelDeFuncaoAposSetRole(__instance, role));
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchEHR] Falha ao sincronizar CoSetRole: " + ex);
        }
    }

    private static IEnumerator AbrirPainelDeFuncaoAposSetRole(PlayerControl jogador, RoleTypes role)
    {
        // CoSetRole é uma coroutine; aguarda a aplicação vanilla terminar.
        yield return new WaitForSecondsRealtime(0.5f);

        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected ||
            !jogador.AmOwner || AmongUsClient.Instance == null ||
            AmongUsClient.Instance.AmHost)
            yield break;

        if (HudManager.InstanceExists)
        {
            HudManager.Instance.SetRolePanelOpen(true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSwitchEHR] Painel de função do HUD aberto no cliente não-host. PlayerId={jogador.PlayerId}, Role={role}.");
        }
        else
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[RoleSwitchEHR] HUD ainda não existe; painel de função não foi aberto para PlayerId={jogador.PlayerId}.");
        }
    }
}

using System;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

// Bloqueio centralizado: eventos e outras partes do mod podem ativar/desativar
// o bloqueio do desaparecimento sem criar patches duplicados.
public static class PhantomVanishAPI
{
    private static readonly System.Collections.Generic.HashSet<string> Bloqueios =
        new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Mantém o comportamento atual: o desaparecimento fica bloqueado por padrão.
    public static bool BloqueioGlobalAtivo { get; private set; } = true;

    public static void DefinirBloqueioGlobal(bool ativo)
    {
        BloqueioGlobalAtivo = ativo;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[PhantomAPI] Bloqueio global {(ativo ? "ativado" : "desativado")}.");
    }

    public static void Bloquear(string origem)
    {
        if (string.IsNullOrWhiteSpace(origem)) return;
        Bloqueios.Add(origem);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[PhantomAPI] Bloqueio adicionado: {origem}.");
    }

    public static void Desbloquear(string origem)
    {
        if (string.IsNullOrWhiteSpace(origem)) return;
        Bloqueios.Remove(origem);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[PhantomAPI] Bloqueio removido: {origem}.");
    }

    public static bool EstaBloqueado =>
        BloqueioGlobalAtivo || Bloqueios.Count > 0;
}

// Impede que a habilidade inicie uma animação de desaparecimento que o host
// vai rejeitar. Se já estiver invisível, deixa a habilidade tentar reaparecer.
[HarmonyPatch(typeof(PhantomRole), nameof(PhantomRole.UseAbility))]
internal static class YouTubeVotePhantomUseAbilityPatch
{
    private static bool Prefix(PhantomRole __instance)
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null ||
            !PhantomVanishAPI.EstaBloqueado)
            return true;

        // EHR corrige o Phantom do próprio host chamando MakePlayerVisible()
        // diretamente. Deixar UseAbility seguir o fluxo vanilla pode manter o
        // botão/animação no estado de desaparecimento quando o vanish é bloqueado.
        if (__instance != null && __instance.isInvisible)
        {
            try
            {
                __instance.MakePlayerVisible();
                RestaurarInterfaceLocal(__instance);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[Phantom] MakePlayerVisible executado para cancelar invisibilidade; PlayerId={__instance.Player?.PlayerId}.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Phantom] Falha em MakePlayerVisible: " + ex.Message);
            }

            return false;
        }

        RestaurarInterfaceLocal(__instance);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] UseAbility cancelado pela API; botão restaurado para PlayerId={__instance?.Player?.PlayerId}.");
        return false;
    }

    internal static void RestaurarInterfaceLocal(PhantomRole role)
    {
        try
        {
            if (role?.Player == null || !role.Player.AmOwner ||
                !HudManager.InstanceExists || HudManager.Instance.AbilityButton == null)
                return;

            // Recria o botão usando a configuração nativa da habilidade.
            HudManager.Instance.AbilityButton.SetFromSettings(role.Player.Data.Role.Ability);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Phantom] Não foi possível restaurar o botão local: " + ex.Message);
        }
    }
}

// Evita que outros caminhos iniciem o desaparecimento em clientes com o mod.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckVanish))]
internal static class YouTubeVotePhantomCmdCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null ||
            !PhantomVanishAPI.EstaBloqueado)
            return true;

        if (__instance != null && __instance.AmOwner &&
            HudManager.InstanceExists && HudManager.Instance.AbilityButton != null)
        {
            try { HudManager.Instance.AbilityButton.SetFromSettings(__instance.Data.Role.Ability); }
            catch { }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] CmdCheckVanish cancelado pela API para PlayerId={__instance?.PlayerId}.");
        return false;
    }
}

// No host, rejeita a execução nativa do CheckVanish quando o bloqueio está ativo.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckVanish))]
internal static class YouTubeVotePhantomCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            !PhantomVanishAPI.EstaBloqueado)
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] CheckVanish bloqueado no host para PlayerId={__instance?.PlayerId}.");
        return false;
    }
}

// Segue o ajuste do EHR: força CanOverrideRole=true em todas as chamadas
// nativas a RpcSetRole, para que a correção de papel não seja ignorada.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class YouTubeVotePhantomCanOverrideRolePatch
{
    private static void Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] ref RoleTypes roleType,
        [HarmonyArgument(1)] ref bool canOverrideRole)
    {
        canOverrideRole = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] RpcSetRole: CanOverrideRole forçado para true; PlayerId={__instance?.PlayerId}, Role={roleType}.");
    }
}

// Rejeita o pedido vindo inclusive de clientes vanilla e corrige o estado no dono.
// Segue a recuperação usada pelo EHR: SetRole(Phantom, true) + ProtectPlayer,
// enviados exclusivamente ao cliente que controla o Phantom.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class YouTubeVotePhantomHandleRpcPatch
{
    private static void Postfix(PlayerControl __instance, byte callId)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            !PhantomVanishAPI.EstaBloqueado || __instance == null ||
            __instance.Data == null || callId != (byte)RpcCalls.CheckVanish)
            return;

        int ownerId = __instance.OwnerId;
        if (ownerId < 0 || ownerId == AmongUsClient.Instance.ClientId)
            return;

        try
        {
            // O bool enviado após RoleTypes é o CanOverrideRole.
            MessageWriter roleWriter = AmongUsClient.Instance.StartRpcImmediately(
                __instance.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable, ownerId);
            roleWriter.Write((ushort)RoleTypes.Phantom);
            roleWriter.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(roleWriter);

            // O EHR usa ProtectPlayer como parte da correção direcionada do Phantom.
            MessageWriter protectWriter = AmongUsClient.Instance.StartRpcImmediately(
                __instance.NetId, (byte)RpcCalls.ProtectPlayer, SendOption.Reliable, ownerId);
            protectWriter.WriteNetObject(__instance);
            protectWriter.Write(0);
            AmongUsClient.Instance.FinishRpcImmediately(protectWriter);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Phantom] Vanish rejeitado; SetRole(Phantom, CanOverride=true) + ProtectPlayer enviados somente ao dono. PlayerId={__instance.PlayerId}, OwnerId={ownerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Phantom] Falha ao restaurar o estado de PlayerId={__instance.PlayerId}: {ex}");
        }
    }
}

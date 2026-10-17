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

// Replica a recuperação do EHR no próprio CheckVanish: o host rejeita a
// habilidade e restaura o estado no dono do Phantom, em vez de corrigir depois
// do HandleRpc, quando a transição visual já pode ter começado.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckVanish))]
internal static class YouTubeVotePhantomCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            !PhantomVanishAPI.EstaBloqueado || __instance == null)
            return true;

        try
        {
            if (__instance.AmOwner)
            {
                if (HudManager.InstanceExists && HudManager.Instance.AbilityButton != null &&
                    __instance.Data?.Role?.Ability != null)
                    HudManager.Instance.AbilityButton.SetFromSettings(__instance.Data.Role.Ability);

                // Esta versão do Among Us não expõe RpcResetAbilityCooldown em PlayerControl.
                // O botão é restaurado acima; não chamamos um RPC inexistente.
            }
            else if (__instance.OwnerId >= 0)
            {
                int ownerId = __instance.OwnerId;

                MessageWriter roleWriter = AmongUsClient.Instance.StartRpcImmediately(
                    __instance.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable, ownerId);
                roleWriter.Write((ushort)RoleTypes.Phantom);
                roleWriter.Write(true); // CanOverrideRole=true, igual ao EHR
                AmongUsClient.Instance.FinishRpcImmediately(roleWriter);

                MessageWriter protectWriter = AmongUsClient.Instance.StartRpcImmediately(
                    __instance.NetId, (byte)RpcCalls.ProtectPlayer, SendOption.Reliable, ownerId);
                protectWriter.WritePacked(__instance.NetId);
                protectWriter.Write(0);
                AmongUsClient.Instance.FinishRpcImmediately(protectWriter);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Phantom] CheckVanish rejeitado e recuperação EHR aplicada; PlayerId={__instance.PlayerId}, OwnerId={__instance.OwnerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Phantom] Falha na recuperação de CheckVanish para PlayerId={__instance.PlayerId}: {ex}");
        }

        return false;
    }
}

// Não alteramos globalmente RpcSetRole. O EHR define CanOverrideRole
// explicitamente apenas nos RPCs direcionados que precisam dessa correção.

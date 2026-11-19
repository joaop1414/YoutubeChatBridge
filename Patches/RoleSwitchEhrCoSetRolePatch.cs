using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
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

/// <summary>
/// Prepara somente as trocas personalizadas para que clientes modificados
/// convertam o próximo SetRole correspondente em canOverrideRole=false.
/// A distribuição inicial vanilla não é afetada.
/// </summary>
internal static class RoleSwitchSetRoleOverrideSync
{
    internal const byte RpcPrepareSetRole = 253;

    private static readonly Dictionary<(byte PlayerId, RoleTypes Role), float> Pendentes = new();

    internal static void Resetar() => Pendentes.Clear();

    internal static void Marcar(byte playerId, RoleTypes role)
    {
        Pendentes[(playerId, role)] = UnityEngine.Time.realtimeSinceStartup + 8f;
    }

    internal static bool Consumir(byte playerId, RoleTypes role)
    {
        var chave = (playerId, role);
        if (!Pendentes.TryGetValue(chave, out float expiraEm))
            return false;

        Pendentes.Remove(chave);
        return UnityEngine.Time.realtimeSinceStartup <= expiraEm;
    }

    internal static void PrepararClientesModificados(PlayerControl alvo, RoleTypes role)
    {
        if (alvo == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null)
            return;

        foreach (int clientId in YouTubeVoteBridgePlugin.ClientesComModSnapshot())
            EnviarMarcador(clientId, alvo.PlayerId, role);
    }

    internal static void PrepararClienteModificado(int clientId, PlayerControl alvo, RoleTypes role)
    {
        if (alvo == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null)
            return;

        EnviarMarcador(clientId, alvo.PlayerId, role);
    }

    private static void EnviarMarcador(int clientId, byte playerId, RoleTypes role)
    {
        if (clientId < 0 || clientId == AmongUsClient.Instance.ClientId)
            return;

        try
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                RpcPrepareSetRole,
                SendOption.Reliable,
                clientId);
            writer.Write(playerId);
            writer.Write((ushort)role);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSwitchEHR] Marcador enviado: cliente={clientId}, PlayerId={playerId}, Role={role}.");
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[RoleSwitchEHR] Falha ao preparar SetRole para cliente {clientId}: {ex.Message}");
        }
    }
}

/// <summary>
/// Clientes modificados convertem para false somente o SetRole personalizado
/// previamente anunciado pelo host. Todas as atribuições normais mantêm o valor
/// original recebido, inclusive a distribuição inicial de funções.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class RoleSwitchRpcSetRoleOverridePatch
{
    private static void Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] RoleTypes roleType,
        [HarmonyArgument(1)] ref bool canOverrideRole)
    {
        if (AmongUsClient.Instance == null || AmongUsClient.Instance.AmHost ||
            __instance == null)
            return;

        if (!RoleSwitchSetRoleOverrideSync.Consumir(__instance.PlayerId, roleType))
            return;

        bool recebido = canOverrideRole;
        canOverrideRole = false;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[RoleSwitchEHR] SetRole personalizado recebido: PlayerId={__instance.PlayerId}, " +
            $"Role={roleType}, canOverrideRole recebido={recebido}, aplicado=false.");
    }
}

/// <summary>
/// Libera roleAssigned somente quando o RPC permite sobrescrever a função.
/// Clientes com o mod recebem canOverrideRole=false e preservam sua função real.
/// </summary>
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
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleSwitchEHR] Prefix falhou ao liberar roleAssigned: " + ex);
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
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleSwitchEHR] Falha no diagnóstico de CoSetRole: " + ex);
        }
    }
}

using System;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;

namespace YouTubeVoteBridge;

// Bloqueia o início da habilidade nos clientes que possuem o mod.
// O host também precisa corrigir jogadores vanilla, que não executam estes patches.
[HarmonyPatch(typeof(PhantomRole), nameof(PhantomRole.UseAbility))]
internal static class YouTubeVotePhantomUseAbilityPatch
{
    private static bool Prefix(PhantomRole __instance)
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null)
            return true;

        // A habilidade do Phantom também é usada para reaparecer. Não bloqueie
        // esse caminho se o jogador já estiver invisível.
        if (__instance != null && __instance.isInvisible)
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] UseAbility bloqueado localmente para PlayerId={__instance?.Player?.PlayerId}.");
        return false;
    }
}

// Evita que outros caminhos do jogo iniciem o desaparecimento em clientes com mod.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckVanish))]
internal static class YouTubeVotePhantomCmdCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null)
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] CmdCheckVanish bloqueado para PlayerId={__instance?.PlayerId}.");
        return false;
    }
}

// No host, não deixa o RPC executar o CheckVanish nativo. Para jogadores vanilla,
// o HandleRpc postfix abaixo envia a correção diretamente ao cliente dono.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckVanish))]
internal static class YouTubeVotePhantomCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] CheckVanish bloqueado no host para PlayerId={__instance?.PlayerId}.");
        return false;
    }
}

// EHR também verifica o RPC recebido no HandleRpc: nem todos os clientes chamam
// CheckVanish no host da mesma forma. Reaplicamos o estado do Phantom e usamos
// ProtectPlayer direcionado ao dono para desfazer a invisibilidade local/cooldown.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class YouTubeVotePhantomHandleRpcPatch
{
    private static void Postfix(PlayerControl __instance, byte callId)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            __instance == null || __instance.Data == null ||
            callId != (byte)RpcCalls.CheckVanish)
            return;

        int ownerId = __instance.OwnerId;
        if (ownerId < 0 || ownerId == AmongUsClient.Instance.ClientId)
            return;

        try
        {
            // O pacote é enviado somente ao cliente que controla o Phantom.
            MessageWriter roleWriter = AmongUsClient.Instance.StartRpcImmediately(
                __instance.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                ownerId);
            roleWriter.Write((ushort)RoleTypes.Phantom);
            roleWriter.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(roleWriter);

            MessageWriter protectWriter = AmongUsClient.Instance.StartRpcImmediately(
                __instance.NetId,
                (byte)RpcCalls.ProtectPlayer,
                SendOption.Reliable,
                ownerId);
            protectWriter.WritePacked(__instance.NetId);
            protectWriter.Write(0);
            AmongUsClient.Instance.FinishRpcImmediately(protectWriter);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Phantom] Solicitação de invisibilidade rejeitada e correção enviada ao dono: PlayerId={__instance.PlayerId}, OwnerId={ownerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Phantom] Falha ao corrigir invisibilidade de PlayerId={__instance.PlayerId}: {ex}");
        }
    }
}

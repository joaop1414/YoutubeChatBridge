using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// Reimplementação independente do fluxo de SetRole usado pelo EHR.
/// Não chama PlayerControl.RpcSetRole para transmitir: envia o RPC vanilla
/// diretamente, permitindo repetir atribuições durante a mesma partida.
/// A aplicação local continua usando a coroutine vanilla CoSetRole.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class RpcSetRoleReplacer
{
    /// <summary>
    /// O EHR força este argumento porque o jogo pode recusar uma substituição
    /// quando canOverrideRole chega como false.
    /// </summary>
    private static void Prefix(ref bool canOverrideRole)
    {
        canOverrideRole = true;
    }

    internal static bool EnviarGlobal(PlayerControl jogador, RoleTypes funcao)
    {
        if (!PodeEnviar(jogador))
            return false;

        try
        {
            // Atualiza primeiro o host local pela mesma coroutine vanilla
            // usada pelo EHR em seu método SetRole.
            if (AmongUsClient.Instance.AmClient)
                jogador.StartCoroutine(jogador.CoSetRole(funcao, true));

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable);

            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            return true;
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[RpcSetRoleReplacer] Falha no envio global: " + ex);
            return false;
        }
    }

    internal static bool EnviarParaCliente(
        PlayerControl jogador,
        RoleTypes funcao,
        int clientId)
    {
        if (!PodeEnviar(jogador) || clientId < 0)
            return false;

        try
        {
            if (AmongUsClient.Instance.ClientId == clientId)
            {
                if (AmongUsClient.Instance.AmClient)
                    jogador.StartCoroutine(jogador.CoSetRole(funcao, true));
                return true;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                clientId);

            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            return true;
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[RpcSetRoleReplacer] Falha no envio individual para clientId=" +
                clientId + ": " + ex);
            return false;
        }
    }

    private static bool PodeEnviar(PlayerControl jogador)
    {
        return jogador != null &&
               jogador.Data != null &&
               AmongUsClient.Instance != null &&
               AmongUsClient.Instance.AmHost;
    }
}

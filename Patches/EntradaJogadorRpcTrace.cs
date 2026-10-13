using System;
using System.Collections.Generic;
using HarmonyLib;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

// Captura RPCs recebidos do cliente recém-conectado durante 20 segundos.
// O patch é somente diagnóstico: não consome o MessageReader nem altera o jogo.
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class EntradaJogadorTraceStartPatch
{
    [HarmonyPostfix]
    private static void Postfix(ClientData client)
    {
        if (client == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
            return;

        EntradaJogadorRpcTrace.Iniciar(client.Id);
    }
}

[HarmonyPatch(typeof(PlayerControl), "HandleRpc")]
internal static class EntradaJogadorHandleRpcTracePatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayerControl __instance, object[] __args)
    {
        try
        {
            if (__instance == null || __args == null || __args.Length == 0 ||
                AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
                return;

            int ownerId = __instance.OwnerId;
            if (!EntradaJogadorRpcTrace.EstaCapturando(ownerId))
                return;

            byte callId = Convert.ToByte(__args[0]);
            string rpcName = Enum.GetName(typeof(RpcCalls), callId) ?? "RPC_" + callId;
            string playerName = "";
            try
            {
                if (__instance.Data != null)
                    playerName = __instance.Data.PlayerName ?? "";
            }
            catch { }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[JOIN TRACE] t={Time.realtimeSinceStartup:F3} owner={ownerId} " +
                $"playerId={__instance.PlayerId} netId={__instance.NetId} " +
                $"nome=\"{playerName}\" callId={callId} nomeRpc={rpcName}");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[JOIN TRACE] Falha ao registrar RPC: " + ex.Message);
        }
    }
}

internal static class EntradaJogadorRpcTrace
{
    private static readonly Dictionary<int, float> Capturas = new();
    private const float DuracaoSegundos = 20f;

    internal static void Iniciar(int clientId)
    {
        Capturas[clientId] = Time.realtimeSinceStartup + DuracaoSegundos;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[JOIN TRACE] Iniciada captura do cliente {clientId} por {DuracaoSegundos:F0}s.");
    }

    internal static bool EstaCapturando(int clientId)
    {
        if (!Capturas.TryGetValue(clientId, out float fim))
            return false;

        if (Time.realtimeSinceStartup <= fim)
            return true;

        Capturas.Remove(clientId);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[JOIN TRACE] Captura do cliente {clientId} encerrada.");
        return false;
    }
}

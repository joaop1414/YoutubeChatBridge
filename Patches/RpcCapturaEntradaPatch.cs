using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

// Diagnóstico temporário: registra os RPCs recebidos do cliente recém-conectado.
// Não reproduz os pacotes automaticamente, pois muitos RPCs dependem de NetIds,
// estado de jogo e objetos que só existem no cliente original.
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class RpcCapturaEntradaPatch
{
    private static readonly Dictionary<int, float> CapturarAte = new();

    [HarmonyPostfix]
    private static void Postfix([HarmonyArgument(0)] ClientData client)
    {
        if (client == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
            return;

        CapturarAte[client.Id] = Time.realtimeSinceStartup + 20f;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[RPC TRACE] Capturando RPCs do cliente {client.Id} por 20 segundos.");
    }

    internal static bool DeveCapturar(int ownerId)
    {
        if (!CapturarAte.TryGetValue(ownerId, out float fim))
            return false;

        if (Time.realtimeSinceStartup <= fim)
            return true;

        CapturarAte.Remove(ownerId);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class RpcCapturaHandlePatch
{
    [HarmonyPrefix]
    private static void Prefix(PlayerControl __instance, byte callId)
    {
        try
        {
            if (__instance == null || AmongUsClient.Instance == null ||
                !AmongUsClient.Instance.AmHost)
                return;

            int ownerId = __instance.OwnerId;
            if (!RpcCapturaEntradaPatch.DeveCapturar(ownerId))
                return;

            string nome = __instance.Data != null
                ? __instance.Data.PlayerName
                : "(sem PlayerData)";
            string rpc = Enum.GetName(typeof(RpcCalls), callId) ?? "RPC_DESCONHECIDO";

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RPC TRACE] owner={ownerId} playerId={__instance.PlayerId} " +
                $"netId={__instance.NetId} nome=\"{nome}\" callId={callId} nomeRpc={rpc}");

            // Só envia o aviso depois que o cliente terminou a etapa SetLevel.
            // RegistrarEntrada agenda o envio para 1 segundo depois deste RPC.
            if (callId == (byte)RpcCalls.SetLevel)
                YouTubeVoteBridgePlugin.RegistrarEntrada(ownerId);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RPC TRACE] Falha ao registrar RPC: " + ex.Message);
        }
    }
}

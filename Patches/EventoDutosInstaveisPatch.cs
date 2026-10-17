using System;
using HarmonyLib;

namespace YouTubeVoteBridge;

// Mesmo ponto de entrada usado pelo EHR: o jogador realmente entrou no duto.
// RpcEnterVent é o envio do RPC; Vent.EnterVent é o callback com o jogador e o duto.
[HarmonyPatch(typeof(Vent), nameof(Vent.EnterVent))]
internal static class EventoDutosInstaveisPatch
{
    [HarmonyPostfix]
    private static void Postfix(Vent __instance, [HarmonyArgument(0)] PlayerControl jogador)
    {
        try
        {
            if (__instance == null || jogador == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[DutosInstaveis] Vent.EnterVent chamado com jogador ou duto nulo.");
                return;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[DutosInstaveis][ENTRADA] Jogador={jogador.Data?.PlayerName ?? "desconhecido"}, " +
                $"PlayerId={jogador.PlayerId}, VentId={__instance.Id}, VentName={__instance.name}");

            if (jogador.MyPhysics == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[DutosInstaveis] MyPhysics nulo para PlayerId={jogador.PlayerId}.");
                return;
            }

            EventoDutosInstaveis.AoEntrarNoDuto(jogador.MyPhysics, __instance);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Dutos instáveis] Falha ao detectar entrada no Vent.EnterVent: " + ex);
        }
    }
}

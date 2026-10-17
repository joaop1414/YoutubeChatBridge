using System;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Detecta a entrada efetiva no duto, inclusive quando o jogo processa a entrada
// recebida de outro cliente. RpcEnterVent sozinho só cobre a solicitação local.
[HarmonyPatch(typeof(PlayerPhysics), "EnterVent")]
internal static class EventoDutosInstaveisPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerPhysics __instance, int id)
    {
        try
        {
            if (__instance == null || ShipStatus.Instance == null)
                return;

            Vent duto = null;
            foreach (Vent candidato in ShipStatus.Instance.AllVents)
            {
                if (candidato != null && candidato.Id == id)
                {
                    duto = candidato;
                    break;
                }
            }

            if (duto != null)
                EventoDutosInstaveis.AoEntrarNoDuto(__instance, duto);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Dutos instáveis] Falha ao detectar entrada: " + ex);
        }
    }
}

using System;
using HarmonyLib;

namespace YouTubeVoteBridge;

// Usa RpcEnterVent, método que o projeto já chama diretamente, em vez de
// "EnterVent", que não existe com esse nome nesta versão do jogo.
[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.RpcEnterVent))]
internal static class EventoDutosInstaveisPatch
{
    [HarmonyPostfix]
    private static void Postfix(PlayerPhysics __instance, int ventId)
    {
        try
        {
            if (__instance == null)
                return;

            PlayerControl jogador = null;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            {
                if (p != null && p.MyPhysics == __instance)
                {
                    jogador = p;
                    break;
                }
            }

            string nome = jogador?.Data?.PlayerName ?? "desconhecido";
            string playerId = jogador != null ? jogador.PlayerId.ToString() : "desconhecido";
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[DutosInstaveis][ENTRADA] Jogador={nome}, PlayerId={playerId}, VentId={ventId}");

            if (ShipStatus.Instance == null)
                return;

            Vent duto = null;
            foreach (Vent candidato in ShipStatus.Instance.AllVents)
            {
                if (candidato != null && candidato.Id == ventId)
                {
                    duto = candidato;
                    break;
                }
            }

            if (duto == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[DutosInstaveis] Não foi possível localizar o duto VentId={ventId}.");
                return;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[DutosInstaveis] Duto identificado: VentId={duto.Id}; chamando AoEntrarNoDuto.");

            EventoDutosInstaveis.AoEntrarNoDuto(__instance, duto);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Dutos instáveis] Falha ao detectar entrada: " + ex);
        }
    }
}

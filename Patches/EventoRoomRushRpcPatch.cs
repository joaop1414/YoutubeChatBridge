using System;
using System.Linq;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

// RPC 248: cliente informa ao host que possui o mod.
// RPC 247: host envia o estado do Room Rush somente aos clientes identificados com o mod.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class EventoRoomRushRpcPatch
{
    private const byte RPC_SEGUIDOR_DESTINO = 246;
    private const byte RPC_ROOM_RUSH_ESTADO = 247;
    private const byte RPC_CLIENTE_COM_MOD = 248;

    [HarmonyPrefix]
    private static void Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] byte callId,
        [HarmonyArgument(1)] MessageReader reader)
    {
        if (__instance == null || reader == null || AmongUsClient.Instance == null)
            return;

        try
        {
            if (callId == RPC_CLIENTE_COM_MOD && YouTubeVoteBridgePlugin.EhHost())
            {
                YouTubeVoteBridgePlugin.RegistrarClienteComMod(__instance.OwnerId);
                return;
            }

            if (callId == RPC_SEGUIDOR_DESTINO && !YouTubeVoteBridgePlugin.EhHost())
            {
                uint netIdNpc = reader.ReadPackedUInt32();
                byte playerIdAlvo = reader.ReadByte();
                float x = reader.ReadSingle();
                float y = reader.ReadSingle();
                EventoSeguidorMortal.ReceberDestinoCliente(
                    netIdNpc, playerIdAlvo, new Vector2(x, y));
                return;
            }

            if (callId != RPC_ROOM_RUSH_ESTADO || YouTubeVoteBridgePlugin.EhHost())
                return;

            bool ativo = reader.ReadBoolean();
            if (!ativo)
            {
                EventoRoomRush.ReceberEstadoLocal(SystemTypes.Hallway, 0f, false);
                return;
            }

            SystemTypes sala = (SystemTypes)reader.ReadByte();
            float segundosRestantes = reader.ReadSingle();
            EventoRoomRush.ReceberEstadoLocal(sala, segundosRestantes, true);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Room Rush] Falha ao processar sincronização: " + ex.Message);
        }
    }
}

// Texto inferior no estilo do EHR. É criado a partir do texto de recarga do botão
// para herdar a mesma fonte usada pelo HUD do jogo.
[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class EventoRoomRushLowerInfoTextPatch
{
    private static TextMeshPro lowerInfoText;

    [HarmonyPostfix]
    private static void Postfix(HudManager __instance)
    {
        if (__instance == null)
            return;

        try
        {
            if (lowerInfoText == null)
            {
                if (__instance.KillButton == null ||
                    __instance.KillButton.cooldownTimerText == null)
                    return;

                lowerInfoText = UnityEngine.Object.Instantiate(
                    __instance.KillButton.cooldownTimerText,
                    __instance.transform,
                    true
                );
                lowerInfoText.name = "YouTubeVoteBridge_RoomRush_LowerInfoText";
                lowerInfoText.alignment = TextAlignmentOptions.Center;
                lowerInfoText.transform.localPosition = new Vector3(0f, -2f, 0f);
                lowerInfoText.overflowMode = TextOverflowModes.Overflow;
                lowerInfoText.enableWordWrapping = false;
                lowerInfoText.color = Color.white;
                lowerInfoText.fontSize = 2.7f;
                lowerInfoText.fontSizeMin = 2.7f;
                lowerInfoText.fontSizeMax = 2.7f;
            }

            if (lowerInfoText == null)
                return;

            string textoRoomRush = EventoRoomRush.ObterTextoHud();
            string textoTsunami = EventoTsunami.ObterTextoAvisoHud();
            string texto = string.Join("\n",
                new[] { textoRoomRush, textoTsunami }
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
            lowerInfoText.text = texto;
            lowerInfoText.enabled = !string.IsNullOrWhiteSpace(texto);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[Room Rush] Falha ao atualizar LowerInfoText: " + ex.Message);
        }
    }

}

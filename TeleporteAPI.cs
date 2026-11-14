using System;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// API central para teletransportar jogadores com sincronização de rede.
/// Primeiro envia o RPC nativo SnapTo para os clientes e depois atualiza
/// explicitamente a posição da cópia local do host.
/// </summary>
public static class TeleporteAPI
{
    /// <summary>
    /// Teletransporta um jogador para a posição indicada.
    /// </summary>
    /// <param name="jogador">Jogador que será teletransportado.</param>
    /// <param name="destino">Posição final no mapa.</param>
    /// <returns>True quando as duas etapas foram executadas.</returns>
    public static bool Teleportar(PlayerControl jogador, Vector2 destino)
    {
        if (jogador == null || jogador.NetTransform == null ||
            AmongUsClient.Instance == null)
        {
            return false;
        }

        try
        {
            var netTransform = jogador.NetTransform;

            // Reserva a sequência antes do envio. O RPC é enviado primeiro,
            // para que os clientes remotos recebam o teleporte antes da
            // atualização explícita da cópia local do host.
            ushort sequencia = (ushort)(netTransform.lastSequenceId + 1);

            // Etapa 1: envia o RPC nativo SnapTo pela rede.
            netTransform.RpcSnapTo(destino);

            // Etapa 2: aplica o SnapTo diretamente no host.
            netTransform.SnapTo(destino, sequencia);

            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[TeleporteAPI] Falha ao teleportar jogador: " + ex);
            return false;
        }
    }
}

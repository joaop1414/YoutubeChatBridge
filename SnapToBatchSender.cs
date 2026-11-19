using System;
using System.Collections.Generic;
using Hazel;
using AmongUs.InnerNet.GameDataMessages;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Agrupa vários RPCs SnapTo em uma única mensagem GameData, seguindo
/// o formato de empacotamento usado pelo CustomRpcSender do EHR.
/// Cada SnapTo continua sendo um RPC individual dentro da mensagem.
/// </summary>
internal static class SnapToBatchSender
{
    internal static int Enviar(IReadOnlyList<PlayerControl> jogadores, IReadOnlyList<Vector2> destinos)
    {
        if (jogadores == null || destinos == null ||
            jogadores.Count == 0 || jogadores.Count != destinos.Count ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return 0;

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        int enviados = 0;
        bool mensagemAberta = false;

        try
        {
            // GameData em broadcast: um único pacote externo para todos os clientes.
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);
            mensagemAberta = true;

            for (int i = 0; i < jogadores.Count; i++)
            {
                PlayerControl jogador = jogadores[i];
                if (jogador == null || jogador.NetTransform == null ||
                    jogador.Data == null || jogador.Data.Disconnected)
                    continue;

                var transformacao = jogador.NetTransform;
                ushort sequencia = (ushort)(transformacao.lastSequenceId + 1);

                // Atualiza a cópia do host usando a mesma sequência enviada pela rede.
                transformacao.SnapTo(destinos[i], sequencia);

                // RPC interno no mesmo GameData externo, como no CustomRpcSender do EHR.
                writer.StartMessage(2);
                writer.WritePacked(transformacao.NetId);
                writer.Write((byte)RpcCalls.SnapTo);
                NetHelpers.WriteVector2(destinos[i], writer);
                writer.Write(sequencia);
                writer.EndMessage();

                enviados++;
            }

            writer.EndMessage();
            mensagemAberta = false;

            if (enviados > 0)
                AmongUsClient.Instance.SendOrDisconnect(writer);

            return enviados;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[SnapToBatch] Falha ao enviar pacote agrupado: " + ex);
            return 0;
        }
        finally
        {
            if (mensagemAberta)
            {
                try { writer.EndMessage(); } catch { }
            }

            try { writer.Recycle(); } catch { }
        }
    }
}

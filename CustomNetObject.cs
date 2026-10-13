using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// Porta para o YouTubeVoteBridge o procedimento de associação de PlayerControl
/// falso usado pelo CustomNetObject do EHR para clientes vanilla.
/// </summary>
internal static class CustomNetObject
{
    private const int TamanhoMaximoLote = 500;

    public static int AssociarClientes(PlayerControl objeto, byte playerIdReservado)
    {
        if (objeto == null || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null || !AmongUsClient.Instance.AmHost)
            return 0;

        var destinatarios = new List<PlayerControl>();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == PlayerControl.LocalPlayer ||
                jogador.Data == null || jogador.Data.Disconnected ||
                jogador.OwnerId < 0)
                continue;

            destinatarios.Add(jogador);
        }

        if (destinatarios.Count == 0)
        {
            objeto.CachedPlayerData = PlayerControl.LocalPlayer.Data;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "CustomNetObject: nenhum cliente remoto elegível para associação.");
            return 0;
        }

        int enviados = 0;
        int mensagensNoLote = 0;
        MessageWriter writer = null;

        try
        {
            writer = NovoLote();

            foreach (PlayerControl jogador in destinatarios)
            {
                // O EHR coloca as mensagens direcionadas (6) dentro de uma
                // mensagem GameData (26); não envia a mensagem 6 isoladamente.
                if (mensagensNoLote > 0 && writer.Length > TamanhoMaximoLote)
                {
                    FecharEnviarLote(writer);
                    writer = NovoLote();
                    mensagensNoLote = 0;
                }

                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(jogador.OwnerId);

                // Temporariamente associa o objeto falso ao PlayerId do cliente.
                writer.StartMessage(1);
                writer.WritePacked(objeto.NetId);
                writer.Write(jogador.PlayerId);
                writer.EndMessage();

                // Sequência do EHR: MurderPlayer com FailedError inicializa
                // o PlayerControl remoto sem efetivamente matar o objeto.
                writer.StartMessage(2);
                writer.WritePacked(objeto.NetId);
                writer.Write((byte)RpcCalls.MurderPlayer);
                writer.WriteNetObject(objeto);
                writer.Write((int)MurderResultFlags.FailedError);
                writer.EndMessage();

                // Restaura o PlayerId reservado para o objeto falso.
                writer.StartMessage(1);
                writer.WritePacked(objeto.NetId);
                writer.Write(playerIdReservado);
                writer.EndMessage();

                writer.EndMessage();
                mensagensNoLote++;
                enviados++;
            }

            if (mensagensNoLote > 0)
            {
                FecharEnviarLote(writer);
                writer = null;
            }

            objeto.CachedPlayerData = PlayerControl.LocalPlayer.Data;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"CustomNetObject: associação no formato EHR enviada para {enviados} clientes.");
            return enviados;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "CustomNetObject: falha ao enviar associação no formato EHR: " + ex);
            return enviados;
        }
        finally
        {
            writer?.Recycle();
        }
    }

    private static MessageWriter NovoLote()
    {
        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        writer.StartMessage(26);
        writer.WritePacked(AmongUsClient.Instance.GameId);
        return writer;
    }

    private static void FecharEnviarLote(MessageWriter writer)
    {
        writer.EndMessage();
        AmongUsClient.Instance.SendOrDisconnect(writer);
        writer.Recycle();
    }
}

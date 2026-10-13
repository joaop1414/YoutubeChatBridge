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


    /// <summary>
    /// Envia a criação de um PlayerControl falso no formato usado pelo EHR.
    /// O objeto deve ter sido instanciado, configurado com PlayerId/isNew/notRealPlayer
    /// e posicionado pelo chamador antes desta chamada.
    /// </summary>
    public static void EnviarSpawn(PlayerControl objeto)
    {
        if (objeto == null || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null || !AmongUsClient.Instance.AmHost)
            throw new InvalidOperationException("Não é possível criar o PlayerControl falso fora do host.");

        AmongUsClient.Instance.NetIdCnt += 1U;
        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);

        try
        {
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);

            writer.StartMessage(4);
            var spawn = AmongUsClient.Instance.CreateSpawnMessage(
                objeto, -2, SpawnFlags.None);
            spawn.SerializeValues(writer);
            writer.EndMessage();

            // O EHR envia três mensagens auxiliares de spawn para manter a
            // sequência de NetId esperada por clientes vanilla.
            for (uint i = 1; i <= 3; i++)
            {
                writer.StartMessage(4);
                writer.WritePacked(2U);
                writer.WritePacked(-2);
                writer.Write((byte)SpawnFlags.None);
                writer.WritePacked(1);
                writer.WritePacked(AmongUsClient.Instance.NetIdCnt - i);
                writer.StartMessage(1);
                writer.EndMessage();
                writer.EndMessage();
            }

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer = null;
        }
        finally
        {
            writer?.Recycle();
        }

        if (PlayerControl.AllPlayerControls.Contains(objeto))
            PlayerControl.AllPlayerControls.Remove(objeto);
    }

    /// <summary>
    /// Replica a atualização de posição no padrão usado pelo CustomNetObject do EHR.
    /// </summary>
    public static void EnviarPosicao(PlayerControl objeto, UnityEngine.Vector2 posicao)
    {
        if (objeto == null || objeto.NetTransform == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        try
        {
            objeto.NetTransform.SnapTo(posicao);
            ushort sequencia = (ushort)(objeto.NetTransform.lastSequenceId + 2U);
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                objeto.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.None);
            writer.Write(posicao);
            writer.Write(sequencia);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "CustomNetObject: falha ao sincronizar posição: " + ex.Message);
        }
    }

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

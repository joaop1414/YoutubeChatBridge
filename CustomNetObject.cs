using System;
using Hazel;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// Gerencia a associação de objetos PlayerControl falsos aos clientes vanilla.
/// A associação é enviada individualmente para cada cliente, como no
/// CustomNetObject do MoreGamemodes, em vez de agrupar todos em uma mensagem global.
/// </summary>
internal static class CustomNetObject
{
    public static int AssociarClientes(PlayerControl objeto, byte playerIdReservado)
    {
        if (objeto == null || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return 0;

        int enviados = 0;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == PlayerControl.LocalPlayer ||
                jogador.Data == null || jogador.Data.Disconnected)
                continue;

            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                // Pacote direcionado a um único cliente (InnerNet message 6).
                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(jogador.OwnerId);

                // Associa temporariamente o objeto ao PlayerId real desse cliente.
                writer.StartMessage(1);
                writer.WritePacked(objeto.NetId);
                writer.Write(jogador.PlayerId);
                writer.EndMessage();

                // Inicialização usada por mods host-only para objetos falsos.
                writer.StartMessage(2);
                writer.WritePacked(objeto.NetId);
                writer.Write((byte)RpcCalls.MurderPlayer);
                writer.WriteNetObject(objeto);
                writer.Write((int)MurderResultFlags.FailedError);
                writer.EndMessage();

                // Restaura o ID reservado do objeto falso.
                writer.StartMessage(1);
                writer.WritePacked(objeto.NetId);
                writer.Write(playerIdReservado);
                writer.EndMessage();

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;
                enviados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"CustomNetObject: falha ao associar objeto ao cliente OwnerId={jogador.OwnerId}: {ex.Message}");
            }
            finally
            {
                writer?.Recycle();
            }
        }

        objeto.CachedPlayerData = PlayerControl.LocalPlayer.Data;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"CustomNetObject: associação enviada individualmente a {enviados} clientes.");
        return enviados;
    }
}

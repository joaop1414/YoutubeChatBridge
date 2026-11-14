using System;
using System.Collections.Generic;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Teleporte autoritativo inspirado no fluxo Utils.TP() do EHR.
/// O host atualiza seu CustomNetworkTransform e envia o RPC SnapTo bruto.
/// </summary>
public static class TeleporteAPI
{
    private static readonly Dictionary<byte, float> ultimoTeleporte = new();
    private static readonly object sincronizacao = new();
    private static int chamadasNestaPartida;

    // Proteção local contra chamadas repetidas para o mesmo jogador.
    // Não representa um limite oficial do servidor/Among Us.
    private const float INTERVALO_MINIMO_POR_JOGADOR = 0.20f;

    public static bool Teleportar(PlayerControl jogador, Vector2 destino)
    {
        if (jogador == null || jogador.NetTransform == null ||
            jogador.Data == null || jogador.Data.Disconnected ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        try
        {
            var nt = jogador.NetTransform;
            byte playerId = jogador.PlayerId;
            float agora = Time.realtimeSinceStartup;

            lock (sincronizacao)
            {
                if (ultimoTeleporte.TryGetValue(playerId, out float anterior) &&
                    agora - anterior < INTERVALO_MINIMO_POR_JOGADOR)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[TeleporteAPI] Teleporte ignorado por frequência: PlayerId={playerId}.");
                    return false;
                }

                ultimoTeleporte[playerId] = agora;
            }

            // Atualiza a cópia local do host com uma sequência adiantada,
            // seguindo o padrão utilizado pelo EHR.
            ushort sequenciaLocal = (ushort)(nt.lastSequenceId + 328);
            nt.SnapTo(destino, sequenciaLocal);
            nt.SetDirtyBit(uint.MaxValue);

            // Envia manualmente o RPC nativo SnapTo para os outros clientes.
            // A sequência de rede é separada da sequência aplicada localmente.
            ushort sequenciaRede = (ushort)(nt.lastSequenceId + 8);
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                nt.NetId,
                (byte)RpcCalls.SnapTo,
                SendOption.Reliable);

            NetHelpers.WriteVector2(destino, writer);
            writer.Write(sequenciaRede);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            chamadasNestaPartida++;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[TeleporteAPI] SnapTo enviado: PlayerId={playerId}, destino={destino}, " +
                $"sequenciaLocal={sequenciaLocal}, sequenciaRede={sequenciaRede}, " +
                $"chamadasNaPartida={chamadasNestaPartida}.");

            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[TeleporteAPI] Falha ao teleportar jogador: " + ex);
            return false;
        }
    }

    /// <summary>Limpa o controle de frequência ao iniciar ou terminar uma partida.</summary>
    public static void Resetar()
    {
        lock (sincronizacao)
        {
            ultimoTeleporte.Clear();
            chamadasNestaPartida = 0;
        }
    }
}

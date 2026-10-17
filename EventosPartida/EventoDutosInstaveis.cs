using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoDutosInstaveis : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static readonly HashSet<byte> alterandoDuto = new();
    public override string Nome => "Dutos instáveis: você pode sair por outro duto ou ser expulso sozinho";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null) ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Dutos instáveis] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida() { if (armado) ativo = true; }

    internal static void AoEntrarNoDuto(PlayerPhysics fisica, Vent duto)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || fisica == null || duto == null ||
            ShipStatus.Instance == null || MeetingHud.Instance != null)
            return;

        PlayerControl jogador = null;
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            if (p != null && p.MyPhysics == fisica) { jogador = p; break; }
        if (jogador == null || jogador.Data == null || jogador.Data.IsDead ||
            jogador.Data.Disconnected || alterandoDuto.Contains(jogador.PlayerId))
            return;

        // 35% de chance de expulsão imediata; 30% de sair por outro duto; 35% sem efeito.
        int sorteio = System.Random.Shared.Next(100);
        if (sorteio >= 65) return;
        try
        {
            alterandoDuto.Add(jogador.PlayerId);
            if (sorteio < 35)
            {
                fisica.RpcExitVent(duto.Id);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[F7/Dutos instáveis] PlayerId={jogador.PlayerId} foi expulso do duto.");
                return;
            }

            List<Vent> candidatos = new();
            foreach (Vent v in ShipStatus.Instance.AllVents)
                if (v != null && v.Id != duto.Id)
                    candidatos.Add(v);
            if (candidatos.Count == 0) return;
            Vent destino = candidatos[System.Random.Shared.Next(candidatos.Count)];
            fisica.RpcExitVent(duto.Id);
            Vector3 pos = destino.transform.position;
            if (jogador.NetTransform != null)
                jogador.NetTransform.RpcSnapTo(new Vector2(pos.x, pos.y));
            fisica.RpcEnterVent(destino.Id);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Dutos instáveis] PlayerId={jogador.PlayerId} saiu do duto {duto.Id} e entrou no duto {destino.Id}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Dutos instáveis] Falha: " + ex.Message);
        }
        finally { alterandoDuto.Remove(jogador.PlayerId); }
    }

    internal static void Resetar() { ativo = false; armado = false; alterandoDuto.Clear(); }
}

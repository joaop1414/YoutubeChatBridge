using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoAssassinatoDesviado : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    public override string Nome => "Ao tentar matar, o impostor pode ser teleportado para outro jogador";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null) ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Assassinato desviado] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida() { if (armado) ativo = true; }

    internal static bool AoTentarMatar(PlayerControl assassino, PlayerControl alvo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || assassino == null || alvo == null ||
            assassino.Data == null || alvo.Data == null || assassino.Data.IsDead ||
            assassino.Data.Disconnected || alvo.Data.IsDead || alvo.Data.Disconnected ||
            MeetingHud.Instance != null || ShipStatus.Instance == null)
            return false;

        // 30% de chance de cancelar o assassinato e teleportar o impostor.
        if (System.Random.Shared.Next(100) >= 30) return false;
        List<PlayerControl> candidatos = new();
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            if (p != null && p.Data != null && p.PlayerId != assassino.PlayerId &&
                !p.Data.IsDead && !p.Data.Disconnected && p.NetTransform != null &&
                !p.inMovingPlat && !p.onLadder)
                candidatos.Add(p);
        if (candidatos.Count == 0) return false;

        PlayerControl destino = candidatos[System.Random.Shared.Next(candidatos.Count)];
        try
        {
            Vector3 pos = destino.transform.position;
            assassino.NetTransform.RpcSnapTo(new Vector2(pos.x, pos.y));
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Assassinato desviado] Tentativa de PlayerId={assassino.PlayerId} cancelada; teleportado para PlayerId={destino.PlayerId}.");
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Assassinato desviado] Falha no teleporte: " + ex.Message);
            return false;
        }
    }

    internal static void Resetar() { ativo = false; armado = false; }
}

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTeleporteAleatorio : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    public override string Nome => "Teleporte aleatório";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null) return;
        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.IsDead ||
                jogador.Data.Disconnected || jogador.NetTransform == null ||
                jogador.MyPhysics == null || jogador.inMovingPlat || jogador.onLadder) continue;
            jogadores.Add(jogador);
        }
        if (jogadores.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Teleporte aleatório cancelado: são necessários pelo menos dois jogadores vivos.");
            return;
        }
        List<Vector2> destinos = jogadores.Select(p => p.GetTruePosition()).ToList();
        for (int i = destinos.Count - 1; i > 0; i--)
        {
            int j = Sorteador.Next(i + 1);
            (destinos[i], destinos[j]) = (destinos[j], destinos[i]);
        }
        int movidos = 0;
        for (int i = 0; i < jogadores.Count; i++)
        {
            try { jogadores[i].NetTransform.RpcSnapTo(destinos[i]); movidos++; }
            catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Falha ao teleportar PlayerId=" + jogadores[i].PlayerId + ": " + ex.Message); }
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Teleporte aleatório concluído; jogadores movidos=" + movidos + ".");
    }
}
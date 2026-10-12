using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal static class EventoDutos
{
    private static readonly Random Sorteador = new();
    public static void Executar(bool todos)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null) return;
        List<Vent> dutos = new();
        foreach (Vent duto in UnityEngine.Object.FindObjectsOfType<Vent>()) if (duto != null) dutos.Add(duto);
        if (dutos.Count == 0)
        { YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Evento de duto: nenhum duto encontrado no mapa."); return; }
        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && !jogador.Data.IsDead && !jogador.Data.Disconnected && !jogador.inMovingPlat && !jogador.onLadder) jogadores.Add(jogador);
        if (jogadores.Count == 0) return;
        if (!todos) { PlayerControl escolhido = jogadores[Sorteador.Next(jogadores.Count)]; jogadores.Clear(); jogadores.Add(escolhido); }
        int processados = 0;
        foreach (PlayerControl jogador in jogadores)
        {
            try
            {
                Vector2 posicao = jogador.GetTruePosition();
                Vent maisProximo = null;
                float menorDistancia = float.MaxValue;
                foreach (Vent duto in dutos)
                {
                    float distancia = Vector2.Distance(posicao, (Vector2)duto.transform.position);
                    if (distancia < menorDistancia) { menorDistancia = distancia; maisProximo = duto; }
                }
                if (maisProximo == null || jogador.MyPhysics == null) continue;
                // A sequência de entrada/saída tenta forçar a animação mesmo quando o jogador não estava num duto.
                jogador.MyPhysics.RpcEnterVent(maisProximo.Id);
                jogador.MyPhysics.RpcExitVent(maisProximo.Id);
                processados++;
            }
            catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Falha no duto para PlayerId=" + jogador.PlayerId + ": " + ex.Message); }
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Evento de duto executado; jogadores processados=" + processados + ", modo=" + (todos ? "todos" : "um aleatório") + ".");
    }
}

internal sealed class EventoDutoMaisProximoTodos : EventoVotacao
{
    public override string Nome => "Expulsar todos para o duto mais próximo";
    public void Executar() => EventoDutos.Executar(true);
}

internal sealed class EventoDutoMaisProximoAleatorio : EventoVotacao
{
    public override string Nome => "Expulsar um jogador aleatório para o duto mais próximo";
    public void Executar() => EventoDutos.Executar(false);
}

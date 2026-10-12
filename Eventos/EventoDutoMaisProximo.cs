using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal static class EventoDutos
{
    private static readonly System.Random Sorteador = new();
    private static readonly List<ExpulsaoPendente> ExpulsoesPendentes = new();

    private sealed class ExpulsaoPendente
    {
        public PlayerControl Jogador;
        public int VentId;
        public float ExecutarEm;
        public bool EntradaSolicitada;
    }

    public static void Executar(bool todos)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null) return;

        List<Vent> dutos = new();
        foreach (Vent duto in ShipStatus.Instance.AllVents)
            if (duto != null) dutos.Add(duto);

        if (dutos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Evento de duto: nenhum duto encontrado no mapa.");
            return;
        }

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.IsDead && !jogador.Data.Disconnected &&
                !jogador.inMovingPlat && !jogador.onLadder)
            {
                jogadores.Add(jogador);
            }
        }

        if (jogadores.Count == 0) return;

        if (!todos)
        {
            PlayerControl escolhido = jogadores[Sorteador.Next(jogadores.Count)];
            jogadores.Clear();
            jogadores.Add(escolhido);
        }

        int agendados = 0;
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
                    if (distancia < menorDistancia)
                    {
                        menorDistancia = distancia;
                        maisProximo = duto;
                    }
                }

                if (maisProximo == null || jogador.MyPhysics == null ||
                    jogador.NetTransform == null) continue;

                // Primeiro posiciona o personagem exatamente na entrada do duto, incluindo
                // o Offset visual do próprio Vent. Só depois pede ao jogo para entrar.
                Vector2 posicaoDuto = (Vector2)(maisProximo.transform.position + maisProximo.Offset);
                jogador.NetTransform.RpcSnapTo(posicaoDuto);

                ExpulsoesPendentes.Add(new ExpulsaoPendente
                {
                    Jogador = jogador,
                    VentId = maisProximo.Id,
                    ExecutarEm = Time.realtimeSinceStartup + 0.20f,
                    EntradaSolicitada = false
                });
                agendados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Falha ao posicionar jogador no duto para PlayerId=" + jogador.PlayerId + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento de duto agendado; jogadores=" + agendados +
            ", modo=" + (todos ? "todos" : "um aleatório") + ".");
    }

    public static void Atualizar()
    {
        if (ExpulsoesPendentes.Count == 0) return;

        float agora = Time.realtimeSinceStartup;
        for (int i = ExpulsoesPendentes.Count - 1; i >= 0; i--)
        {
            ExpulsaoPendente pendente = ExpulsoesPendentes[i];
            if (agora < pendente.ExecutarEm) continue;

            PlayerControl jogador = pendente.Jogador;
            if (jogador == null || jogador.MyPhysics == null ||
                jogador.NetTransform == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
            {
                ExpulsoesPendentes.RemoveAt(i);
                continue;
            }

            try
            {
                if (!pendente.EntradaSolicitada)
                {
                    // A entrada é uma etapa separada para o SnapTo chegar antes do RPC de vent.
                    jogador.MyPhysics.RpcEnterVent(pendente.VentId);
                    pendente.EntradaSolicitada = true;
                    pendente.ExecutarEm = agora + 0.40f;

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "RpcEnterVent executado para PlayerId=" + jogador.PlayerId +
                        ", ventId=" + pendente.VentId + ".");
                    continue;
                }

                // Só expulsa depois de dar tempo ao jogo para registrar a entrada no sistema de dutos.
                jogador.MyPhysics.RpcBootFromVent(pendente.VentId);
                ExpulsoesPendentes.RemoveAt(i);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "RpcBootFromVent executado para PlayerId=" + jogador.PlayerId +
                    ", ventId=" + pendente.VentId + ".");
            }
            catch (Exception ex)
            {
                ExpulsoesPendentes.RemoveAt(i);
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Falha no evento de duto para PlayerId=" + jogador.PlayerId + ": " + ex.Message);
            }
        }
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

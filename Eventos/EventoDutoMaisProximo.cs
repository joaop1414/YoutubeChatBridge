using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal static class EventoDutos
{
    private static readonly System.Random Sorteador = new();

    public static void Executar(bool todos, bool dutoAleatorio = false)
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

        int executados = 0;
        foreach (PlayerControl jogador in jogadores)
        {
            try
            {
                if (jogador.MyPhysics == null) continue;

                Vent dutoEscolhido = null;

                if (dutoAleatorio)
                {
                    dutoEscolhido = dutos[Sorteador.Next(dutos.Count)];
                }
                else
                {
                    Vector2 posicao = jogador.GetTruePosition();
                    float menorDistancia = float.MaxValue;

                    foreach (Vent duto in dutos)
                    {
                        if (duto == null) continue;

                        float distancia = Vector2.Distance(
                            posicao,
                            (Vector2)duto.transform.position
                        );

                        if (distancia < menorDistancia)
                        {
                            menorDistancia = distancia;
                            dutoEscolhido = duto;
                        }
                    }
                }

                if (dutoEscolhido == null) continue;

                // Usar apenas RpcBootFromVent, como o teleporte para dutos do Hydra.
                // Não enviar RpcSnapTo nem RpcEnterVent antes: essa sequência fazia o jogo
                // assumir a entrada no duto e podia deslocar o controle/expulsar o jogador
                // em uma posição incorreta, como se saísse do chão.
                jogador.MyPhysics.RpcBootFromVent(dutoEscolhido.Id);
                executados++;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "RpcBootFromVent enviado; PlayerId=" + jogador.PlayerId +
                    ", ventId=" + dutoEscolhido.Id + "."
                );
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Falha no evento de duto para PlayerId=" + jogador.PlayerId + ": " + ex.Message
                );
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento de duto concluído; jogadores=" + executados +
            ", modo=" + (todos ? "todos" : "um aleatório") +
            ", seleção do duto=" + (dutoAleatorio ? "aleatória" : "mais próximo") + "."
        );
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

internal sealed class EventoDutoAleatorioTodos : EventoVotacao
{
    public override string Nome => "Expulsar todos por um duto aleatório";
    public void Executar() => EventoDutos.Executar(true, true);
}

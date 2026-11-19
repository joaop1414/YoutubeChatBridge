using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTrocarPosicoesTodos : EventoVotacao
{
    private static readonly System.Random Sorteador = new();

    public override string Nome => "Todos trocam de posição aleatoriamente";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null)
            return;

        List<PlayerControl> jogadores = new();
        List<Vector2> posicoes = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null || jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.NetTransform == null || jogador.MyPhysics == null ||
                jogador.inVent || jogador.inMovingPlat || jogador.onLadder)
                continue;

            jogadores.Add(jogador);
            posicoes.Add(jogador.GetTruePosition());
        }

        if (jogadores.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Troca geral cancelada: são necessários pelo menos dois jogadores livres.");
            return;
        }

        // Embaralha as posições, não os jogadores, para capturar todos os destinos
        // antes do primeiro SnapTo e evitar que um jogador receba uma posição já alterada.
        for (int i = posicoes.Count - 1; i > 0; i--)
        {
            int j = Sorteador.Next(i + 1);
            (posicoes[i], posicoes[j]) = (posicoes[j], posicoes[i]);
        }

        if (posicoes.Count > 1)
        {
            bool iguais = true;
            for (int i = 0; i < posicoes.Count; i++)
            {
                if (Vector2.Distance(jogadores[i].GetTruePosition(), posicoes[i]) > 0.05f)
                {
                    iguais = false;
                    break;
                }
            }

            if (iguais)
                (posicoes[0], posicoes[1]) = (posicoes[1], posicoes[0]);
        }

        int movidos = SnapToBatchSender.Enviar(jogadores, posicoes);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Troca geral de posições concluída: " + movidos + " jogador(es) movimentado(s).");
    }
}

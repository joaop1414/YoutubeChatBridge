using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoReportarCorpo : EventoVotacao
{
    public override string Nome => "Reportar um jogador vivo pelo chat do YouTube";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O chat sorteou um reporte, mas já existe uma reunião em andamento.");
            return;
        }

        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.IsDead && !jogador.Data.Disconnected)
            {
                vivos.Add(jogador);
            }
        }

        if (vivos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "Não há jogadores vivos para realizar o reporte.");
            return;
        }

        // Primeiro, procura um corpo real no mapa.
        DeadBody[] corpos = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        DeadBody corpoDisponivel = null;

        foreach (DeadBody corpo in corpos)
        {
            if (corpo != null && corpo.enabled && corpo.gameObject != null &&
                corpo.gameObject.activeInHierarchy)
            {
                corpoDisponivel = corpo;
                break;
            }
        }

        PlayerControl reporter = vivos[new Random().Next(vivos.Count)];

        try
        {
            if (corpoDisponivel != null)
            {
                // Existe corpo: o jogador aleatório reporta o corpo real.
                NetworkedPlayerInfo dadosCorpo = null;
                foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
                {
                    if (jogador != null && jogador.Data != null &&
                        jogador.PlayerId == corpoDisponivel.ParentId)
                    {
                        dadosCorpo = jogador.Data;
                        break;
                    }
                }

                if (dadosCorpo == null)
                {
                    YouTubeVoteBridgePlugin.Anunciar(
                        "Encontrei um corpo, mas não consegui obter os dados do jogador morto.");
                    return;
                }

                reporter.ReportDeadBody(dadosCorpo);
                YouTubeVoteBridgePlugin.Anunciar(
                    $"{reporter.Data.PlayerName} reportou um corpo por causa do voto do YouTube!");
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Evento de reporte: reporter={reporter.Data.PlayerName}, corpo={dadosCorpo.PlayerName}.");
            }
            else
            {
                // Sem corpo: escolhe um jogador aleatório e tenta fazê-lo
                // reportar a si próprio (1 reportou 1).
                reporter.ReportDeadBody(reporter.Data);
                YouTubeVoteBridgePlugin.Anunciar(
                    $"{reporter.Data.PlayerName} tentou se auto-reportar (jogador reportou a si mesmo), pois não havia corpos no mapa!");
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Evento de auto-reporte sem corpos: jogador={reporter.Data.PlayerName} (PlayerId={reporter.PlayerId}).");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha no evento de reporte do YouTube: " + ex);
            YouTubeVoteBridgePlugin.Anunciar(
                "O jogo recusou ou não conseguiu processar o reporte. Confira o log do mod.");
        }
    }
}

using System;
using System.Collections.Generic;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoReportarCorpo : EventoVotacao
{
    public override string Nome => "Reportar corpo com aviso do YouTube";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O chat sorteou reportar um corpo, mas uma reunião já está em andamento.");
            return;
        }

        var corpos = UnityEngine.Object.FindObjectsOfType<DeadBody>();
        try
        {
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Não foi possível localizar corpos para o evento: " + ex);
            YouTubeVoteBridgePlugin.Anunciar(
                "Não consegui localizar um corpo para reportar.");
            return;
        }

        List<NetworkedPlayerInfo> candidatos = new();
        foreach (DeadBody corpo in corpos)
        {
            if (corpo == null)
                continue;

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador != null && jogador.Data != null &&
                    jogador.PlayerId == corpo.ParentId && jogador.Data.IsDead)
                {
                    candidatos.Add(jogador.Data);
                    break;
                }
            }
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O chat sorteou reportar um corpo, mas não há corpos reportáveis no mapa.");
            return;
        }

        PlayerControl reporter = null;
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local != null && local.Data != null && !local.Data.IsDead)
            reporter = local;

        if (reporter == null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador != null && jogador.Data != null && !jogador.Data.IsDead)
                {
                    reporter = jogador;
                    break;
                }
            }
        }

        if (reporter == null)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "Não há jogadores vivos que possam reportar o corpo.");
            return;
        }

        NetworkedPlayerInfo alvo = candidatos[new Random().Next(candidatos.Count)];
        try
        {
            reporter.ReportDeadBody(alvo);
            YouTubeVoteBridgePlugin.Anunciar(
                "O chat do YouTube reportou um corpo!");
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Evento de reportar corpo: reporter={reporter.Data.PlayerName}, corpo={alvo.PlayerName}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao reportar corpo pelo evento do YouTube: " + ex);
            YouTubeVoteBridgePlugin.Anunciar(
                "Falha ao reportar o corpo. Confira o log do mod.");
        }
    }
}

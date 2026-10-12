using System;
using System.Collections.Generic;

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
                "O chat sorteou o reporte de um jogador vivo, mas já existe uma reunião em andamento.");
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

        if (vivos.Count < 2)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O chat tentou reportar um jogador vivo, mas são necessários pelo menos dois jogadores vivos.");
            return;
        }

        PlayerControl reporter = PlayerControl.LocalPlayer;
        if (reporter == null || reporter.Data == null ||
            reporter.Data.IsDead || reporter.Data.Disconnected)
        {
            reporter = vivos[0];
        }

        List<PlayerControl> alvos = vivos.FindAll(p => p.PlayerId != reporter.PlayerId);
        if (alvos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "Não encontrei outro jogador vivo para ser o alvo do reporte.");
            return;
        }

        PlayerControl alvo = alvos[new Random().Next(alvos.Count)];

        try
        {
            // Passa os dados de um jogador VIVO como alvo do reporte.
            // O jogo pode validar esse estado e rejeitar o reporte; isso será
            // registrado no log para facilitar o teste na versão atual.
            reporter.ReportDeadBody(alvo.Data);

            YouTubeVoteBridgePlugin.Anunciar(
                $"O chat tentou reportar {alvo.Data.PlayerName}, que ainda está vivo!");
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Reporte de jogador vivo solicitado pelo evento do YouTube: reporter={reporter.Data.PlayerName}, alvo={alvo.Data.PlayerName} (PlayerId={alvo.PlayerId}).");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao tentar reportar um jogador vivo pelo evento do YouTube: " + ex);
            YouTubeVoteBridgePlugin.Anunciar(
                "O jogo recusou ou não conseguiu processar o reporte do jogador vivo. Confira o log do mod.");
        }
    }
}

using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoReportarCorpo : EventoVotacao
{
    public override string Nome => "Reportar um corpo";

    private static readonly Random Sorteador = new();

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Evento de reporte ignorado: já existe uma reunião em andamento.");
            return;
        }

        try
        {
            List<PlayerControl> jogadores = new();

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected)
                    continue;

                // Inclui jogadores vivos e mortos: ambos podem ser sorteados.
                jogadores.Add(jogador);
            }

            if (jogadores.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Evento de reporte cancelado: não há jogadores disponíveis.");
                return;
            }

            PlayerControl escolhido = jogadores[Sorteador.Next(jogadores.Count)];

            // O evento pede que o jogador sorteado reporte a si próprio.
            // Não exige que exista um corpo no mapa nem que o sorteado esteja vivo.
            escolhido.ReportDeadBody(escolhido.Data);

            // Informa cada cliente usando o mesmo caminho das respostas de
            // comando: título temporário YouTubeChatBridge via GameDataTo.
            // Assim a mensagem não depende de o cliente ter este mod instalado.
            const string avisoMod = "O reporte deste corpo foi acionado pelo mod.";

            foreach (PlayerControl destinatario in PlayerControl.AllPlayerControls)
            {
                if (destinatario == null || destinatario.Data == null ||
                    destinatario.Data.Disconnected || destinatario.notRealPlayer)
                    continue;

                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(
                    destinatario, avisoMod);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Evento de autorreporte solicitado pelo mod. Jogador=" +
                escolhido.Data.PlayerName + ", PlayerId=" +
                escolhido.PlayerId + ", morto=" +
                escolhido.Data.IsDead + ". Aviso enviado aos clientes.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha no evento de autorreporte do YouTube: " + ex);
        }
    }
}

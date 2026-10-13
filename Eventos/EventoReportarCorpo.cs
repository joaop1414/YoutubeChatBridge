using System;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoReportarCorpo : EventoVotacao
{
    public override string Nome => "Reportar um corpo";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        // Não tenta abrir outro reporte se uma reunião já estiver aberta.
        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Evento de reporte ignorado: já existe uma reunião em andamento.");
            return;
        }

        PlayerControl host = PlayerControl.LocalPlayer;
        if (host == null || host.Data == null ||
            host.Data.IsDead || host.Data.Disconnected)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Evento de reporte cancelado: o host local não está vivo.");
            return;
        }

        try
        {
            DeadBody corpoDisponivel = null;
            DeadBody[] corpos = UnityEngine.Object.FindObjectsOfType<DeadBody>();

            foreach (DeadBody corpo in corpos)
            {
                if (corpo != null && corpo.enabled && corpo.gameObject != null &&
                    corpo.gameObject.activeInHierarchy)
                {
                    corpoDisponivel = corpo;
                    break;
                }
            }

            // Sem corpo real, não fazemos auto-reporte nem reunião de emergência:
            // essas alternativas causaram softlock em testes anteriores.
            if (corpoDisponivel == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Evento de reporte não executado: não há corpo ativo no mapa.");
                return;
            }

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

            if (dadosCorpo == null || !dadosCorpo.IsDead || dadosCorpo.Disconnected)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Evento de reporte cancelado: os dados do corpo não correspondem a um jogador morto válido.");
                return;
            }

            // Executar pelo host vivo e reportar apenas um corpo existente.
            host.ReportDeadBody(dadosCorpo);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Evento de reporte solicitado pelo host. Reporter=" +
                host.Data.PlayerName + ", corpo=" + dadosCorpo.PlayerName +
                ", PlayerId=" + corpoDisponivel.ParentId + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha no evento de reporte do YouTube: " + ex);
        }
    }
}

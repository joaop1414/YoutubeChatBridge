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
            return;
        }

        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.IsDead && !jogador.Data.Disconnected)
                vivos.Add(jogador);
        }

        if (vivos.Count == 0)
        {
            return;
        }

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

        PlayerControl host = PlayerControl.LocalPlayer;
        if (host == null || host.Data == null || host.Data.IsDead || host.Data.Disconnected)
        {
            return;
        }

        try
        {
            if (corpoDisponivel != null)
            {
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

                if (dadosCorpo == null || !dadosCorpo.IsDead)
                {
                    return;
                }

                // ReportDeadBody é uma ação de rede sujeita à autoridade do cliente.
                // Executar pelo jogador local do host evita fingir que outro cliente
                // enviou um RPC que não foi realmente enviado por ele.
                host.ReportDeadBody(dadosCorpo);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Evento de reporte: reporter local do host={host.Data.PlayerName}, corpo={dadosCorpo.PlayerName}.");
            }
            else
            {
                // Primeiro tenta o comportamento solicitado: um jogador vivo
                // aleatório tenta reportar a si mesmo (1 reportou 1).
                PlayerControl escolhido = vivos[new System.Random().Next(vivos.Count)];
                escolhido.ReportDeadBody(escolhido.Data);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Tentativa de auto-reporte sem corpo: jogador={escolhido.Data.PlayerName}, PlayerId={escolhido.PlayerId}.");

                // ReportDeadBody com alvo vivo pode ser recusado pelo jogo vanilla.
                // Se a chamada não abriu uma reunião imediatamente, usa o mecanismo
                // de emergência documentado no EHR para não deixar o evento sem efeito.
                if (MeetingHud.Instance == null)
                {
                    host.ReportDeadBody(null);
                }
                else
                {
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha no evento de reporte do YouTube: " + ex);
        }
    }
}

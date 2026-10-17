using System;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTeletransportarAlvoParaMetamorfo : EventoVotacao
{
    private static bool ativo;

    public override string Nome => "Alvo da metamorfose é teleportado para o metamorfo";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento ativado: quando alguém tentar se metamorfosear, o alvo será teleportado até o metamorfo. O evento termina na próxima reunião.");
    }

    public static void AoTentarMetamorfosear(PlayerControl metamorfo, PlayerControl alvo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            metamorfo == null || alvo == null ||
            metamorfo.PlayerId == alvo.PlayerId ||
            metamorfo.Data == null || alvo.Data == null ||
            metamorfo.Data.IsDead || metamorfo.Data.Disconnected ||
            alvo.Data.IsDead || alvo.Data.Disconnected ||
            metamorfo.NetTransform == null || alvo.NetTransform == null ||
            metamorfo.inMovingPlat || metamorfo.onLadder ||
            alvo.inMovingPlat || alvo.onLadder)
            return;

        try
        {
            Vector2 destino = metamorfo.GetTruePosition();
            alvo.NetTransform.RpcSnapTo(destino);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Evento metamorfose: alvo {alvo.Data.PlayerName} (PlayerId={alvo.PlayerId}) teleportado para o metamorfo {metamorfo.Data.PlayerName} (PlayerId={metamorfo.PlayerId}).");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao teleportar o alvo da metamorfose para o metamorfo: " + ex.Message);
        }
    }

    public static void Desativar()
    {
        if (!ativo)
            return;

        ativo = false;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento de teleporte do alvo da metamorfose encerrado na reunião.");
    }
}

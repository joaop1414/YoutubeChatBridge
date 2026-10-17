using System;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTeletransportarAlvoParaMetamorfoPartida : EventoVotacao
{
    private static bool ativo;
    private static bool armado;

    public override string Nome => "Alvo da metamorfose é teleportado para o metamorfo (partida inteira)";
    internal static bool EstaAtivo => ativo;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (LobbyTagManager.PartidaIniciada()) ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Metamorfo/partida inteira] Ativado: toda tentativa de metamorfose teleporta o alvo para o metamorfo e rejeita a transformação até o fim da partida.");
    }

    internal static void AoTentarMetamorfosear(PlayerControl metamorfo, PlayerControl alvo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || metamorfo == null || alvo == null ||
            metamorfo.PlayerId == alvo.PlayerId || metamorfo.Data == null || alvo.Data == null ||
            metamorfo.Data.IsDead || metamorfo.Data.Disconnected || alvo.Data.IsDead ||
            alvo.Data.Disconnected || metamorfo.NetTransform == null || alvo.NetTransform == null ||
            metamorfo.inMovingPlat || metamorfo.onLadder || alvo.inMovingPlat || alvo.onLadder)
            return;

        try
        {
            Vector3 p = metamorfo.transform.position;
            alvo.NetTransform.RpcSnapTo(new Vector2(p.x, p.y));
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Metamorfo/partida inteira] Alvo PlayerId={alvo.PlayerId} teleportado para metamorfo PlayerId={metamorfo.PlayerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Metamorfo/partida inteira] Falha no teleporte: " + ex.Message);
        }
    }

    internal static void IniciarPartida()
    {
        if (armado) ativo = true;
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
    }
}

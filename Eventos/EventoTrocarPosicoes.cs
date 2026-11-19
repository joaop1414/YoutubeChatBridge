using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTrocarPosicoes : EventoVotacao
{
    private static readonly System.Random Sorteador = new();

    public override string Nome => "Trocar posição de dois jogadores";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null)
            return;

        List<PlayerControl> candidatos = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.NetTransform == null || jogador.MyPhysics == null ||
                jogador.inMovingPlat || jogador.onLadder)
                continue;

            candidatos.Add(jogador);
        }

        if (candidatos.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Troca de posições cancelada: são necessários pelo menos dois jogadores vivos.");
            return;
        }

        int indiceA = Sorteador.Next(candidatos.Count);
        PlayerControl jogadorA = candidatos[indiceA];
        candidatos.RemoveAt(indiceA);
        PlayerControl jogadorB = candidatos[Sorteador.Next(candidatos.Count)];

        try
        {
            // Captura as duas posições antes de mover qualquer jogador.
            Vector2 posicaoA = jogadorA.GetTruePosition();
            Vector2 posicaoB = jogadorB.GetTruePosition();

            // Os dois SnapTo seguem no mesmo GameData externo, no padrão de empacotamento do EHR.
            int enviados = SnapToBatchSender.Enviar(
                new List<PlayerControl> { jogadorA, jogadorB },
                new List<Vector2> { posicaoB, posicaoA });

            if (enviados != 2)
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Troca de posições: apenas {enviados}/2 SnapTo foram enviados.");

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Troca de posições executada: {jogadorA.Data.PlayerName} (PlayerId={jogadorA.PlayerId}) " +
                $"<-> {jogadorB.Data.PlayerName} (PlayerId={jogadorB.PlayerId}).");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao trocar posições dos jogadores: " + ex);
        }
    }
}

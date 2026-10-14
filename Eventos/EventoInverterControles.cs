using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoInverterControles : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private static readonly Random Sorteador = new();

    public override string Nome =>
        "Inverter os controles de um jogador aleatório por 15 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null ||
                jogador.Data.IsDead ||
                jogador.Data.Disconnected)
                continue;

            candidatos.Add(jogador);
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Inversão de controles cancelada: nenhum jogador vivo disponível.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];

        EventoVelocidadeJogadores.AplicarMultiplicador(
            alvo.PlayerId, -1f, DuracaoSegundos);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Controles invertidos para " + alvo.Data.PlayerName +
            " (PlayerId=" + alvo.PlayerId + ", OwnerId=" + alvo.OwnerId +
            ") por " + DuracaoSegundos + " segundos.");
    }
}

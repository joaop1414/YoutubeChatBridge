using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoInverterMovimentoAleatorio : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private static readonly Random Sorteador = new();
    public override string Nome => "Um jogador aleatório fica com movimento invertido por 15 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador?.Data != null && !jogador.Data.IsDead && !jogador.Data.Disconnected)
                candidatos.Add(jogador);

        if (candidatos.Count == 0) return;
        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        EventoVelocidadeJogadores.AplicarMultiplicador(alvo.PlayerId, -1f, DuracaoSegundos);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Movimento invertido: " + alvo.Data.PlayerName + " (PlayerId=" + alvo.PlayerId +
            ") por " + DuracaoSegundos + " segundos.");
    }
}
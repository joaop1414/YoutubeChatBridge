using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoDesconexaoFalsa : EventoVotacao
{
    public override string Nome => "Simular desconexão por erro (ninguém sai)";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> jogadores = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null ||
                jogador.Data == null ||
                jogador.Data.Disconnected)
                continue;

            jogadores.Add(jogador);
        }

        if (jogadores.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Evento de desconexão falsa: não há jogadores disponíveis.");
            return;
        }

        PlayerControl escolhido = jogadores[new Random().Next(jogadores.Count)];
        YouTubeVoteBridgePlugin.SimularDesconexaoPorErro(escolhido);
    }
}

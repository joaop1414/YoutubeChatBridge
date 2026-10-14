using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoSuicidioAleatorio : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    public override string Nome => "Um jogador aleatório comete suicídio";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && !jogador.Data.IsDead && !jogador.Data.Disconnected)
                candidatos.Add(jogador);

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Suicídio aleatório cancelado: não há jogadores vivos.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        try
        {
            alvo.RpcMurderPlayer(alvo, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Suicídio aleatório solicitado para " + alvo.Data.PlayerName + " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao executar suicídio aleatório: " + ex);
        }
    }
}
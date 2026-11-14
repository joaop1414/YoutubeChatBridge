using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoVotoDuplo : EventoVotacao
{
    private static bool ativo;
    private static byte jogadorSelecionado = byte.MaxValue;

    public override string Nome => "Um jogador aleatório terá 2 votos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();

        if (PlayerControl.AllPlayerControls != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null ||
                    jogador.Data == null ||
                    jogador.Data.IsDead ||
                    jogador.Data.Disconnected ||
                    jogador.notRealPlayer)
                    continue;

                candidatos.Add(jogador);
            }
        }

        if (candidatos.Count == 0)
        {
            ativo = false;
            jogadorSelecionado = byte.MaxValue;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Voto duplo] Não havia jogadores vivos e conectados para sortear.");
            return;
        }

        PlayerControl escolhido = candidatos[System.Random.Shared.Next(candidatos.Count)];
        jogadorSelecionado = escolhido.PlayerId;
        ativo = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Voto duplo] {escolhido.Data.PlayerName} (PlayerId={jogadorSelecionado}) terá dois votos em cada reunião enquanto estiver vivo.");
    }

    internal static bool TentarObterJogador(out byte playerId)
    {
        playerId = jogadorSelecionado;
        return ativo && jogadorSelecionado != byte.MaxValue;
    }

    internal static void Resetar()
    {
        ativo = false;
        jogadorSelecionado = byte.MaxValue;
    }
}

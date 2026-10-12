using System;
using System.Collections.Generic;
using System.Linq;

namespace YouTubeVoteBridge;

internal sealed class EventoTrocarCores : EventoVotacao
{
    private static readonly System.Random Sorteador = new();

    public override string Nome => "Trocar cores de jogadores";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
        {
            if (p != null && p.Data != null && !p.Data.Disconnected)
                jogadores.Add(p);
        }

        if (jogadores.Count < 2)
            return;

        // ColorId é int nesta versão das bibliotecas do Among Us.
        List<int> cores = jogadores.Select(p => p.Data.DefaultOutfit.ColorId).ToList();
        List<int> embaralhadas = new(cores);
        for (int i = embaralhadas.Count - 1; i > 0; i--)
        {
            int j = Sorteador.Next(i + 1);
            (embaralhadas[i], embaralhadas[j]) = (embaralhadas[j], embaralhadas[i]);
        }

        if (embaralhadas.SequenceEqual(cores))
            embaralhadas = embaralhadas.Skip(1).Concat(embaralhadas.Take(1)).ToList();

        for (int i = 0; i < jogadores.Count; i++)
        {
            try
            {
                jogadores[i].RpcSetColor((byte)embaralhadas[i]);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível trocar a cor de " + jogadores[i].Data.PlayerName + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Evento executado: troca de cores.");
    }
}

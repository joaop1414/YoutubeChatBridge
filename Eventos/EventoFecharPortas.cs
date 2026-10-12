using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoFecharPortas : EventoVotacao
{
    private static readonly Random Sorteador = new();

    public override string Nome => "Fechar as portas";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null)
            return;

        // Escolhe uma sala por sorteio. Salas sem portas no mapa atual
        // são ignoradas pelo próprio jogo.
        SystemTypes[] salas =
        {
            SystemTypes.Cafeteria,
            SystemTypes.Storage,
            SystemTypes.Electrical,
            SystemTypes.MedBay,
            SystemTypes.Security,
            SystemTypes.Weapons,
            SystemTypes.Navigation,
            SystemTypes.Admin,
            SystemTypes.Comms,
            SystemTypes.UpperEngine,
            SystemTypes.LowerEngine,
            SystemTypes.Shields
        };

        // Tenta algumas salas para aumentar a chance de acertar uma
        // que tenha portas no mapa atual.
        for (int tentativa = 0; tentativa < 4; tentativa++)
        {
            SystemTypes sala = salas[Sorteador.Next(salas.Length)];

            try
            {
                ShipStatus.Instance.CloseDoorsOfType(sala);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Evento executado: fechamento de portas na sala " + sala + ".");
                return;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível fechar as portas de " + sala + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "O evento de fechar portas não conseguiu executar em nenhuma sala sorteada.");
    }
}

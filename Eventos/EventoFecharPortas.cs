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

        // Fecha portas em até quatro salas sorteadas. Isso também aumenta
        // a chance de afetar salas que realmente existam no mapa atual.
        HashSet<SystemTypes> escolhidas = new();

        while (escolhidas.Count < 4 && escolhidas.Count < salas.Length)
            escolhidas.Add(salas[Sorteador.Next(salas.Length)]);

        int executadas = 0;
        foreach (SystemTypes sala in escolhidas)
        {
            try
            {
                ShipStatus.Instance.CloseDoorsOfType(sala);
                executadas++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível fechar as portas de " + sala + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento executado: tentativa de fechar portas em " + executadas + " sala(s).");
    }
}

using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoFecharPortaAleatoria : EventoVotacao
{
    private static readonly Random Sorteador = new();

    public override string Nome => "Fechar uma porta aleatória";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null)
            return;

        try
        {
            List<OpenableDoor> portasDisponiveis = new();

            foreach (OpenableDoor porta in ShipStatus.Instance.AllDoors)
            {
                if (porta != null)
                    portasDisponiveis.Add(porta);
            }

            if (portasDisponiveis.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Evento de porta aleatória: nenhuma porta encontrada neste mapa.");
                return;
            }

            OpenableDoor escolhida =
                portasDisponiveis[Sorteador.Next(portasDisponiveis.Count)];

            // Fecha a porta selecionada. A sincronização será validada em jogo.
            escolhida.SetDoorway(false);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Evento executado: porta aleatória selecionada. Id=" +
                escolhida.Id + ", sala=" + escolhida.Room + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao fechar uma porta aleatória: " + ex);
        }
    }
}

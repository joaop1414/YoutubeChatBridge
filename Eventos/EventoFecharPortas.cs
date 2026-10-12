using System;

namespace YouTubeVoteBridge;

internal sealed class EventoFecharPortas : EventoVotacao
{
    public override string Nome => "Fechar todas as portas";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null)
            return;

        // Tenta fechar as portas de todas as salas conhecidas pelos mapas
        // clássicos. Salas inexistentes no mapa atual são ignoradas pelo jogo.
        SystemTypes[] salas =
        {
            SystemTypes.Cafeteria,
            SystemTypes.Storage,
            SystemTypes.Electrical,
            SystemTypes.MedBay,
            SystemTypes.Security,
            SystemTypes.Weapons,
            SystemTypes.Admin,
            SystemTypes.Comms,
            SystemTypes.UpperEngine,
            SystemTypes.LowerEngine,
            SystemTypes.Shields
        };

        int tentativas = 0;

        foreach (SystemTypes sala in salas)
        {
            try
            {
                ShipStatus.Instance.CloseDoorsOfType(sala);
                tentativas++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível fechar as portas de " + sala + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento executado: solicitações de fechamento enviadas para " +
            tentativas + " salas.");
    }
}

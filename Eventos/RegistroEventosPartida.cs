using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal static class RegistroEventosPartida
{
    // Eventos especiais selecionáveis pela votação do YouTube no lobby.
    private static readonly IReadOnlyList<EventoVotacao> Eventos = new EventoVotacao[]
    {
        new EventoTeletransportarAlvoParaMetamorfoPartida(),
        new EventoTeletransportarJogadorAoCompletarTarefa(),
    };

    public static IReadOnlyList<EventoVotacao> ObterTodos() => Eventos;
}

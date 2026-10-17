using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal static class RegistroEventosPartida
{
    // Eventos especiais selecionáveis pela votação do YouTube no lobby.
    private static readonly IReadOnlyList<EventoVotacao> Eventos = new EventoVotacao[]
    {
        new EventoOportunista()
    };

    public static IReadOnlyList<EventoVotacao> ObterTodos() => Eventos;
}

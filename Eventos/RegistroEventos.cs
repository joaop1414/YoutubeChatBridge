using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal static class RegistroEventos
{
    // Para cadastrar um evento novo, adicione sua classe nesta lista.
    private static readonly IReadOnlyList<EventoVotacao> Eventos = new EventoVotacao[]
    {
        new EventoSupervelocidade(),
        new EventoVelocidadeReduzida(),
        new EventoCongelarJogador(),
        new EventoTrocarPosicoes(),
        new EventoSabotagemAleatoria(),
        new EventoFecharPortas(),
        new EventoFecharPortaAleatoria(),
        new EventoDutoMaisProximoTodos(),
        new EventoDutoMaisProximoAleatorio(),
        new EventoTarefaMataJogador(),
        new EventoTarefaSuicidio(),
        new EventoTrocarCores(),
        new EventoEscudoTemporario(),
        new EventoCriarCorpoFalso()
    };

    public static IReadOnlyList<EventoVotacao> ObterTodos() => Eventos;
}

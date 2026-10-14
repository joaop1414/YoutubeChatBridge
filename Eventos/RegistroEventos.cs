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
        new EventoTrocarPosicoesTodos(),
        new EventoSabotagemAleatoria(),
        new EventoFecharPortas(),
        new EventoFecharPortaAleatoria(),
        new EventoDutoMaisProximoTodos(),
        new EventoDutoAleatorioTodos(),
        new EventoDutoAleatorioAleatorio(),
        new EventoDutoMaisProximoAleatorio(),
        new EventoTarefaMataJogador(),
        new EventoTarefaSuicidio(),
        new EventoTrocarCores(),
        new EventoTrocarIdentidades(),
        new EventoEscudoTemporario(),
        new EventoCriarCorpoFalso(),
        new EventoNpcSeguidor(),
        new EventoTsunami(),
        new EventoReportarCorpo(),
        new EventoInverterControles(),
        new EventoInverterMovimentoTodos(),
        new EventoTeleporteAleatorio(),
        new EventoSuicidioAleatorio(),
        new EventoVisaoReduzida()
    };

    public static IReadOnlyList<EventoVotacao> ObterTodos() => Eventos;
}

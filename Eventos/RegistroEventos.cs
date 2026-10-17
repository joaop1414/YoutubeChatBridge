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
        new EventoTodosFicamInvisiveis(),
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
        new EventoTsunami(),
        new EventoReportarCorpo(),
        new EventoInverterControles(),
        new EventoInverterMovimentoTodos(),
        new EventoTeleporteAleatorio(),
        new EventoTeletransportarAlvoParaMetamorfo(),
        new EventoTestarRastreadorComFantasma(),
        new EventoSuicidioAleatorio(),
        new EventoVisaoReduzida(),
        new EventoAdicionarTarefas(1, true),
        new EventoAdicionarTarefas(5, true),
        new EventoAdicionarTarefas(10, true),
        new EventoAdicionarTarefas(1, false),
        new EventoAdicionarTarefas(5, false),
        new EventoAdicionarTarefas(10, false),
        new EventoCompletarTarefa(true),
        new EventoCompletarTarefa(false)
    };

    public static IReadOnlyList<EventoVotacao> ObterTodos() => Eventos;
}

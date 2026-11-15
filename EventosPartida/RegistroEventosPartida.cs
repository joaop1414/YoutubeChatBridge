using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal static class RegistroEventosPartida
{
    // Eventos selecionáveis no lobby pelo menu F7; seus efeitos são armados
    // no lobby e ativados no início da partida.
    private static readonly IReadOnlyList<EventoVotacao> Eventos = new EventoVotacao[]
    {
        new EventoTeletransportarAlvoParaMetamorfoPartida(),
        new EventoTeletransportarJogadorAoCompletarTarefa(),
        new EventoTarefaMortal(),
        new EventoAssassinatoDesviado(),
        new EventoTrocaDeDestinos(),
        new EventoUltimoSuspiro(),
        new EventoDutosInstaveis(),
        new EventoMorrerQuandoPortaFechar(),
        new EventoRaio(),
        new EventoSeguidorMortal(),
        new EventoImpostorSuicidaAoTentarMatar(),
        new EventoVotoDuplo(),
        new EventoAnjoTraidor(),
    };

    public static IReadOnlyList<EventoVotacao> ObterTodos() => Eventos;
}

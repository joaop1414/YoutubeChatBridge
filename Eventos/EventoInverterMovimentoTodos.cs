using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoInverterMovimentoTodos : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    public override string Nome => "Todos ficam com movimento invertido por 15 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        int aplicados = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null || jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;
            EventoVelocidadeJogadores.AplicarMultiplicador(jogador.PlayerId, -1f, DuracaoSegundos);
            aplicados++;
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Movimento invertido aplicado a " + aplicados + " jogadores por " + DuracaoSegundos + " segundos.");
    }
}
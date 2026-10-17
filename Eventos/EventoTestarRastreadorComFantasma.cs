using System;
using System.Collections.Generic;
using AmongUs.GameOptions;

namespace YouTubeVoteBridge;

internal sealed class EventoTestarRastreadorComFantasma : EventoVotacao
{
    private static readonly System.Random Sorteador = new();

    public override string Nome => "Testar Rastreador após Tripulante Fantasma";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null &&
                jogador.Data != null &&
                !jogador.Data.Disconnected)
            {
                candidatos.Add(jogador);
            }
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Teste de Rastreador cancelado: não há jogadores disponíveis.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];

        try
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Teste de Rastreador: jogador escolhido: {alvo.Data.PlayerName} (PlayerId={alvo.PlayerId}).");

            // Primeiro tenta atribuir CrewmateGhost, permitindo substituir a função atual.
            alvo.RpcSetRole(RoleTypes.CrewmateGhost, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Teste de Rastreador: enviado RpcSetRole(CrewmateGhost, canOverride: true) para PlayerId={alvo.PlayerId}.");

            // Em seguida, tenta substituir a função fantasma por Tracker com a mesma permissão.
            alvo.RpcSetRole(RoleTypes.Tracker, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Teste de Rastreador: enviado RpcSetRole(Tracker, canOverride: true) para PlayerId={alvo.PlayerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha no teste CrewmateGhost -> Tracker para PlayerId={alvo.PlayerId}: {ex}");
        }
    }
}

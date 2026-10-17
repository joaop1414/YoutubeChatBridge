using System;
using System.Collections;
using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

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
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Teste EHR canOverride: jogador escolhido: {alvo.Data.PlayerName} (PlayerId={alvo.PlayerId}).");

        // EHR aplica a função localmente pela coroutine e envia o SetRole RPC
        // global com o booleano canOverride=true.
        AmongUsClient.Instance.StartCoroutine(TestarSequencia(alvo));
    }

    private static IEnumerator TestarSequencia(PlayerControl alvo)
    {
        try
        {
            EnviarRoleGlobalComoEhr(alvo, RoleTypes.CrewmateGhost);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Teste EHR: enviado SetRole global(CrewmateGhost, canOverride=true) para PlayerId={alvo.PlayerId}.");

            // Dá tempo para CoSetRole terminar e para os clientes processarem
            // o primeiro SetRole antes de mandar a segunda função.
            yield return new WaitForSeconds(0.5f);

            if (alvo == null || alvo.Data == null || alvo.Data.Disconnected)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Teste EHR cancelado: o jogador saiu antes da segunda atribuição.");
                yield break;
            }

            EnviarRoleGlobalComoEhr(alvo, RoleTypes.Tracker);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Teste EHR: enviado SetRole global(Tracker, canOverride=true) para PlayerId={alvo.PlayerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha no teste EHR CrewmateGhost -> Tracker para PlayerId={alvo?.PlayerId}: {ex}");
        }
    }

    private static void EnviarRoleGlobalComoEhr(PlayerControl alvo, RoleTypes role)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || alvo == null)
            return;

        // Equivalente ao EHR: player.SetRole(role) local + SetRole RPC global.
        // SetRole chama CoSetRole(role, canOverride), que também executa a
        // rotina interna do jogo, enquanto o RPC informa canOverride=true a todos.
        alvo.StartCoroutine(alvo.CoSetRole(role, true));

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            alvo.NetId,
            (byte)RpcCalls.SetRole,
            SendOption.Reliable);
        writer.Write((ushort)role);
        writer.Write(true);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }
}

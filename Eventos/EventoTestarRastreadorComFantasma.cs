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

        // PlayerControl, assim como no EHR, inicia a coroutine de sequência.
        alvo.StartCoroutine(TestarSequencia(alvo));
    }

    private static IEnumerator TestarSequencia(PlayerControl alvo)
    {
        EnviarRoleGlobalComoEhr(alvo, RoleTypes.CrewmateGhost);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Teste EHR: tentativa CrewmateGhost com canOverride=true para PlayerId={alvo.PlayerId}.");

        // Aguarda o jogo processar a primeira atribuição antes de tentar Tracker.
        yield return new WaitForSeconds(0.5f);

        if (alvo == null || alvo.Data == null || alvo.Data.Disconnected)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Teste EHR cancelado: o jogador saiu antes da segunda atribuição.");
            yield break;
        }

        EnviarRoleGlobalComoEhr(alvo, RoleTypes.Tracker);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Teste EHR: tentativa Tracker com canOverride=true para PlayerId={alvo.PlayerId}.");
    }

    private static void EnviarRoleGlobalComoEhr(PlayerControl alvo, RoleTypes role)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || alvo == null)
            return;

        try
        {
            // O EHR inicia a rotina local CoSetRole e também transmite SetRole
            // globalmente, com o booleano canOverride=true.
            alvo.StartCoroutine(alvo.CoSetRole(role, true));

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                alvo.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable);
            writer.Write((ushort)role);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha ao atribuir {role} com canOverride=true a PlayerId={alvo.PlayerId}: {ex}");
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTeletransportarJogadorAoCompletarTarefa : EventoVotacao
{
    private static bool ativo;
    private static bool armado;

    public override string Nome => "Ao concluir uma tarefa, um jogador aleatório é teleportado até você";
    internal static bool EstaAtivo => ativo;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (LobbyTagManager.PartidaIniciada()) ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Tarefa/teleporte] Ativado até a próxima reunião.");
    }

    internal static void AoCompletarTarefa(PlayerControl executor)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || executor == null ||
            executor.Data == null || executor.Data.IsDead || executor.Data.Disconnected ||
            executor.NetTransform == null || executor.inMovingPlat || executor.onLadder ||
            PlayerControl.AllPlayerControls == null)
            return;

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
        {
            if (p == null || p.Data == null || p.PlayerId == executor.PlayerId ||
                p.Data.IsDead || p.Data.Disconnected || p.NetTransform == null ||
                p.inMovingPlat || p.onLadder)
                continue;
            candidatos.Add(p);
        }

        if (candidatos.Count == 0) return;
        PlayerControl alvo = candidatos[System.Random.Shared.Next(candidatos.Count)];
        try
        {
            Vector3 pos = executor.transform.position;
            alvo.NetTransform.RpcSnapTo(new Vector2(pos.x, pos.y));
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Tarefa/teleporte] Após tarefa de PlayerId={executor.PlayerId}, jogador aleatório PlayerId={alvo.PlayerId} foi teleportado até ele.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Tarefa/teleporte] Falha no teleporte: " + ex.Message);
        }
    }

    internal static void IniciarPartida()
    {
        if (armado) ativo = true;
    }

    internal static void Desativar(string motivo)
    {
        if (!ativo && !armado) return;
        ativo = false;
        armado = false;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Tarefa/teleporte] Evento encerrado: " + motivo + ".");
    }
}

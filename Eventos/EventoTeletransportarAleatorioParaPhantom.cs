using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTeletransportarAleatorioParaPhantom : EventoVotacao
{
    private static bool ativo;

    public override string Nome => "Ao ficar invisível, o Phantom teleporta um jogador aleatório até ele";
    internal static bool EstaAtivo => ativo;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Phantom/teleporte] Ativado: quando um Phantom iniciar a invisibilidade, um jogador aleatório será teleportado até ele.");
    }

    internal static void AoAtivarInvisibilidade(PlayerControl phantom)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || phantom == null ||
            phantom.Data == null || phantom.Data.IsDead || phantom.Data.Disconnected ||
            phantom.NetTransform == null || PlayerControl.AllPlayerControls == null)
            return;

        // Só considera o jogador que realmente possui a função Phantom.
        string papel = phantom.Data.Role?.GetType().Name + " " + phantom.Data.Role?.ToString();
        if (string.IsNullOrEmpty(papel) ||
            papel.IndexOf("Phantom", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
        {
            if (p == null || p.Data == null || p.PlayerId == phantom.PlayerId ||
                p.Data.IsDead || p.Data.Disconnected || p.NetTransform == null ||
                p.inMovingPlat || p.onLadder)
                continue;
            candidatos.Add(p);
        }

        if (candidatos.Count == 0) return;
        PlayerControl alvo = candidatos[Random.Shared.Next(candidatos.Count)];
        try
        {
            Vector3 pos = phantom.transform.position;
            alvo.NetTransform.RpcSnapTo(new Vector2(pos.x, pos.y));
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Phantom/teleporte] PlayerId={alvo.PlayerId} teleportado para Phantom PlayerId={phantom.PlayerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Phantom/teleporte] Falha no teleporte: " + ex.Message);
        }
    }

    internal static void Resetar() => ativo = false;
}

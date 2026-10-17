using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTrocaDeDestinos : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static float proximaTroca;
    public override string Nome => "A cada 25 segundos, dois jogadores trocam de posição";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null) IniciarPartida();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Troca de destinos] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida()
    {
        if (!armado) return;
        ativo = true;
        proximaTroca = Time.realtimeSinceStartup + 25f;
    }

    internal static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null ||
            MeetingHud.Instance != null || Time.realtimeSinceStartup < proximaTroca)
            return;
        proximaTroca = Time.realtimeSinceStartup + 25f;
        List<PlayerControl> jogadores = new();
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            if (p != null && p.Data != null && !p.Data.IsDead && !p.Data.Disconnected &&
                p.NetTransform != null && !p.inMovingPlat && !p.onLadder)
                jogadores.Add(p);
        if (jogadores.Count < 2) return;

        int a = System.Random.Shared.Next(jogadores.Count);
        int b = System.Random.Shared.Next(jogadores.Count - 1);
        if (b >= a) b++;
        PlayerControl primeiro = jogadores[a];
        PlayerControl segundo = jogadores[b];
        Vector3 pa = primeiro.transform.position;
        Vector3 pb = segundo.transform.position;
        try
        {
            primeiro.NetTransform.RpcSnapTo(new Vector2(pb.x, pb.y));
            segundo.NetTransform.RpcSnapTo(new Vector2(pa.x, pa.y));
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Troca de destinos] PlayerId={primeiro.PlayerId} e PlayerId={segundo.PlayerId} trocaram de posição.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Troca de destinos] Falha: " + ex.Message);
        }
    }

    internal static void Resetar() { ativo = false; armado = false; proximaTroca = 0f; }
}

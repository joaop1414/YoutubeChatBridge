using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoInverterControles : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private static float fimEfeito;
    private static bool ativo;
    private static float velocidadeOriginal;
    private static float velocidadeFantasmaOriginal;
    public override string Nome => "Inverter os controles por 15 segundos";
    public void Executar() => Ativar();

    public static void Ativar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || PlayerControl.LocalPlayer?.MyPhysics == null) return;
        PlayerPhysics fisica = PlayerControl.LocalPlayer.MyPhysics;
        if (!ativo) { velocidadeOriginal = fisica.Speed; velocidadeFantasmaOriginal = fisica.GhostSpeed; }
        ativo = true;
        fimEfeito = Time.realtimeSinceStartup + DuracaoSegundos;
        AplicarInversao(fisica);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Controles invertidos localmente no host por " + DuracaoSegundos + " segundos.");
    }

    public static void Atualizar()
    {
        if (!ativo) return;
        PlayerPhysics fisica = PlayerControl.LocalPlayer?.MyPhysics;
        if (fisica == null) { ativo = false; return; }
        if (!YouTubeVoteBridgePlugin.EhHost() || Time.realtimeSinceStartup >= fimEfeito)
        {
            fisica.Speed = Mathf.Abs(velocidadeOriginal);
            fisica.GhostSpeed = Mathf.Abs(velocidadeFantasmaOriginal);
            ativo = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Controles do host restaurados.");
            return;
        }
        AplicarInversao(fisica);
    }

    private static void AplicarInversao(PlayerPhysics fisica)
    {
        fisica.Speed = -Mathf.Abs(fisica.Speed);
        fisica.GhostSpeed = -Mathf.Abs(fisica.GhostSpeed);
    }
}

internal sealed class EventoTeleporteAleatorio : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    public override string Nome => "Teleporte aleatório";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null) return;
        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.IsDead ||
                jogador.Data.Disconnected || jogador.NetTransform == null ||
                jogador.MyPhysics == null || jogador.inMovingPlat || jogador.onLadder) continue;
            jogadores.Add(jogador);
        }
        if (jogadores.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Teleporte aleatório cancelado: são necessários pelo menos dois jogadores vivos.");
            return;
        }
        List<Vector2> destinos = jogadores.Select(p => p.GetTruePosition()).ToList();
        for (int i = destinos.Count - 1; i > 0; i--)
        {
            int j = Sorteador.Next(i + 1);
            (destinos[i], destinos[j]) = (destinos[j], destinos[i]);
        }
        int movidos = 0;
        for (int i = 0; i < jogadores.Count; i++)
        {
            try { jogadores[i].NetTransform.RpcSnapTo(destinos[i]); movidos++; }
            catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Falha ao teleportar PlayerId=" + jogadores[i].PlayerId + ": " + ex.Message); }
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Teleporte aleatório concluído; jogadores movidos=" + movidos + ".");
    }
}

internal sealed class EventoMatarJogadorAleatorio : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    public override string Nome => "Matar um jogador aleatório";
    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || PlayerControl.LocalPlayer == null) return;
        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && !jogador.Data.IsDead && !jogador.Data.Disconnected) candidatos.Add(jogador);
        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Matar jogador aleatório cancelado: não há jogadores vivos.");
            return;
        }
        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        try
        {
            PlayerControl.LocalPlayer.RpcMurderPlayer(alvo, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Pedido de morte aleatória enviado para " + alvo.Data.PlayerName + " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao matar jogador aleatório: " + ex); }
    }
}

internal sealed class EventoReviverJogadorMorto : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    public override string Nome => "Reviver um jogador morto";
    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        List<PlayerControl> mortos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && jogador.Data.IsDead && !jogador.Data.Disconnected) mortos.Add(jogador);
        if (mortos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Reviver jogador cancelado: não há jogadores mortos elegíveis.");
            return;
        }
        PlayerControl alvo = mortos[Sorteador.Next(mortos.Count)];
        try
        {
            MethodInfo metodo = alvo.GetType().GetMethod("Revive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
            if (metodo == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("A versão atual não expõe PlayerControl.Revive() sem parâmetros; o jogador não foi alterado para evitar uma ressurreição incompleta.");
                return;
            }
            metodo.Invoke(alvo, null);
            if (alvo.Data != null && !alvo.Data.IsDead)
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Jogador revivido: " + alvo.Data.PlayerName + " (PlayerId=" + alvo.PlayerId + ").");
            else
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Revive foi chamado, mas o jogo ainda marca o jogador como morto; valide a ressurreição.");
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao reviver jogador morto: " + (ex.InnerException ?? ex)); }
    }
}

internal sealed class EventoPersonalizado : EventoVotacao
{
    public override string Nome => "Evento personalizado: teleporte + controles invertidos";
    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        new EventoTeleporteAleatorio().Executar();
        EventoInverterControles.Ativar();
    }
}

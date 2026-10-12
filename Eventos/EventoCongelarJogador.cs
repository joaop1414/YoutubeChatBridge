using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoCongelarJogador : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private const float VELOCIDADE_CONGELADO = 0.0001f;

    private static readonly Dictionary<byte, float> FimCongelamento = new();
    private static readonly System.Random Sorteador = new();

    public override string Nome => "Congelar um jogador temporariamente";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
        {
            if (player != null && player.Data != null &&
                !player.Data.IsDead && !player.Data.Disconnected &&
                !FimCongelamento.ContainsKey(player.PlayerId))
                candidatos.Add(player);
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Congelamento cancelado: não há jogadores vivos disponíveis.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        byte id = alvo.PlayerId;
        FimCongelamento[id] = Time.realtimeSinceStartup + DuracaoSegundos;

        // A velocidade é substituída no cálculo de movimento, inclusive nos frames
        // seguintes. moveable=false sozinho no host não congela de forma confiável
        // um jogador controlado por outro cliente.
        EventoVelocidadeJogadores.AplicarMultiplicador(id, VELOCIDADE_CONGELADO, DuracaoSegundos);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Congelamento iniciado: alvo={alvo.Data.PlayerName}, PlayerId={id}, " +
            $"velocidade={VELOCIDADE_CONGELADO}, duração={DuracaoSegundos:0}s.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || FimCongelamento.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, float> item in FimCongelamento)
        {
            PlayerControl alvo = EncontrarJogador(item.Key);
            if (agora >= item.Value || alvo == null || alvo.Data == null ||
                alvo.Data.IsDead || alvo.Data.Disconnected)
                expirados.Add(item.Key);
        }

        foreach (byte id in expirados)
        {
            FimCongelamento.Remove(id);
            EventoVelocidadeJogadores.Limpar(id);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Congelamento encerrado para PlayerId=" + id + ".");
        }
    }

    public static void PausarDuranteReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || FimCongelamento.Count == 0)
            return;

        float delta = Math.Max(0f, Time.deltaTime);
        foreach (byte id in new List<byte>(FimCongelamento.Keys))
            FimCongelamento[id] += delta;
    }

    public static void Restaurar()
    {
        foreach (byte id in new List<byte>(FimCongelamento.Keys))
            EventoVelocidadeJogadores.Limpar(id);

        FimCongelamento.Clear();
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            if (player != null && player.PlayerId == playerId)
                return player;
        return null;
    }
}

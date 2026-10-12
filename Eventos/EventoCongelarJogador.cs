using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoCongelarJogador : EventoVotacao
{
    private const float DuracaoSegundos = 15f;

    private static readonly Dictionary<byte, float> FimCongelamento = new();
    private static readonly Dictionary<byte, bool> MovimentoOriginal = new();
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
            {
                candidatos.Add(player);
            }
        }

        if (candidatos.Count == 0)
        {
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        byte id = alvo.PlayerId;

        MovimentoOriginal[id] = alvo.moveable;
        FimCongelamento[id] = Time.realtimeSinceStartup + DuracaoSegundos;

        // Bloqueia o movimento local quando o jogador está sob controle do host.
        alvo.moveable = false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Congelamento iniciado: alvo={alvo.Data.PlayerName}, PlayerId={id}, duração={DuracaoSegundos:0}s.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || FimCongelamento.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, float> item in FimCongelamento)
        {
            byte id = item.Key;
            PlayerControl alvo = EncontrarJogador(id);

            if (agora >= item.Value || alvo == null || alvo.Data == null ||
                alvo.Data.IsDead || alvo.Data.Disconnected)
            {
                RestaurarJogador(alvo, id);
                expirados.Add(id);
                continue;
            }

            alvo.moveable = false;

        }

        foreach (byte id in expirados)
            RemoverCongelamento(id);
    }

    public static void PausarDuranteReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || FimCongelamento.Count == 0)
            return;

        // Não teleporta ninguém. Apenas estende a duração enquanto a reunião está ativa.
        float delta = Math.Max(0f, Time.deltaTime);
        foreach (byte id in new List<byte>(FimCongelamento.Keys))
            FimCongelamento[id] += delta;
    }
    public static void Restaurar()
    {
        foreach (KeyValuePair<byte, bool> item in MovimentoOriginal)
            RestaurarJogador(EncontrarJogador(item.Key), item.Key);

        FimCongelamento.Clear();
        MovimentoOriginal.Clear();
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
        {
            if (player != null && player.PlayerId == playerId)
                return player;
        }

        return null;
    }

    private static void RestaurarJogador(PlayerControl jogador, byte playerId)
    {
        if (jogador == null || jogador.Data == null)
            return;

        if (MovimentoOriginal.TryGetValue(playerId, out bool movimento) &&
            !jogador.Data.IsDead && !jogador.Data.Disconnected)
        {
            jogador.moveable = movimento;
        }
    }

    private static void RemoverCongelamento(byte playerId)
    {
        FimCongelamento.Remove(playerId);
        MovimentoOriginal.Remove(playerId);
    }
}

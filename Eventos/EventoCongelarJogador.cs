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
            if (player != null && player.Data != null && !player.Data.IsDead &&
                player.moveable && !FimCongelamento.ContainsKey(player.PlayerId))
            {
                candidatos.Add(player);
            }
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O evento de congelamento foi sorteado, mas não havia jogador válido.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        MovimentoOriginal[alvo.PlayerId] = alvo.moveable;
        FimCongelamento[alvo.PlayerId] = Time.realtimeSinceStartup + DuracaoSegundos;
        alvo.moveable = false;

        YouTubeVoteBridgePlugin.Anunciar(
            $"❄ {alvo.Data.PlayerName} foi congelado por {DuracaoSegundos:0} segundos pelo chat do YouTube!");

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Congelamento iniciado: {alvo.Data.PlayerName} (PlayerId={alvo.PlayerId}), {DuracaoSegundos:0}s.");
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
            if (agora >= item.Value || alvo == null || alvo.Data == null || alvo.Data.IsDead)
            {
                RestaurarJogador(alvo, item.Key);
                expirados.Add(item.Key);
            }
            else
            {
                // Mantém o comportamento atual de congelamento em clientes vanilla.
                alvo.moveable = false;
            }
        }

        foreach (byte id in expirados)
        {
            FimCongelamento.Remove(id);
            MovimentoOriginal.Remove(id);
        }
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
        if (jogador == null || jogador.Data == null || jogador.Data.IsDead)
            return;

        if (MovimentoOriginal.TryGetValue(playerId, out bool movimento))
            jogador.moveable = movimento;
    }
}

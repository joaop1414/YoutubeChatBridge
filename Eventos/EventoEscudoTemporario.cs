using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoEscudoTemporario : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private static readonly System.Random Sorteador = new();
    private static readonly Dictionary<byte, float> ProtecoesAtivas = new();

    public override string Nome => "Escudo temporário";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                ProtecoesAtivas.ContainsKey(jogador.PlayerId))
                continue;

            candidatos.Add(jogador);
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Escudo temporário cancelado: não há jogadores vivos sem escudo.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        ProtecoesAtivas[alvo.PlayerId] = Time.realtimeSinceStartup + DuracaoSegundos;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Escudo temporário ativado para {alvo.Data.PlayerName} " +
            $"(PlayerId={alvo.PlayerId}) por {DuracaoSegundos:0} segundos.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ProtecoesAtivas.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, float> item in ProtecoesAtivas)
        {
            PlayerControl jogador = EncontrarJogador(item.Key);

            if (agora >= item.Value || jogador == null ||
                jogador.Data == null || jogador.Data.IsDead ||
                jogador.Data.Disconnected)
            {
                expirados.Add(item.Key);
            }
        }

        foreach (byte id in expirados)
        {
            ProtecoesAtivas.Remove(id);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Escudo temporário encerrado para PlayerId={id}.");
        }
    }

    private static PlayerControl EncontrarJogador(byte id)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.PlayerId == id)
                return jogador;
        }

        return null;
    }

    internal static bool EstaProtegido(byte id)
    {
        return ProtecoesAtivas.TryGetValue(id, out float fim) &&
               Time.realtimeSinceStartup < fim;
    }
}

// A verificação autoritativa da morte acontece em CheckMurder.
// Patchar MurderPlayer era tarde demais ou não interceptava o fluxo usado pelo jogo.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class EventoEscudoTemporarioMortePatch
{
    public static bool Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || alvo == null)
            return true;

        if (!EventoEscudoTemporario.EstaProtegido(alvo.PlayerId))
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Tentativa de morte bloqueada pelo escudo temporário: " +
            $"{alvo.Data?.PlayerName ?? "jogador desconhecido"} " +
            $"(PlayerId={alvo.PlayerId}, atacante={__instance?.Data?.PlayerName ?? "desconhecido"}).");

        return false;
    }
}

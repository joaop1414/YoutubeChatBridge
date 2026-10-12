using System;
using System.Collections.Generic;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoCongelarJogador : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private const float IntervaloReforco = 0.15f;

    private static readonly Dictionary<byte, float> FimCongelamento = new();
    private static readonly Dictionary<byte, bool> MovimentoOriginal = new();
    private static readonly Dictionary<byte, Vector2> PosicaoCongelada = new();
    private static readonly Dictionary<byte, float> ProximoReforco = new();
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
                !FimCongelamento.ContainsKey(player.PlayerId))
                candidatos.Add(player);
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O evento de congelamento foi sorteado, mas não havia jogador válido.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        byte id = alvo.PlayerId;

        MovimentoOriginal[id] = alvo.moveable;
        PosicaoCongelada[id] = alvo.transform.position;
        FimCongelamento[id] = Time.realtimeSinceStartup + DuracaoSegundos;
        ProximoReforco[id] = 0f;

        // moveable=false sozinho só bloqueia o objeto no host. Para impedir
        // que o cliente vanilla avance, o host também reenvia SnapTo para
        // todos os clientes, mantendo o jogador na posição inicial.
        alvo.moveable = false;
        EnviarSnapTo(alvo, PosicaoCongelada[id]);

        YouTubeVoteBridgePlugin.Anunciar(
            $"❄ {alvo.Data.PlayerName} foi congelado por {DuracaoSegundos:0} segundos pelo chat do YouTube!");

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Congelamento iniciado: {alvo.Data.PlayerName} (PlayerId={id}), {DuracaoSegundos:0}s.");
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

            if (agora >= item.Value || alvo == null || alvo.Data == null || alvo.Data.IsDead)
            {
                RestaurarJogador(alvo, id);
                expirados.Add(id);
                continue;
            }

            alvo.moveable = false;
            if (!ProximoReforco.TryGetValue(id, out float proximo) || agora >= proximo)
            {
                EnviarSnapTo(alvo, PosicaoCongelada[id]);
                ProximoReforco[id] = agora + IntervaloReforco;
            }
        }

        foreach (byte id in expirados)
            RemoverCongelamento(id);
    }

    public static bool EstaCongelado(byte playerId)
    {
        return FimCongelamento.TryGetValue(playerId, out float fim) &&
               Time.realtimeSinceStartup < fim;
    }

    public static void Restaurar()
    {
        foreach (KeyValuePair<byte, bool> item in MovimentoOriginal)
            RestaurarJogador(EncontrarJogador(item.Key), item.Key);

        FimCongelamento.Clear();
        MovimentoOriginal.Clear();
        PosicaoCongelada.Clear();
        ProximoReforco.Clear();
    }

    private static void EnviarSnapTo(PlayerControl jogador, Vector2 posicao)
    {
        if (jogador == null || jogador.NetTransform == null ||
            AmongUsClient.Instance == null)
            return;

        try
        {
            ushort sequencia = jogador.NetTransform.lastSequenceId;
            EnviarSnapToComSequencia(jogador, posicao, sequencia);
            // Um segundo número de sequência ajuda clientes que descartam
            // SnapTo repetido com a mesma sequência.
            EnviarSnapToComSequencia(
                jogador, posicao, unchecked((ushort)(sequencia + 16383)));
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível reforçar o congelamento por SnapTo: " + ex.Message);
        }
    }

    private static void EnviarSnapToComSequencia(
        PlayerControl jogador, Vector2 posicao, ushort sequencia)
    {
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            jogador.NetTransform.NetId,
            (byte)RpcCalls.SnapTo,
            SendOption.Reliable);

        writer.Write(posicao);
        writer.Write(sequencia);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
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

    private static void RemoverCongelamento(byte playerId)
    {
        FimCongelamento.Remove(playerId);
        MovimentoOriginal.Remove(playerId);
        PosicaoCongelada.Remove(playerId);
        ProximoReforco.Remove(playerId);
    }
}

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
            if (player != null && player.Data != null &&
                !player.Data.IsDead && !player.Data.Disconnected &&
                !FimCongelamento.ContainsKey(player.PlayerId))
            {
                candidatos.Add(player);
            }
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Anunciar(
                "O congelamento foi sorteado, mas não havia jogador vivo disponível.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        byte id = alvo.PlayerId;

        MovimentoOriginal[id] = alvo.moveable;
        PosicaoCongelada[id] = alvo.transform.position;
        FimCongelamento[id] = Time.realtimeSinceStartup + DuracaoSegundos;
        ProximoReforco[id] = 0f;

        // Bloqueia o movimento local quando o jogador está sob controle do host.
        alvo.moveable = false;

        // O EHR reforça SnapTo individualmente para os clientes. Um único RPC
        // em broadcast pode não corrigir a posição em todos os clientes vanilla.
        EnviarSnapToParaClientes(alvo, PosicaoCongelada[id]);

        YouTubeVoteBridgePlugin.Anunciar(
            $"❄ {alvo.Data.PlayerName} foi congelado por {DuracaoSegundos:0} segundos pelo chat do YouTube!");

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
                alvo.Data.IsDead || alvo.Data.Disconnected || MeetingHud.Instance != null)
            {
                RestaurarJogador(alvo, id);
                expirados.Add(id);
                continue;
            }

            alvo.moveable = false;
            if (!ProximoReforco.TryGetValue(id, out float proximo) || agora >= proximo)
            {
                EnviarSnapToParaClientes(alvo, PosicaoCongelada[id]);
                ProximoReforco[id] = agora + IntervaloReforco;
            }
        }

        foreach (byte id in expirados)
            RemoverCongelamento(id);
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

    private static void EnviarSnapToParaClientes(PlayerControl jogador, Vector2 posicao)
    {
        if (jogador == null || jogador.NetTransform == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        ushort sequencia = jogador.NetTransform.lastSequenceId;
        int enviados = 0;

        foreach (PlayerControl destinatario in PlayerControl.AllPlayerControls)
        {
            if (destinatario == null || destinatario.AmOwner || destinatario.OwnerId < 0)
                continue;

            try
            {
                EnviarSnapTo(jogador, destinatario.OwnerId, posicao, sequencia);
                EnviarSnapTo(jogador, destinatario.OwnerId, posicao,
                    unchecked((ushort)(sequencia + 16383)));
                enviados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Falha enviando SnapTo de congelamento para OwnerId={destinatario.OwnerId}: {ex.Message}");
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"SnapTo de congelamento enviado para {enviados} cliente(s); alvo PlayerId={jogador.PlayerId}.");
    }

    private static void EnviarSnapTo(
        PlayerControl jogador, int destinatarioId, Vector2 posicao, ushort sequencia)
    {
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            jogador.NetTransform.NetId,
            (byte)RpcCalls.SnapTo,
            SendOption.Reliable,
            destinatarioId);

        // MessageWriter da versão do projeto não expõe WriteVector2:
        // o formato do RPC SnapTo é dois floats (X, Y) e o ushort da sequência.
        writer.Write(posicao.x);
        writer.Write(posicao.y);
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
        PosicaoCongelada.Remove(playerId);
        ProximoReforco.Remove(playerId);
    }
}

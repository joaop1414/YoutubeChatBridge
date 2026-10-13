using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

// NPC de rede: o host cria o objeto e envia atualizações SnapTo aos clientes.
internal sealed class EventoNpcSeguidor : EventoVotacao
{
    public override string Nome => "Criar NPC que segue jogadores";

    private static PlayerControl npc;
    private static float proximaAtualizacao;
    private static PlayerControl alvo;
    private static readonly System.Random Sorteador = new();

    private const byte PlayerIdNpc = 254;
    private const float IntervaloMovimento = 0.20f;
    private const float Velocidade = 2.0f;
    private const float DistanciaParada = 0.65f;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        if (MeetingHud.Instance != null || ShipStatus.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC cancelado: entre na fase de tarefas antes de criá-lo.");
            return;
        }

        if (npc != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "O NPC já existe nesta partida.");
            return;
        }

        try
        {
            Vector2 spawnPos = PlayerControl.LocalPlayer.transform.position;
            npc = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, spawnPos, Quaternion.identity);
            npc.PlayerId = PlayerIdNpc;
            npc.isNew = false;
            npc.notRealPlayer = true;
            npc.NetTransform.SnapTo(spawnPos);

            // Registra e transmite o objeto falso para todos os clientes.
            AmongUsClient.Instance.NetIdCnt += 1U;
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);

                writer.StartMessage(4);
                var spawn = AmongUsClient.Instance.CreateSpawnMessage(
                    npc, -2, SpawnFlags.None);
                spawn.SerializeValues(writer);
                writer.EndMessage();

                // Os três NetIds auxiliares do prefab não são necessários ao NPC.
                for (uint i = 1; i <= 3; i++)
                {
                    writer.StartMessage(4);
                    writer.WritePacked(2U);
                    writer.WritePacked(-2);
                    writer.Write((byte)SpawnFlags.None);
                    writer.WritePacked(1);
                    writer.WritePacked(AmongUsClient.Instance.NetIdCnt - i);
                    writer.StartMessage(1);
                    writer.EndMessage();
                    writer.EndMessage();
                }

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;
            }
            finally
            {
                writer?.Recycle();
            }

            if (PlayerControl.AllPlayerControls.Contains(npc))
                PlayerControl.AllPlayerControls.Remove(npc);

            // Define o nome e a cor do NPC através de RPCs normais.
            EnviarNome("NPC");
            EnviarCor(5);

            alvo = EscolherAlvo();
            proximaAtualizacao = Time.realtimeSinceStartup + IntervaloMovimento;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "NPC criado; começando a seguir jogadores reais.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao criar NPC: " + ex);
            RemoverLocal();
        }
    }

    public static void Atualizar()
    {
        if (npc == null || !YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        if (MeetingHud.Instance != null)
            return;

        if (Time.realtimeSinceStartup < proximaAtualizacao)
            return;

        proximaAtualizacao = Time.realtimeSinceStartup + IntervaloMovimento;

        if (!AlvoValido(alvo))
            alvo = EscolherAlvo();

        if (!AlvoValido(alvo))
            return;

        Vector2 atual = npc.transform.position;
        Vector2 destino = alvo.transform.position;
        Vector2 diferenca = destino - atual;
        if (diferenca.sqrMagnitude <= DistanciaParada * DistanciaParada)
            return;

        Vector2 novaPosicao = Vector2.MoveTowards(
            atual, destino, Velocidade * IntervaloMovimento);

        try
        {
            ushort sequencia = (ushort)(npc.NetTransform.lastSequenceId + 1);
            npc.NetTransform.SnapTo(novaPosicao, sequencia);

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                npc.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.Reliable);
            writer.Write(novaPosicao.x);
            writer.Write(novaPosicao.y);
            writer.Write(sequencia);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao movimentar NPC: " + ex.Message);
        }
    }

    private static PlayerControl EscolherAlvo()
    {
        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == npc || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;
            vivos.Add(jogador);
        }

        return vivos.Count == 0 ? null : vivos[Sorteador.Next(vivos.Count)];
    }

    private static bool AlvoValido(PlayerControl jogador) =>
        jogador != null && jogador.Data != null &&
        !jogador.Data.Disconnected && !jogador.Data.IsDead;

    private static void EnviarNome(string nome)
    {
        if (npc == null || npc.Data == null) return;
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            npc.NetId, (byte)RpcCalls.SetName, SendOption.Reliable);
        writer.Write(npc.Data.NetId);
        writer.Write(nome);
        writer.Write(false);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    private static void EnviarCor(byte cor)
    {
        if (npc == null || npc.Data == null) return;
        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            npc.NetId, (byte)RpcCalls.SetColor, SendOption.Reliable);
        writer.Write(npc.Data.NetId);
        writer.Write(cor);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    public static void Limpar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || npc == null ||
            AmongUsClient.Instance == null)
        {
            RemoverLocal();
            return;
        }

        try
        {
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.StartMessage(5);
            writer.WritePacked(npc.NetId);
            writer.EndMessage();
            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            AmongUsClient.Instance.RemoveNetObject(npc);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao remover NPC da rede: " + ex.Message);
        }
        finally
        {
            RemoverLocal();
        }
    }

    private static void RemoverLocal()
    {
        if (npc != null)
        {
            try
            {
                if (PlayerControl.AllPlayerControls.Contains(npc))
                    PlayerControl.AllPlayerControls.Remove(npc);
            }
            catch { }

            try { UnityEngine.Object.Destroy(npc.gameObject); }
            catch { }
        }

        npc = null;
        alvo = null;
    }
}

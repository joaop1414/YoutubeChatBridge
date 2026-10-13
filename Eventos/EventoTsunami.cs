using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTsunami : EventoVotacao
{
    public override string Nome => "Tsunami";
    private const byte PlayerIdIndicador = 253;
    private const float Velocidade = 3.0f;
    private const float Intervalo = 0.08f;
    private const float ProtecaoDuto = 1.0f;
    private const float Alcance = 0.85f;

    private static PlayerControl indicador;
    private static Vector2 posicao;
    private static float proximaAtualizacao;
    private static float fimProtecao;
    private static bool ativo;
    private static readonly HashSet<byte> atingidos = new();
    private static readonly Dictionary<byte, float> protegidos = new();

    private const string NomeOnda =
        "<color=#0099d1><size=4><line-height=97%><cspace=0.16em><mark=#000000>WWWWWWWWWWWWWWWWWWWWWWWW</mark></cspace></line-height></size></color>";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null || ShipStatus.Instance == null ||
            MeetingHud.Instance != null)
            return;

        if (ativo)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("O tsunami já está ativo.");
            return;
        }

        try
        {
            LimparLocal();
            atingidos.Clear();
            protegidos.Clear();
            fimProtecao = 0f;
            posicao = ObterInicio();
            indicador = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, posicao, Quaternion.identity);
            indicador.PlayerId = PlayerIdIndicador;
            indicador.isNew = false;
            indicador.notRealPlayer = true;
            indicador.CachedPlayerData = PlayerControl.LocalPlayer.Data;
            indicador.NetTransform.SnapTo(posicao);

            AmongUsClient.Instance.NetIdCnt += 1U;
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(4);
                var spawn = AmongUsClient.Instance.CreateSpawnMessage(indicador, -2, SpawnFlags.None);
                spawn.SerializeValues(writer);
                writer.EndMessage();

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
            finally { writer?.Recycle(); }

            if (PlayerControl.AllPlayerControls.Contains(indicador))
                PlayerControl.AllPlayerControls.Remove(indicador);

            ativo = true;
            proximaAtualizacao = Time.realtimeSinceStartup;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Tsunami iniciado. Indicador visual separado dos jogadores reais.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao iniciar tsunami: " + ex);
            Limpar();
        }
    }

    public static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        if (MeetingHud.Instance != null || ShipStatus.Instance == null)
        {
            Limpar();
            return;
        }

        float agora = Time.realtimeSinceStartup;
        if (agora < proximaAtualizacao) return;
        proximaAtualizacao = agora + Intervalo;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == indicador || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;

            if (protegidos.TryGetValue(jogador.PlayerId, out float protecaoAte))
            {
                if (agora < protecaoAte) continue;
                protegidos.Remove(jogador.PlayerId);
                try
                {
                    // Saída de emergência: só tenta sair se o jogo ainda o considera dentro de um duto.
                    if (jogador.inVent && jogador.MyPhysics != null)
                        jogador.MyPhysics.RpcExitVent(0);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Tsunami: falha ao retirar jogador do duto: " + ex.Message);
                }
            }

            if (jogador.inVent)
            {
                // Entrar manualmente num duto depende da API e do estado de cada mapa.
                // Quem já está no duto recebe proteção contra a onda.
                protegidos[jogador.PlayerId] = agora + ProtecaoDuto;
                continue;
            }

            if (atingidos.Contains(jogador.PlayerId)) continue;
            Vector2 p = jogador.transform.position;
            if (Mathf.Abs(p.x - posicao.x) > Alcance ||
                Mathf.Abs(p.y - posicao.y) > 1.35f)
                continue;

            try
            {
                // A morte real é registrada antes da reunião, para que a vítima
                // apareça na lista de mortos. A API padrão pode tocar animação.
                jogador.RpcMurderPlayer(jogador, true);
                atingidos.Add(jogador.PlayerId);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami atingiu " + jogador.Data.PlayerName + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao matar jogador: " + ex);
            }
        }

        if (indicador == null)
        {
            Limpar();
            return;
        }

        posicao.x += Velocidade * Intervalo;
        if (posicao.x > LimiteDireito() + 1.5f)
        {
            Limpar();
            return;
        }

        try
        {
            indicador.NetTransform.SnapTo(posicao,
                (ushort)(indicador.NetTransform.lastSequenceId + 1U));
            MessageWriter move = AmongUsClient.Instance.StartRpcImmediately(
                indicador.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.None);
            move.Write(posicao.x);
            move.Write(posicao.y);
            move.Write((ushort)(indicador.NetTransform.lastSequenceId + 2U));
            AmongUsClient.Instance.FinishRpcImmediately(move);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha ao mover indicador: " + ex.Message);
        }
    }

    private static Vector2 ObterInicio()
    {
        float y = 0f;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null && !jogador.Data.Disconnected)
            {
                y = jogador.transform.position.y;
                break;
            }
        }
        return new Vector2(LimiteEsquerdo() - 1.5f, y);
    }

    private static float LimiteEsquerdo()
    {
        float x = 0f;
        bool encontrado = false;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected) continue;
            float px = jogador.transform.position.x;
            if (!encontrado || px < x) { x = px; encontrado = true; }
        }
        return encontrado ? x - 5f : -10f;
    }

    private static float LimiteDireito()
    {
        float x = 0f;
        bool encontrado = false;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected) continue;
            float px = jogador.transform.position.x;
            if (!encontrado || px > x) { x = px; encontrado = true; }
        }
        return encontrado ? x + 5f : 10f;
    }

    public static void Limpar()
    {
        ativo = false;
        atingidos.Clear();
        protegidos.Clear();
        if (indicador != null && AmongUsClient.Instance != null &&
            YouTubeVoteBridgePlugin.EhHost())
        {
            try
            {
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(5);
                writer.WritePacked(indicador.NetId);
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer.Recycle();
                AmongUsClient.Instance.RemoveNetObject(indicador);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Tsunami: falha ao remover indicador: " + ex.Message);
            }
        }
        LimparLocal();
    }

    private static void LimparLocal()
    {
        if (indicador != null)
        {
            try
            {
                if (PlayerControl.AllPlayerControls.Contains(indicador))
                    PlayerControl.AllPlayerControls.Remove(indicador);
            }
            catch { }
            try { UnityEngine.Object.Destroy(indicador.gameObject); }
            catch { }
        }
        indicador = null;
    }
}

using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private static bool ativo;
    private static bool armado;
    private static readonly HashSet<byte> jogadoresInvisiveis = new();

    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;

        ativo = true;
        armado = true;
        jogadoresInvisiveis.Clear();

        if (ShipStatus.Instance != null)
            AplicarInvisibilidade("ativação do evento");
    }

    internal static void IniciarPartida()
    {
        if (ativo && armado && ShipStatus.Instance != null)
            AplicarInvisibilidade("início da partida");
    }

    internal static void AoImpostorMatar(PlayerControl assassino)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            assassino == null || assassino.Data == null ||
            assassino.Data.Role?.IsImpostor != true)
            return;

        AplicarInvisibilidade("morte causada por impostor");
    }

    private static void AplicarInvisibilidade(string motivo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            PlayerControl.AllPlayerControls == null ||
            AmongUsClient.Instance == null)
            return;

        armado = false;
        int alvos = 0;
        int destinatarios = 0;

        // O host prepara um único PackedGameDataTo, como o EHR.
        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        bool pacoteAberto = false;

        try
        {
            writer.StartMessage(26); // Packed GameData
            writer.WritePacked(AmongUsClient.Instance.GameId);
            pacoteAberto = true;

            foreach (PlayerControl alvo in PlayerControl.AllPlayerControls)
            {
                if (!PodeFicarInvisivel(alvo))
                    continue;

                jogadoresInvisiveis.Add(alvo.PlayerId);

                // Aplica a invisibilidade na visão local do host.
                // O dono do personagem nunca deve esconder o próprio personagem.
                if (!alvo.AmOwner)
                {
                    try
                    {
                        alvo.MakeInvisible();
                    }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            $"[Invisibilidade] MakeInvisible falhou no host para PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
                    }
                }

                ushort sequencia = alvo.NetTransform.lastSequenceId;

                foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
                {
                    // Somente tripulantes vivos recebem. Cada um não recebe
                    // os RPCs do próprio personagem.
                    if (!PodeReceberRpc(espectador) ||
                        espectador.AmOwner ||
                        espectador == alvo)
                        continue;

                    // Formato copiado do CustomRpcSender do EHR:
                    // GameDataTo (6) + GameId + ClientId + RPCs.
                    writer.StartMessage(6);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(espectador.OwnerId);

                    // Técnica do EHR: dois SnapTo para (50, 50), com
                    // sequências diferentes, fazem o personagem desaparecer
                    // somente para esse cliente.
                    EscreverSnapTo(writer, alvo, new Vector2(50f, 50f), sequencia);
                    EscreverSnapTo(writer, alvo, new Vector2(50f, 50f),
                        (ushort)(sequencia + 16383));

                    writer.EndMessage(); // GameDataTo
                    destinatarios++;
                }

                alvos++;
            }

            writer.EndMessage(); // Packed GameData
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;
        }
        catch (Exception ex)
        {
            if (pacoteAberto)
            {
                try { writer.EndMessage(); } catch { }
            }

            writer?.Recycle();
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Invisibilidade] Erro ao montar/enviar pacote ({motivo}): {ex}");
            return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Técnica EHR aplicada. Motivo={motivo}; alvos={alvos}; destinatários={destinatarios}.");
    }

    private static void EscreverSnapTo(MessageWriter writer, PlayerControl alvo,
        Vector2 posicao, ushort sequencia)
    {
        writer.StartMessage(2); // RPC
        writer.WritePacked(alvo.NetTransform.NetId);
        writer.Write((byte)RpcCalls.SnapTo);
        writer.Write(posicao.x);
        writer.Write(posicao.y);
        writer.Write(sequencia);
        writer.EndMessage();
    }

    private static bool PodeFicarInvisivel(PlayerControl jogador)
    {
        // Todos os jogadores vivos são alvos, inclusive impostores.
        return jogador != null && jogador.Data != null &&
               !jogador.Data.Disconnected && !jogador.Data.IsDead &&
               jogador.NetTransform != null;
    }

    private static bool PodeReceberRpc(PlayerControl espectador)
    {
        // Somente tripulantes vivos recebem os RPCs direcionados.
        return espectador != null && espectador.Data != null &&
               !espectador.Data.Disconnected && !espectador.Data.IsDead &&
               espectador.Data.Role?.IsImpostor == false &&
               espectador.OwnerId >= 0;
    }

    internal static void EncerrarNaReuniao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = false;
        armado = false;

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        bool pacoteAberto = false;
        int restaurados = 0;

        try
        {
            writer.StartMessage(26);
            writer.WritePacked(AmongUsClient.Instance.GameId);
            pacoteAberto = true;

            foreach (byte playerId in jogadoresInvisiveis)
            {
                PlayerControl alvo = EncontrarJogador(playerId);
                if (alvo == null || alvo.Data == null ||
                    alvo.Data.Disconnected || alvo.NetTransform == null)
                    continue;

                if (!alvo.AmOwner)
                {
                    try { alvo.MakeVisible(); }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            $"[Invisibilidade] MakeVisible falhou para PlayerId={playerId}: {ex.GetBaseException().Message}");
                    }
                }

                ushort seq = alvo.NetTransform.lastSequenceId;
                foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
                {
                    if (!PodeReceberRpc(espectador) ||
                        espectador.AmOwner ||
                        espectador == alvo)
                        continue;

                    writer.StartMessage(6);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(espectador.OwnerId);

                    // Sequências de recuperação usadas pelo EHR.
                    EscreverSnapTo(writer, alvo, new Vector2(50f, 50f),
                        (ushort)(seq + 32767));
                    EscreverSnapTo(writer, alvo, new Vector2(50f, 50f),
                        (ushort)(seq + 32767 + 16383));
                    EscreverSnapTo(writer, alvo, (Vector2)alvo.transform.position, seq);

                    writer.EndMessage();
                }

                restaurados++;
            }

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;
        }
        catch (Exception ex)
        {
            if (pacoteAberto)
            {
                try { writer.EndMessage(); } catch { }
            }
            writer?.Recycle();
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Invisibilidade] Erro ao restaurar jogadores na reunião: {ex}");
        }

        jogadoresInvisiveis.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Evento encerrado na reunião. Jogadores restaurados={restaurados}.");
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        if (PlayerControl.AllPlayerControls == null) return null;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        return null;
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        jogadoresInvisiveis.Clear();
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    private static class ReuniaoInvisibilidadePatch
    {
        private static void Postfix() => EncerrarNaReuniao();
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.MurderPlayer))]
    private static class MorteImpostorInvisibilidadePatch
    {
        private static void Postfix(PlayerControl __instance)
        {
            AoImpostorMatar(__instance);
        }
    }
}

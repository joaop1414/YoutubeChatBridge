using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    // 249 está livre: 248 = handshake, 250 = painel, 251 = nome NPC.
    private const byte RpcInvisibilidade = 249;
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
        int rpcModded = 0;
        int fallbackVanilla = 0;

        foreach (PlayerControl alvo in PlayerControl.AllPlayerControls)
        {
            if (!PodeFicarInvisivel(alvo))
                continue;

            jogadoresInvisiveis.Add(alvo.PlayerId);

            // A visão do host é atualizada localmente. Nunca escondemos o próprio host.
            if (!alvo.AmOwner)
            {
                try { AplicarVisualInvisivelLocal(alvo); }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha local para PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
                }
            }

            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (!PodeReceberRpc(espectador) ||
                    espectador.AmOwner ||
                    espectador == alvo)
                    continue;

                if (YouTubeVoteBridgePlugin.ClienteTemMod(espectador.OwnerId))
                {
                    if (EnviarRpcDirecionado(espectador, alvo, true))
                        rpcModded++;
                }
                else
                {
                    // Clientes vanilla não conhecem o RPC personalizado. Para eles,
                    // mantemos o fallback SnapTo do EHR, direcionado ao OwnerId.
                    if (EnviarSnapToVanilla(espectador, alvo, true))
                        fallbackVanilla++;
                }
            }

            alvos++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Ativada. Motivo={motivo}; alvos={alvos}; " +
            $"RPCs personalizados enviados={rpcModded}; fallback vanilla enviado={fallbackVanilla}.");
    }

    private static bool EnviarRpcDirecionado(
        PlayerControl espectador, PlayerControl alvo, bool invisivel)
    {
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null ||
            espectador == null || alvo == null)
            return false;

        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(espectador.OwnerId);

            // O RPC é executado no PlayerControl do host no cliente destinatário.
            writer.StartMessage(2);
            writer.WritePacked(PlayerControl.LocalPlayer.NetId);
            writer.Write(RpcInvisibilidade);
            writer.WritePacked(alvo.NetId);
            writer.Write((byte)(invisivel ? 1 : 0));
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer = null;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha enviando RPC para OwnerId={espectador.OwnerId}, " +
                $"alvo NetId={alvo.NetId}: {ex.GetBaseException().Message}");
            return false;
        }
        finally
        {
            writer?.Recycle();
        }
    }

    private static bool EnviarSnapToVanilla(
        PlayerControl espectador, PlayerControl alvo, bool invisivel)
    {
        if (AmongUsClient.Instance == null || espectador == null ||
            alvo == null || alvo.NetTransform == null)
            return false;

        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(espectador.OwnerId);

            ushort seq = alvo.NetTransform.lastSequenceId;
            if (invisivel)
            {
                EscreverSnapTo(writer, alvo, new Vector2(50f, 50f), seq);
                EscreverSnapTo(writer, alvo, new Vector2(50f, 50f),
                    (ushort)(seq + 16383));
            }
            else
            {
                EscreverSnapTo(writer, alvo, new Vector2(50f, 50f),
                    (ushort)(seq + 32767));
                EscreverSnapTo(writer, alvo, new Vector2(50f, 50f),
                    (ushort)(seq + 32767 + 16383));
                EscreverSnapTo(writer, alvo, (Vector2)alvo.transform.position, seq);
            }

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer = null;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha no fallback vanilla para OwnerId={espectador.OwnerId}: {ex.GetBaseException().Message}");
            return false;
        }
        finally
        {
            writer?.Recycle();
        }
    }

    private static void EscreverSnapTo(
        MessageWriter writer, PlayerControl alvo, Vector2 posicao, ushort sequencia)
    {
        writer.StartMessage(2);
        writer.WritePacked(alvo.NetTransform.NetId);
        writer.Write((byte)RpcCalls.SnapTo);
        writer.Write(posicao.x);
        writer.Write(posicao.y);
        writer.Write(sequencia);
        writer.EndMessage();
    }

    private static void AplicarVisualInvisivelLocal(PlayerControl jogador)
    {
        if (jogador == null || PlayerControl.LocalPlayer == null ||
            PlayerControl.LocalPlayer.Data == null)
            return;

        jogador.invisibilityAlpha = jogador.AmOwner
            ? 0.5f
            : PlayerControl.LocalPlayer.Data.Role.IsDead ? 0.5f : 0f;
        jogador.cosmetics.SetPhantomRoleAlpha(jogador.invisibilityAlpha);

        if (!jogador.AmOwner && !PlayerControl.LocalPlayer.Data.Role.IsDead)
        {
            jogador.shouldAppearInvisible = true;
            jogador.Visible = false;
        }
    }

    private static void RestaurarVisualLocal(PlayerControl jogador)
    {
        if (jogador == null) return;

        if (!jogador.AmOwner)
        {
            jogador.shouldAppearInvisible = false;
            jogador.Visible = true;
        }

        jogador.invisibilityAlpha = 1f;
        jogador.cosmetics.SetPhantomRoleAlpha(jogador.invisibilityAlpha);

        if (!jogador.AmOwner)
        {
            jogador.shouldAppearInvisible = false;
            jogador.Visible = !jogador.inVent;
        }
    }

    private static void AplicarVisualNoCliente(uint netId, bool invisivel)
    {
        PlayerControl alvo = EncontrarPorNetId(netId);
        if (alvo == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] RPC recebido, mas NetId={netId} ainda não foi encontrado.");
            return;
        }

        if (invisivel)
            AplicarVisualInvisivelLocal(alvo);
        else
            RestaurarVisualLocal(alvo);
    }

    private static bool PodeFicarInvisivel(PlayerControl jogador) =>
        jogador != null && jogador.Data != null &&
        !jogador.Data.Disconnected && !jogador.Data.IsDead &&
        jogador.NetTransform != null;

    private static bool PodeReceberRpc(PlayerControl espectador) =>
        espectador != null && espectador.Data != null &&
        !espectador.Data.Disconnected && !espectador.Data.IsDead &&
        espectador.Data.Role?.IsImpostor == false &&
        espectador.OwnerId >= 0;

    internal static void EncerrarNaReuniao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = false;
        armado = false;
        int restaurados = 0;
        int rpcModded = 0;
        int fallbackVanilla = 0;

        foreach (byte playerId in jogadoresInvisiveis)
        {
            PlayerControl alvo = EncontrarJogador(playerId);
            if (alvo == null || alvo.Data == null ||
                alvo.Data.Disconnected || alvo.NetTransform == null)
                continue;

            if (!alvo.AmOwner)
            {
                try { RestaurarVisualLocal(alvo); }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha ao restaurar localmente PlayerId={playerId}: {ex.GetBaseException().Message}");
                }
            }

            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (!PodeReceberRpc(espectador) ||
                    espectador.AmOwner ||
                    espectador == alvo)
                    continue;

                if (YouTubeVoteBridgePlugin.ClienteTemMod(espectador.OwnerId))
                {
                    if (EnviarRpcDirecionado(espectador, alvo, false))
                        rpcModded++;
                }
                else
                {
                    if (EnviarSnapToVanilla(espectador, alvo, false))
                        fallbackVanilla++;
                }
            }

            restaurados++;
        }

        jogadoresInvisiveis.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Encerrada na reunião. Jogadores={restaurados}; " +
            $"RPCs personalizados enviados={rpcModded}; fallback vanilla enviado={fallbackVanilla}.");
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        if (PlayerControl.AllPlayerControls == null) return null;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        return null;
    }

    private static PlayerControl EncontrarPorNetId(uint netId)
    {
        if (PlayerControl.AllPlayerControls == null) return null;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.NetId == netId)
                return jogador;
        return null;
    }

    internal static void ReceberRpc(MessageReader reader)
    {
        try
        {
            uint netId = reader.ReadPackedUInt32();
            bool invisivel = reader.ReadByte() != 0;
            AplicarVisualNoCliente(netId, invisivel);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Invisibilidade] RPC personalizado inválido: " + ex);
        }
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
        private static void Postfix(PlayerControl __instance) =>
            AoImpostorMatar(__instance);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class EventoTodosFicamInvisiveisRpcPatch
{
    private const byte RpcInvisibilidade = 249;

    [HarmonyPrefix]
    private static bool Prefix(byte callId, MessageReader reader)
    {
        if (callId != RpcInvisibilidade)
            return true;

        EventoTodosFicamInvisiveis.ReceberRpc(reader);
        return false;
    }
}

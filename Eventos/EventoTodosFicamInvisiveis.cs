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
    private static readonly Dictionary<byte, int> dutosForcados = new();

    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        ativo = true;
        armado = true;
        dutosForcados.Clear();
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
            AmongUsClient.Instance == null ||
            ShipStatus.Instance?.AllVents == null)
            return;

        armado = false;
        int processados = 0;
        int envios = 0;

        foreach (PlayerControl alvo in PlayerControl.AllPlayerControls)
        {
            if (!PodeFicarInvisivel(alvo))
                continue;

            Vent duto = EncontrarDutoMaisProximo(alvo);
            if (duto == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] Nenhum duto encontrado para PlayerId={alvo.PlayerId}.");
                continue;
            }

            dutosForcados[alvo.PlayerId] = duto.Id;
            envios += EnviarRpcParaCadaTripulante(alvo, duto.Id, entrar: true);

            // O host não recebe GameDataTo direcionado a si mesmo.
            // Sua visão local é atualizada sem enviar RPC ao próprio jogador.
            PlayerControl local = PlayerControl.LocalPlayer;
            if (PodeReceberRpc(local) && !alvo.AmOwner)
            {
                try
                {
                    ushort sequence = (ushort)(alvo.NetTransform.lastSequenceId + 2);
                    alvo.NetTransform.SnapTo((Vector2)duto.transform.position, sequence);
                    alvo.Visible = false;
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha ao atualizar host para PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
                }
            }

            processados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Aplicação/reaplicação concluída. Motivo={motivo}; jogadores={processados}; envios individuais={envios}.");
    }

    private static bool PodeFicarInvisivel(PlayerControl jogador)
    {
        return jogador != null && jogador.Data != null &&
               !jogador.Data.Disconnected && !jogador.Data.IsDead &&
               jogador.Data.Role?.IsImpostor != true &&
               jogador.MyPhysics != null && jogador.NetTransform != null;
    }

    private static bool PodeReceberRpc(PlayerControl espectador)
    {
        return espectador != null && espectador.Data != null &&
               !espectador.Data.Disconnected && !espectador.Data.IsDead &&
               espectador.Data.Role?.IsImpostor != true && espectador.OwnerId >= 0;
    }

    private static Vent EncontrarDutoMaisProximo(PlayerControl jogador)
    {
        Vent melhor = null;
        float menorDistancia = float.MaxValue;
        Vector2 posicao = jogador.GetTruePosition();

        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            if (vent == null) continue;
            float distancia = Vector2.Distance(posicao, (Vector2)vent.transform.position);
            if (distancia < menorDistancia)
            {
                menorDistancia = distancia;
                melhor = vent;
            }
        }
        return melhor;
    }

    // Cria um GameDataTo por destinatário. O cliente recebe todos os jogadores
    // elegíveis, menos o personagem que ele próprio controla.
    // Ordem obrigatória: EnterVent primeiro; SnapTo depois.
    private static int EnviarRpcParaCadaTripulante(PlayerControl alvo, int ventId, bool entrar)
    {
        if (alvo?.MyPhysics == null || alvo.NetTransform == null ||
            AmongUsClient.Instance == null || PlayerControl.AllPlayerControls == null)
            return 0;

        int enviados = 0;
        int hostClientId = AmongUsClient.Instance.ClientId;
        var clientes = new HashSet<int>();

        foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
        {
            if (!PodeReceberRpc(espectador) ||
                espectador.OwnerId == hostClientId ||
                espectador == alvo ||
                !clientes.Add(espectador.OwnerId))
                continue;

            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(26); // GameData
                writer.WritePacked(AmongUsClient.Instance.GameId);
                writer.StartMessage(6); // GameDataTo
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(espectador.OwnerId);

                if (entrar)
                {
                    // 1. RPC EnterVent
                    writer.StartMessage(2);
                    writer.WritePacked(alvo.MyPhysics.NetId);
                    writer.Write((byte)RpcCalls.EnterVent);
                    writer.WritePacked(ventId);
                    writer.EndMessage();

                    // 2. RPC SnapTo para a posição do duto selecionado
                    ushort sequence = (ushort)(alvo.NetTransform.lastSequenceId + 2);
                    Vector2 posicao = (Vector2)EncontrarDuto(ventId).transform.position;
                    writer.StartMessage(2);
                    writer.WritePacked(alvo.NetTransform.NetId);
                    writer.Write((byte)RpcCalls.SnapTo);
                    writer.Write(posicao.x);
                    writer.Write(posicao.y);
                    writer.Write(sequence);
                    writer.EndMessage();
                }
                else
                {
                    writer.StartMessage(2);
                    writer.WritePacked(alvo.MyPhysics.NetId);
                    writer.Write((byte)RpcCalls.ExitVent);
                    writer.WritePacked(ventId);
                    writer.EndMessage();
                }

                writer.EndMessage(); // GameDataTo
                writer.EndMessage(); // GameData
                AmongUsClient.Instance.SendOrDisconnect(writer);
                enviados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] Falha no RPC para OwnerId={espectador.OwnerId}, PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
            }
            finally
            {
                writer?.Recycle();
            }
        }
        return enviados;
    }

    private static Vent EncontrarDuto(int ventId)
    {
        foreach (Vent vent in ShipStatus.Instance.AllVents)
            if (vent != null && vent.Id == ventId)
                return vent;
        return null;
    }

    internal static void EncerrarNaReuniao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = false;
        armado = false;
        int expulsos = 0;

        // Usa exatamente a operação do evento de duto aleatório.
        foreach (KeyValuePair<byte, int> entrada in dutosForcados)
        {
            PlayerControl jogador = EncontrarJogador(entrada.Key);
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.MyPhysics == null)
                continue;

            try
            {
                jogador.MyPhysics.RpcBootFromVent(entrada.Value);
                jogador.Visible = true;
                expulsos++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] Falha ao expulsar PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            }
        }

        dutosForcados.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Evento encerrado na reunião. RpcBootFromVent enviado para {expulsos} jogadores.");
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
        dutosForcados.Clear();
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

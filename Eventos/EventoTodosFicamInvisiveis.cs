using System;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private static bool ativo;
    private static bool armado;
    private static readonly System.Collections.Generic.HashSet<byte> invisiveis = new();
    private static readonly System.Collections.Generic.Dictionary<byte, int> dutosForcados = new();

    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        ativo = true;
        armado = true;
        if (ShipStatus.Instance != null) Aplicar();
    }

    internal static void IniciarPartida()
    {
        if (armado && ativo && ShipStatus.Instance != null) Aplicar();
    }

    private static void Aplicar()
    {
        if (!ativo || PlayerControl.AllPlayerControls == null ||
            AmongUsClient.Instance == null || ShipStatus.Instance?.AllVents == null)
            return;

        armado = false;
        dutosForcados.Clear();
        int enviados = 0;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead ||
                jogador.MyPhysics == null || jogador.NetTransform == null)
                continue;

            Vent duto = EncontrarDutoMaisProximo(jogador);
            if (duto == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade/Duto] Nenhum duto encontrado para PlayerId={jogador.PlayerId}.");
                continue;
            }

            Vector2 posicaoDuto = (Vector2)duto.transform.position;
            dutosForcados[jogador.PlayerId] = duto.Id;

            // Cada RPC é enviado individualmente apenas aos tripulantes vivos.
            // O cliente dono do jogador-alvo nunca recebe os RPCs desse jogador.
            EnviarRpcDutoParaEspectadores(jogador, duto.Id, entrar: true, posicaoDuto);

            // O host não recebe mensagens direcionadas a si mesmo. Portanto,
            // atualizamos a visão local somente se o host for tripulante vivo.
            PlayerControl local = PlayerControl.LocalPlayer;
            bool hostPodeReceber =
                local != null &&
                local.Data != null &&
                !local.Data.IsDead &&
                local.Data.Role?.IsImpostor != true;

            if (!jogador.AmOwner && hostPodeReceber)
            {
                ushort sequenceId = (ushort)(jogador.NetTransform.lastSequenceId + 2);
                jogador.NetTransform.SnapTo(posicaoDuto, sequenceId);
                jogador.Visible = false;
            }

            enviados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade/Duto] RPCs direcionados somente aos tripulantes vivos, excluindo o cliente dono de cada jogador. Jogadores processados: {enviados}. Restauração na reunião.");
    }

    private static Vent EncontrarDutoMaisProximo(PlayerControl jogador)
    {
        Vent melhor = null;
        float menorDistancia = float.MaxValue;
        Vector2 pos = jogador.transform.position;

        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            if (vent == null) continue;
            float distancia = Vector2.Distance(pos, vent.transform.position);
            if (distancia < menorDistancia)
            {
                menorDistancia = distancia;
                melhor = vent;
            }
        }

        return melhor;
    }

    private static bool PodeReceberRpc(PlayerControl espectador)
    {
        return espectador != null &&
               espectador.Data != null &&
               !espectador.Data.Disconnected &&
               !espectador.Data.IsDead &&
               espectador.Data.Role?.IsImpostor != true &&
               espectador.OwnerId >= 0;
    }

    // Encaminha os RPCs individualmente para cada tripulante vivo, exceto
    // para o cliente dono do jogador-alvo. Impostores e fantasmas são ignorados.
    // A ordem é SnapTo e depois EnterVent; no encerramento, somente ExitVent.
    private static void EnviarRpcDutoParaEspectadores(
        PlayerControl alvo,
        int ventId,
        bool entrar,
        Vector2? posicaoDuto = null)
    {
        if (alvo?.MyPhysics == null || alvo.NetTransform == null ||
            AmongUsClient.Instance == null || PlayerControl.AllPlayerControls == null)
            return;

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        bool enviou = false;

        try
        {
            writer.StartMessage(26);
            writer.WritePacked(AmongUsClient.Instance.GameId);

            int clientHost = AmongUsClient.Instance.ClientId;
            var clientes = new System.Collections.Generic.HashSet<int>();
            ushort sequenceId = (ushort)(alvo.NetTransform.lastSequenceId + 2);

            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (!PodeReceberRpc(espectador) ||
                    espectador.OwnerId == clientHost ||
                    espectador == alvo ||
                    !clientes.Add(espectador.OwnerId))
                    continue;

                // GameDataTo: envia este conjunto de RPCs somente ao cliente
                // específico que controla o tripulante vivo selecionado.
                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(espectador.OwnerId);

                if (entrar && posicaoDuto.HasValue)
                {
                    // CustomNetworkTransform.SnapTo: Vector2 + ushort sequence ID.
                    writer.StartMessage(2);
                    writer.WritePacked(alvo.NetTransform.NetId);
                    writer.Write((byte)RpcCalls.SnapTo);
                    writer.Write(posicaoDuto.Value.x);
                    writer.Write(posicaoDuto.Value.y);
                    writer.Write(sequenceId);
                    writer.EndMessage();
                }

                // PlayerPhysics.EnterVent/ExitVent.
                writer.StartMessage(2);
                writer.WritePacked(alvo.MyPhysics.NetId);
                writer.Write((byte)(entrar ? RpcCalls.EnterVent : RpcCalls.ExitVent));
                writer.WritePacked(ventId);
                writer.EndMessage();

                writer.EndMessage();
                enviou = true;
            }

            writer.EndMessage();

            if (enviou)
                AmongUsClient.Instance.SendOrDisconnect(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade/Duto] Falha ao enviar {(entrar ? "SnapTo + EnterVent" : "ExitVent")} para PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
        }
        finally
        {
            writer.Recycle();
        }
    }

    internal static void EncerrarNaReuniao()
    {
        if (!ativo) return;
        ativo = false;
        armado = false;

        if (PlayerControl.AllPlayerControls != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                    continue;

                if (dutosForcados.TryGetValue(jogador.PlayerId, out int ventId))
                    EnviarRpcDutoParaEspectadores(jogador, ventId, entrar: false);

                PlayerControl local = PlayerControl.LocalPlayer;
                bool hostPodeReceber =
                    local != null &&
                    local.Data != null &&
                    !local.Data.IsDead &&
                    local.Data.Role?.IsImpostor != true;

                if (!jogador.AmOwner && hostPodeReceber)
                    jogador.Visible = true;
            }
        }

        dutosForcados.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Invisibilidade/Duto] Evento encerrado na reunião; ExitVent enviado somente aos tripulantes vivos, sem enviar ao dono do jogador.");
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    private static class ReuniaoInvisibilidadePatch
    {
        private static void Postfix()
        {
            if (YouTubeVoteBridgePlugin.EhHost())
                EncerrarNaReuniao();
        }
    }
}

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

            EnviarRpcDutoParaEspectadores(jogador, duto.Id, entrar: true, posicaoDuto);
            
            // O host é o emissor dos RPCs direcionados e não recebe a própria
            // mensagem de rede. Atualizamos a posição local para manter a visão
            // do host alinhada com a dos demais clientes.
            if (!jogador.AmOwner)
            {
                ushort sequenceId = (ushort)(jogador.NetTransform.lastSequenceId + 2);
                jogador.NetTransform.SnapTo(posicaoDuto, sequenceId);
                jogador.Visible = false;
            }

            enviados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade/Duto] SnapTo + EnterVent enviados a todos os outros clientes (incluindo impostores), excluindo somente o dono de cada jogador. Jogadores processados: {enviados}. Restauração na reunião.");
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

    // Encaminha os RPCs individualmente para todos os clientes, exceto o dono
    // do jogador-alvo. A ordem é importante: SnapTo primeiro, EnterVent depois.
    // No encerramento, envia apenas ExitVent, também para todos menos o dono.
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
                if (espectador == null || espectador.Data == null ||
                    espectador.Data.Disconnected || espectador.OwnerId < 0 ||
                    espectador.OwnerId == clientHost ||
                    espectador == alvo || !clientes.Add(espectador.OwnerId))
                    continue;

                // GameDataTo: entrega os RPCs somente ao cliente deste espectador.
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

                // Restaura a visibilidade local do host para os outros jogadores.
                if (!jogador.AmOwner)
                    jogador.Visible = true;
            }
        }

        dutosForcados.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Invisibilidade/Duto] Evento encerrado na reunião; ExitVent enviado a todos os outros clientes.");
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

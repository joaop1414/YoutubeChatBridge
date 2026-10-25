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

    private static readonly System.Collections.Generic.Dictionary<byte, int> dutosForcados = new();

    private static void Aplicar()
    {
        if (!ativo || PlayerControl.AllPlayerControls == null ||
            AmongUsClient.Instance == null || ShipStatus.Instance?.AllVents == null)
            return;

        armado = false;
        dutosForcados.Clear();
        int enviados = 0;

        bool hostImpostor = PlayerControl.LocalPlayer?.Data?.Role?.IsImpostor == true;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead ||
                jogador.MyPhysics == null)
                continue;

            Vent duto = EncontrarDutoMaisProximo(jogador);
            if (duto == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade/Duto] Nenhum duto encontrado para PlayerId={jogador.PlayerId}.");
                continue;
            }

            dutosForcados[jogador.PlayerId] = duto.Id;

            // Sincroniza a posição real do jogador com o centro do duto antes
            // de anunciar EnterVent aos clientes. Sem este SnapTo, alguns clientes
            // recebem o estado de duto enquanto o jogador ainda está fora dele.
            if (jogador.NetTransform != null)
                jogador.NetTransform.RpcSnapTo((Vector2)duto.transform.position);

            EnviarRpcDutoParaEspectadores(jogador, duto.Id, entrar: true);

            // O host não pode enviar um RPC direcionado a si mesmo. Se o host for
            // tripulante, reproduzimos localmente apenas o efeito visual dos outros.
            // Se for impostor, ele continua vendo todos normalmente.
            if (!hostImpostor && !jogador.AmOwner)
                jogador.Visible = false;

            enviados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade/Duto] RPC EnterVent enviado a espectadores não-impostores para {enviados} jogador(es); os alvos e os impostores não recebem o RPC. Restauração na reunião.");
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

    // Envia o RPC nativo EnterVent/ExitVent somente para clientes que devem
    // enxergar o jogador dentro do duto. Clientes impostores e o próprio alvo
    // são excluídos deliberadamente.
    private static void EnviarRpcDutoParaEspectadores(PlayerControl alvo, int ventId, bool entrar)
    {
        if (alvo?.MyPhysics == null || AmongUsClient.Instance == null ||
            PlayerControl.AllPlayerControls == null)
            return;

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        bool enviou = false;

        try
        {
            writer.StartMessage(26);
            writer.WritePacked(AmongUsClient.Instance.GameId);

            int clientHost = AmongUsClient.Instance.ClientId;
            var clientes = new System.Collections.Generic.HashSet<int>();

            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (espectador == null || espectador.Data == null || espectador.Data.Disconnected ||
                    espectador.OwnerId < 0 || espectador.OwnerId == clientHost ||
                    espectador == alvo || espectador.Data.Role?.IsImpostor == true ||
                    !clientes.Add(espectador.OwnerId))
                    continue;

                // GameDataTo: RPC direcionado a este espectador.
                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(espectador.OwnerId);

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
                $"[Invisibilidade/Duto] Falha ao enviar {(entrar ? "EnterVent" : "ExitVent")} para PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
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

                // Restaura apenas a visibilidade local do host quando ele é tripulante.
                if (!jogador.AmOwner)
                    jogador.Visible = true;
            }
        }

        dutosForcados.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Invisibilidade/Duto] Evento encerrado na reunião; enviados ExitVent aos espectadores elegíveis.");
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

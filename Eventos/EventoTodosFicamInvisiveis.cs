using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private const float AtrasoSaida = 0.5f;

    private static bool ativo;
    private static bool armado;

    // Jogadores que foram colocados no duto para produzir a dessincronização visual.
    private static readonly Dictionary<byte, int> jogadoresNoDuto = new();

    // A saída é agendada para acontecer depois que o EnterVent já foi replicado.
    private sealed class SaidaPendente
    {
        public byte PlayerId;
        public int VentId;
        public float ExecutarEm;
    }

    private static readonly List<SaidaPendente> saidasPendentes = new();

    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = true;
        armado = true;
        jogadoresNoDuto.Clear();
        saidasPendentes.Clear();

        if (ShipStatus.Instance != null)
            AplicarInvisibilidade("ativação do evento");
    }

    internal static void IniciarPartida()
    {
        if (ativo && armado && ShipStatus.Instance != null)
            AplicarInvisibilidade("início da partida");
    }

    private static void AplicarInvisibilidade(string motivo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            PlayerControl.AllPlayerControls == null ||
            AmongUsClient.Instance == null)
            return;

        if (ShipStatus.Instance?.AllVents == null)
            return;

        armado = false;
        int colocadosNoDuto = 0;

        foreach (PlayerControl alvo in PlayerControl.AllPlayerControls)
        {
            if (!PodeFicarInvisivel(alvo) || jogadoresNoDuto.ContainsKey(alvo.PlayerId))
                continue;

            if (alvo.MyPhysics == null || alvo.NetTransform == null)
                continue;

            Vent vent = EncontrarDutoMaisProximo(alvo);
            if (vent == null)
                continue;

            int ventId = vent.Id;

            try
            {
                // Primeiro posiciona o jogador exatamente sobre a entrada do duto.
                // O SnapTo evita depender de uma posição arbitrária ou de um VentId fixo.
                Vector2 posicaoDuto = (Vector2)vent.transform.position;
                ushort sequencia = (ushort)(alvo.NetTransform.lastSequenceId + 128);
                alvo.NetTransform.SnapTo(posicaoDuto, sequencia);

                // Em seguida usa a entrada nativa no duto, preservando a sincronização.
                alvo.MyPhysics.RpcEnterVent(ventId);

                jogadoresNoDuto[alvo.PlayerId] = ventId;
                saidasPendentes.Add(new SaidaPendente
                {
                    PlayerId = alvo.PlayerId,
                    VentId = ventId,
                    ExecutarEm = Time.realtimeSinceStartup + AtrasoSaida
                });

                colocadosNoDuto++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] Não foi possível colocar PlayerId={alvo.PlayerId} no duto: {ex.GetBaseException().Message}");
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] SnapTo + entrada nos dutos enviados. Motivo={motivo}; jogadores={colocadosNoDuto}.");
    }

    private static Vent EncontrarDutoMaisProximo(PlayerControl jogador)
    {
        if (jogador == null || ShipStatus.Instance?.AllVents == null)
            return null;

        Vent maisProximo = null;
        float menorDistancia = float.MaxValue;
        Vector2 posicaoJogador = jogador.transform.position;

        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            if (vent == null)
                continue;

            float distancia = Vector2.Distance(posicaoJogador, vent.transform.position);
            if (distancia >= menorDistancia)
                continue;

            menorDistancia = distancia;
            maisProximo = vent;
        }

        return maisProximo;
    }

    private static bool PodeFicarInvisivel(PlayerControl jogador) =>
        jogador != null &&
        jogador.Data != null &&
        !jogador.Data.Disconnected &&
        !jogador.Data.IsDead &&
        jogador.MyPhysics != null &&
        jogador.NetTransform != null;

    private static bool EstaVivo(PlayerControl jogador) =>
        jogador != null &&
        jogador.Data != null &&
        !jogador.Data.Disconnected &&
        !jogador.Data.IsDead &&
        jogador.OwnerId >= 0;

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        if (PlayerControl.AllPlayerControls == null)
            return null;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        }

        return null;
    }

    // Equivalente ao RpcExitVentDesync do EHR:
    // executa CoExitVent localmente quando o destinatário é o host;
    // caso contrário, envia o RPC nativo ExitVent somente ao OwnerId indicado.
    private static bool EnviarSaidaDutoDesync(PlayerControl alvo, int ventId, PlayerControl espectador)
    {
        if (AmongUsClient.Instance == null ||
            alvo == null ||
            alvo.MyPhysics == null ||
            espectador == null ||
            espectador.OwnerId < 0)
            return false;

        int clientId = espectador.OwnerId;

        try
        {
            if (AmongUsClient.Instance.ClientId == clientId)
            {
                alvo.MyPhysics.StopAllCoroutines();
                alvo.MyPhysics.StartCoroutine(alvo.MyPhysics.CoExitVent(ventId));
                return true;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                alvo.MyPhysics.NetId,
                (byte)RpcCalls.ExitVent,
                SendOption.Reliable,
                clientId
            );

            writer.WritePacked(ventId);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha ao sincronizar saída do duto. Alvo={alvo.PlayerId}; " +
                $"espectador={espectador.PlayerId}; erro={ex.GetBaseException().Message}");
            return false;
        }
    }

    private static void ProcessarSaidasPendentes()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || saidasPendentes.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;

        for (int i = saidasPendentes.Count - 1; i >= 0; i--)
        {
            SaidaPendente pendente = saidasPendentes[i];
            if (agora < pendente.ExecutarEm)
                continue;

            saidasPendentes.RemoveAt(i);

            PlayerControl alvo = EncontrarJogador(pendente.PlayerId);
            if (!EstaVivo(alvo))
            {
                jogadoresNoDuto.Remove(pendente.PlayerId);
                continue;
            }

            int saidasEnviadas = 0;

            // O próprio jogador precisa sair do duto somente na própria tela.
            if (EnviarSaidaDutoDesync(alvo, pendente.VentId, alvo))
                saidasEnviadas++;

            // Impostores também recebem a saída, para continuarem vendo o jogador.
            // Tripulantes que não são o próprio alvo NÃO recebem ExitVent:
            // para eles, o alvo continua dentro do duto e fica invisível.
            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (!EstaVivo(espectador) ||
                    espectador == alvo ||
                    espectador.Data.Role?.IsImpostor != true)
                    continue;

                if (EnviarSaidaDutoDesync(alvo, pendente.VentId, espectador))
                    saidasEnviadas++;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Invisibilidade] Duto dessincronizado para PlayerId={alvo.PlayerId}; " +
                $"saídas direcionadas={saidasEnviadas}.");
        }
    }

    internal static void EncerrarNaReuniao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        ativo = false;
        armado = false;
        saidasPendentes.Clear();

        int restaurados = 0;
        int saidasEnviadas = 0;

        foreach (KeyValuePair<byte, int> entrada in jogadoresNoDuto)
        {
            PlayerControl alvo = EncontrarJogador(entrada.Key);
            if (alvo == null || alvo.Data == null ||
                alvo.Data.Disconnected || alvo.MyPhysics == null)
                continue;

            // Na reunião, todos os clientes precisam receber ExitVent.
            // Isso desfaz a dessincronização criada durante o evento.
            foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
            {
                if (espectador == null || espectador.Data == null ||
                    espectador.Data.Disconnected || espectador.OwnerId < 0)
                    continue;

                if (EnviarSaidaDutoDesync(alvo, entrada.Value, espectador))
                    saidasEnviadas++;
            }

            restaurados++;
        }

        jogadoresNoDuto.Clear();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Evento encerrado na reunião. Jogadores restaurados={restaurados}; " +
            $"saídas direcionadas={saidasEnviadas}.");
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        jogadoresNoDuto.Clear();
        saidasPendentes.Clear();
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    private static class ReuniaoInvisibilidadePatch
    {
        private static void Postfix() => EncerrarNaReuniao();
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
    private static class AtualizarSaidasInvisibilidadePatch
    {
        private static void Postfix()
        {
            if (ativo)
                ProcessarSaidasPendentes();
        }
    }
}

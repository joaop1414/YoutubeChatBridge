using System;
using System.Collections.Generic;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private const float IntervaloEntreSnapTo = 0.08f;
    private const float AtrasoAntesDasEntradas = 0.20f;
    private const float IntervaloEntreEntradas = 0.08f;
    private const float AtrasoRestaurarPosicao = 0.15f;
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
        public Vector2 PosicaoOriginal;
        public float RestaurarPosicaoEm;
        public float ExecutarEm;
    }

    private static readonly List<SaidaPendente> saidasPendentes = new();

    private sealed class JogadorPreparacao
    {
        public byte PlayerId;
        public Vector2 PosicaoOriginal;
        public bool SnapToConcluido;
    }

    private enum EtapaPreparacao
    {
        Nenhuma,
        SnapTo,
        Entradas
    }

    private static readonly List<JogadorPreparacao> preparacaoPendente = new();
    private static EtapaPreparacao etapaPreparacao = EtapaPreparacao.Nenhuma;
    private static int indicePreparacao;
    private static int ventPreparacaoId = -1;
    private static Vector2 posicaoVentPreparacao;
    private static float proximoProcessamentoPreparacao;

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
            AmongUsClient.Instance == null ||
            etapaPreparacao != EtapaPreparacao.Nenhuma)
            return;

        Vent ventGlobal = EncontrarVentGlobal();
        if (ventGlobal == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Invisibilidade] Não existe duto com ID 1 ou ID 0 para posicionar os jogadores.");
            return;
        }

        preparacaoPendente.Clear();
        saidasPendentes.Clear();
        jogadoresNoDuto.Clear();

        foreach (PlayerControl alvo in PlayerControl.AllPlayerControls)
        {
            if (!PodeFicarInvisivel(alvo) ||
                alvo.MyPhysics == null ||
                alvo.NetTransform == null)
                continue;

            preparacaoPendente.Add(new JogadorPreparacao
            {
                PlayerId = alvo.PlayerId,
                PosicaoOriginal = (Vector2)alvo.transform.position
            });
        }

        if (preparacaoPendente.Count == 0)
        {
            armado = false;
            return;
        }

        armado = false;
        ventPreparacaoId = ventGlobal.Id;
        posicaoVentPreparacao = (Vector2)ventGlobal.transform.position;
        indicePreparacao = 0;
        etapaPreparacao = EtapaPreparacao.SnapTo;
        proximoProcessamentoPreparacao = Time.realtimeSinceStartup;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Preparação iniciada. Motivo={motivo}; duto fixo={ventPreparacaoId}; " +
            $"jogadores={preparacaoPendente.Count}; intervalo entre SnapTo={IntervaloEntreSnapTo:0.00}s; " +
            $"espera antes das entradas={AtrasoAntesDasEntradas:0.00}s.");
    }

    // Move localmente no host e transmite o RPC nativo SnapTo para todos os clientes.
    // O RPC contém Vector2 (X/Y) e o novo sequence ID, exatamente nessa ordem.
    private static bool EnviarSnapToNaRede(PlayerControl alvo, Vector2 posicao)
    {
        if (AmongUsClient.Instance == null ||
            alvo == null ||
            alvo.NetTransform == null)
            return false;

        try
        {
            ushort sequenciaLocal = (ushort)(alvo.NetTransform.lastSequenceId + 128);
            alvo.NetTransform.SnapTo(posicao, sequenciaLocal);

            ushort sequenciaRede = (ushort)(alvo.NetTransform.lastSequenceId + 2);
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                alvo.NetTransform.NetId,
                (byte)RpcCalls.SnapTo,
                SendOption.Reliable
            );

            writer.Write(posicao.x);
            writer.Write(posicao.y);
            writer.Write(sequenciaRede);

            AmongUsClient.Instance.FinishRpcImmediately(writer);
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha ao transmitir SnapTo de PlayerId={alvo.PlayerId}: " +
                ex.GetBaseException().Message);
            return false;
        }
    }

    private static void ProcessarPreparacao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            etapaPreparacao == EtapaPreparacao.Nenhuma ||
            Time.realtimeSinceStartup < proximoProcessamentoPreparacao)
            return;

        if (indicePreparacao >= preparacaoPendente.Count)
        {
            if (etapaPreparacao == EtapaPreparacao.SnapTo)
            {
                etapaPreparacao = EtapaPreparacao.Entradas;
                indicePreparacao = 0;
                proximoProcessamentoPreparacao =
                    Time.realtimeSinceStartup + AtrasoAntesDasEntradas;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[Invisibilidade] Todos os SnapTo foram processados. " +
                    $"Aguardando {AtrasoAntesDasEntradas:0.00}s antes dos RPCs de entrada.");
                return;
            }

            etapaPreparacao = EtapaPreparacao.Nenhuma;
            preparacaoPendente.Clear();
            ventPreparacaoId = -1;
            return;
        }

        JogadorPreparacao item = preparacaoPendente[indicePreparacao];
        PlayerControl alvo = EncontrarJogador(item.PlayerId);

        if (etapaPreparacao == EtapaPreparacao.SnapTo)
        {
            if (PodeFicarInvisivel(alvo) && alvo.NetTransform != null)
            {
                try
                {
                    item.SnapToConcluido =
                        EnviarSnapToNaRede(alvo, posicaoVentPreparacao);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha no SnapTo de PlayerId={item.PlayerId}: " +
                        ex.GetBaseException().Message);
                }
            }

            indicePreparacao++;
            proximoProcessamentoPreparacao =
                Time.realtimeSinceStartup + IntervaloEntreSnapTo;
            return;
        }

        if (etapaPreparacao == EtapaPreparacao.Entradas)
        {
            if (item.SnapToConcluido &&
                PodeFicarInvisivel(alvo) &&
                alvo.MyPhysics != null &&
                alvo.NetTransform != null)
            {
                try
                {
                    // O ID é sempre o mesmo para todos; nenhum cliente escolhe outro duto.
                    alvo.MyPhysics.RpcEnterVent(ventPreparacaoId);

                    float agora = Time.realtimeSinceStartup;
                    jogadoresNoDuto[alvo.PlayerId] = ventPreparacaoId;
                    saidasPendentes.Add(new SaidaPendente
                    {
                        PlayerId = alvo.PlayerId,
                        VentId = ventPreparacaoId,
                        PosicaoOriginal = item.PosicaoOriginal,
                        RestaurarPosicaoEm = agora + AtrasoRestaurarPosicao,
                        ExecutarEm = agora + AtrasoSaida
                    });
                }
                catch (Exception ex)
                {
                    try
                    {
                        EnviarSnapToNaRede(alvo, item.PosicaoOriginal);
                    }
                    catch { }

                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Invisibilidade] Falha ao entrar no duto com PlayerId={item.PlayerId}: " +
                        ex.GetBaseException().Message);
                }
            }

            indicePreparacao++;
            proximoProcessamentoPreparacao =
                Time.realtimeSinceStartup + IntervaloEntreEntradas;
        }
    }

    private static Vent EncontrarVentGlobal()
    {
        if (ShipStatus.Instance?.AllVents == null)
            return null;

        Vent ventId1 = null;
        Vent ventId0 = null;

        foreach (Vent vent in ShipStatus.Instance.AllVents)
        {
            if (vent == null)
                continue;

            if (vent.Id == 1)
                ventId1 = vent;
            else if (vent.Id == 0)
                ventId0 = vent;
        }

        // Prioriza o duto 1. Se não existir, usa o duto 0.
        return ventId1 ?? ventId0;
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
            PlayerControl alvo = EncontrarJogador(pendente.PlayerId);

            // Depois que o RPC de entrada já foi enviado, devolve a posição
            // física do jogador ao ponto original sem desfazer ainda o estado do duto.
            if (agora >= pendente.RestaurarPosicaoEm &&
                pendente.RestaurarPosicaoEm > 0f)
            {
                pendente.RestaurarPosicaoEm = 0f;

                if (alvo != null && alvo.NetTransform != null)
                {
                    try
                    {
                        EnviarSnapToNaRede(alvo, pendente.PosicaoOriginal);
                    }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            $"[Invisibilidade] Falha ao restaurar posição de PlayerId={pendente.PlayerId}: {ex.GetBaseException().Message}");
                    }
                }
            }

            if (agora < pendente.ExecutarEm)
                continue;

            saidasPendentes.RemoveAt(i);

            alvo = EncontrarJogador(pendente.PlayerId);
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
        etapaPreparacao = EtapaPreparacao.Nenhuma;
        preparacaoPendente.Clear();
        indicePreparacao = 0;
        ventPreparacaoId = -1;
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
        etapaPreparacao = EtapaPreparacao.Nenhuma;
        preparacaoPendente.Clear();
        indicePreparacao = 0;
        ventPreparacaoId = -1;
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
            {
                ProcessarPreparacao();
                ProcessarSaidasPendentes();
            }
        }
    }
}

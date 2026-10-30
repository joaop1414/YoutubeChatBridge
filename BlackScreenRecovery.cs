using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using InnerNet;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

// Recuperação automática da tela preta após reuniões.
// Como o host não consegue ler o estado de renderização dos outros clientes,
// a falta contínua de movimento é usada como indício.
internal static class BlackScreenRecovery
{
    private const float TempoParadoParaPrimeiraTentativa = 5f;
    private const float IntervaloEntreTentativas = 5f;
    private const float DistanciaMinimaMovimento = 0.20f;
    private const float AtrasoReparo = 0.25f;

    private sealed class EstadoJogador
    {
        public Vector2 UltimaPosicao;
        public float UltimoMovimento;
        public float ProximaTentativa;
        public int Tentativas;
    }

    private static readonly Dictionary<byte, EstadoJogador> Jogadores = new();
    private static readonly Dictionary<byte, (PlayerControl Jogador, SystemTypes Sistema, float Quando)> ReparosPendentes = new();
    private static bool AguardandoFimDaReuniao;
    private static bool Monitorando;
    private static bool EncerramentoEmAndamento;

    public static void NotificarFimDaReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null)
            return;

        Jogadores.Clear();
        AguardandoFimDaReuniao = true;
        Monitorando = false;
        EncerramentoEmAndamento = false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Recuperação de tela preta: aguardando a transição terminar para monitorar o movimento dos jogadores.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null ||
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.IsGameStarted ||
            EncerramentoEmAndamento)
            return;

        ProcessarReparosPendentes();

        // A reunião e a tela de ejeção precisam ter desaparecido antes de iniciar
        // a contagem, para não confundir a animação de saída com falta de movimento.
        if (AguardandoFimDaReuniao)
        {
            if (MeetingHud.Instance != null ||
                UnityEngine.Object.FindObjectOfType<ExileController>() != null)
                return;

            IniciarMonitoramento();
        }

        if (!Monitorando)
            return;

        float agora = Time.realtimeSinceStartup;
        List<PlayerControl> vivos = ObterJogadoresVivos();

        if (vivos.Count == 0)
        {
            PararMonitoramento("não há jogadores vivos para monitorar");
            return;
        }

        foreach (PlayerControl jogador in vivos)
        {
            if (jogador == null || jogador.Data == null)
                continue;

            Vector2 posicao = jogador.transform.position;
            if (!Jogadores.TryGetValue(jogador.PlayerId, out EstadoJogador estado))
            {
                estado = new EstadoJogador
                {
                    UltimaPosicao = posicao,
                    UltimoMovimento = agora,
                    ProximaTentativa = agora + TempoParadoParaPrimeiraTentativa,
                    Tentativas = 0
                };
                Jogadores[jogador.PlayerId] = estado;
                continue;
            }

            if ((posicao - estado.UltimaPosicao).sqrMagnitude >=
                DistanciaMinimaMovimento * DistanciaMinimaMovimento)
            {
                estado.UltimaPosicao = posicao;
                estado.UltimoMovimento = agora;
                estado.Tentativas = 0;
                estado.ProximaTentativa = agora + TempoParadoParaPrimeiraTentativa;
                continue;
            }

            if (estado.Tentativas < 2 && agora >= estado.ProximaTentativa)
            {
                estado.Tentativas++;
                estado.ProximaTentativa = agora + IntervaloEntreTentativas;

                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Possível tela preta: {jogador.Data.PlayerName} (PlayerId={jogador.PlayerId}) " +
                    $"permaneceu parado por {agora - estado.UltimoMovimento:0.0}s. " +
                    $"Executando recuperação {estado.Tentativas}/2.");

                ExecutarRecuperacao(jogador, estado.Tentativas);
            }
        }

        // Só encerra se TODOS os jogadores vivos tiverem recebido as duas tentativas
        // e continuarem sem movimento durante o intervalo completo da segunda tentativa.
        bool todosSemResposta = vivos.All(j =>
            j != null &&
            Jogadores.TryGetValue(j.PlayerId, out EstadoJogador estado) &&
            estado.Tentativas >= 2 &&
            agora >= estado.ProximaTentativa);

        if (todosSemResposta)
            EncerrarPartidaComoTripulantesFantasma();
    }

    private static void IniciarMonitoramento()
    {
        AguardandoFimDaReuniao = false;
        Monitorando = true;
        Jogadores.Clear();

        float agora = Time.realtimeSinceStartup;
        foreach (PlayerControl jogador in ObterJogadoresVivos())
        {
            if (jogador == null || jogador.Data == null)
                continue;

            Jogadores[jogador.PlayerId] = new EstadoJogador
            {
                UltimaPosicao = jogador.transform.position,
                UltimoMovimento = agora,
                ProximaTentativa = agora + TempoParadoParaPrimeiraTentativa,
                Tentativas = 0
            };
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Monitoramento pós-reunião iniciado para {Jogadores.Count} jogador(es) vivo(s). " +
            "Primeira tentativa após 5s parado.");
    }

    private static List<PlayerControl> ObterJogadoresVivos()
    {
        List<PlayerControl> vivos = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            vivos.Add(jogador);
        }

        return vivos;
    }

    private static void ExecutarRecuperacao(PlayerControl jogador, int tentativa)
    {
        if (jogador == null || jogador.Data == null ||
            jogador.Data.IsDead || jogador.Data.Disconnected ||
            AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        try
        {
            SystemTypes sistema = ObterSistemaDeSabotagem();
            EnviarAtualizacaoSistemaDirecionada(jogador, sistema, 128);

            // Reaproveita a sequência já existente no mod:
            // ProtectPlayer + MurderPlayer(FailedProtected), somente no cliente dono.
            EventoEscudoTemporarioMortePatch.AplicarEscudoEResetarCooldown(jogador);

            ReparosPendentes[jogador.PlayerId] =
                (jogador, sistema, Time.realtimeSinceStartup + AtrasoReparo);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Recuperação {tentativa}/2 enviada para PlayerId={jogador.PlayerId}; " +
                $"sabotagem {sistema}, escudo local e tentativa de morte protegida.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha na recuperação de tela preta de PlayerId={jogador.PlayerId}: {ex}");
        }
    }

    private static SystemTypes ObterSistemaDeSabotagem()
    {
        // Os mapas Polus e Airship usam sistemas diferentes do reator padrão.
        try
        {
            var sistemas = ShipStatus.Instance.Systems;
            if (sistemas != null && sistemas.ContainsKey(SystemTypes.Laboratory))
                return SystemTypes.Laboratory;
            if (sistemas != null && sistemas.ContainsKey(SystemTypes.HeliSabotage))
                return SystemTypes.HeliSabotage;
        }
        catch { }

        return SystemTypes.Reactor;
    }

    private static void EnviarAtualizacaoSistemaDirecionada(
        PlayerControl jogador,
        SystemTypes sistema,
        byte quantidade)
    {
        if (jogador == null || ShipStatus.Instance == null ||
            AmongUsClient.Instance == null || jogador.OwnerId < 0)
            return;

        // O host não recebe de volta um RPC enviado para o próprio ClientId.
        // Nesse caso, tenta executar a atualização local pelo método do ShipStatus.
        if (jogador.AmOwner)
        {
            MethodInfo metodo = AccessTools.Method(typeof(ShipStatus), "UpdateSystem");
            if (metodo != null)
            {
                ParameterInfo[] parametros = metodo.GetParameters();
                object[] argumentos = new object[parametros.Length];

                for (int i = 0; i < parametros.Length; i++)
                {
                    Type tipo = parametros[i].ParameterType;
                    if (tipo == typeof(SystemTypes))
                        argumentos[i] = sistema;
                    else if (tipo == typeof(PlayerControl))
                        argumentos[i] = jogador;
                    else if (tipo == typeof(byte))
                        argumentos[i] = quantidade;
                    else if (tipo == typeof(int))
                        argumentos[i] = (int)quantidade;
                    else
                        argumentos[i] = tipo.IsValueType ? Activator.CreateInstance(tipo) : null;
                }

                metodo.Invoke(ShipStatus.Instance, argumentos);
                return;
            }
        }

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            ShipStatus.Instance.NetId,
            (byte)RpcCalls.UpdateSystem,
            SendOption.Reliable,
            jogador.OwnerId);

        writer.Write((byte)sistema);
        writer.WriteNetObject(jogador);
        writer.Write(quantidade);
        AmongUsClient.Instance.FinishRpcImmediately(writer);
    }

    private static void ProcessarReparosPendentes()
    {
        if (ReparosPendentes.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        foreach (byte playerId in ReparosPendentes.Keys.ToArray())
        {
            var pendente = ReparosPendentes[playerId];
            if (agora < pendente.Quando)
                continue;

            ReparosPendentes.Remove(playerId);

            PlayerControl jogador = pendente.Jogador;
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || ShipStatus.Instance == null ||
                AmongUsClient.Instance == null)
                continue;

            try
            {
                EnviarAtualizacaoSistemaDirecionada(jogador, pendente.Sistema, 16);

                // O Airship possui dois estágios de reparo para o helicóptero.
                if (pendente.Sistema == SystemTypes.HeliSabotage)
                    EnviarAtualizacaoSistemaDirecionada(jogador, pendente.Sistema, 17);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Falha ao reparar a sabotagem usada na recuperação: " + ex.Message);
            }
        }
    }

    private static void EncerrarPartidaComoTripulantesFantasma()
    {
        if (EncerramentoEmAndamento || !YouTubeVoteBridgePlugin.EhHost() ||
            GameManager.Instance == null || AmongUsClient.Instance == null)
            return;

        EncerramentoEmAndamento = true;
        Monitorando = false;
        AguardandoFimDaReuniao = false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogError(
            "RECUPERAÇÃO FALHOU: todos os jogadores vivos permaneceram parados após duas tentativas. " +
            "Encerrando a partida como vitória de tripulantes por tarefas.");

        try
        {
            // Converte todos, vivos ou mortos, para Fantasma Tripulante antes do fim.
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected)
                    continue;

                try
                {
                    RoleSwitchAPI.TrocarFuncao(jogador, RoleTypes.CrewmateGhost);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"Não foi possível definir Fantasma Tripulante para PlayerId={jogador.PlayerId}: {ex.Message}");
                }
            }

            // A chamada é explicitamente autorizada para passar pelos dois bloqueios
            // da opção "Sem fim de jogo".
            YouTubeVoteBridgePlugin.PermitirFimDeJogoForcado = true;

            MethodInfo shouldCheck = AccessTools.PropertySetter(
                typeof(GameManager), "ShouldCheckForGameEnd");
            try { shouldCheck?.Invoke(GameManager.Instance, new object[] { false }); }
            catch { }

            MethodInfo fim = AccessTools.Method(
                typeof(GameManager), nameof(GameManager.RpcEndGame));

            if (fim == null)
                throw new MissingMethodException("GameManager.RpcEndGame não foi encontrado.");

            ParameterInfo[] parametros = fim.GetParameters();
            object[] argumentos = new object[parametros.Length];

            for (int i = 0; i < parametros.Length; i++)
            {
                Type tipo = parametros[i].ParameterType;
                if (tipo == typeof(GameOverReason))
                    argumentos[i] = GameOverReason.CrewmatesByTask;
                else if (tipo == typeof(bool))
                    argumentos[i] = false;
                else if (parametros[i].HasDefaultValue)
                    argumentos[i] = parametros[i].DefaultValue;
                else
                    argumentos[i] = tipo.IsValueType ? Activator.CreateInstance(tipo) : null;
            }

            fim.Invoke(GameManager.Instance, argumentos);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao encerrar a partida pela proteção contra tela preta: " + ex);
            EncerramentoEmAndamento = false;
        }
        finally
        {
            YouTubeVoteBridgePlugin.PermitirFimDeJogoForcado = false;
        }
    }

    private static void PararMonitoramento(string motivo)
    {
        AguardandoFimDaReuniao = false;
        Monitorando = false;
        Jogadores.Clear();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Monitoramento de tela preta encerrado: " + motivo + ".");
    }
}

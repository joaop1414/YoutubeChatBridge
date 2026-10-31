using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoRoomRush : EventoVotacao
{
    private const float TEMPO_POR_RODADA = 15f;
    private const float INTERVALO_SINCRONIZACAO = 0.5f;

    private static bool ativo;
    private static float prazo;
    private static float proximaSincronizacao;
    private static SystemTypes salaAlvo;
    private static SystemTypes salaAlvoLocal;
    private static float prazoLocal;
    private static bool ativoLocal;
    private static bool reparoReatorPendente;
    private static float repararReatorAte;

    public override string Nome => "Vá para a sala ou morra";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ShipStatus.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] O evento só pode ser iniciado durante uma partida.");
            return;
        }

        if (!EscolherSala())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Não foi possível encontrar salas válidas no mapa.");
            Resetar();
            return;
        }

        ativo = true;
        IniciarAvisoReator();
        AplicarEscudoERomperParaTodos();
        prazo = Time.realtimeSinceStartup + TEMPO_POR_RODADA;
        proximaSincronizacao = 0f;
        ReceberEstadoLocal(salaAlvo, TEMPO_POR_RODADA, true);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Iniciado. Os jogadores devem chegar à sala indicada em 15 segundos.");
        SincronizarEstado();
    }

    internal static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ShipStatus.Instance == null || AmongUsClient.Instance == null)
        {
            Resetar();
            return;
        }

        AtualizarReparoReator();

        if (MeetingHud.Instance != null || ExileController.Instance != null)
            return;

        float agora = Time.realtimeSinceStartup;
        if (agora >= proximaSincronizacao)
            SincronizarEstado();

        if (agora < prazo)
            return;

        int mortos = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer)
                continue;

            try
            {
                PlainShipRoom salaJogador = ObterSalaJogador(jogador.GetTruePosition());
                if (salaJogador != null && salaJogador.RoomId == salaAlvo)
                    continue;

                jogador.RpcMurderPlayer(jogador, true);
                mortos++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Falha ao eliminar PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Rodada encerrada! " + mortos +
            " jogador(es) eliminado(s) por não chegar à sala.");
        Resetar();
    }

    private static void IniciarAvisoReator()
    {
        reparoReatorPendente = false;
        repararReatorAte = 0f;

        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (navio == null ||
                !navio.Systems.ContainsKey(SystemTypes.Sabotage) ||
                !navio.Systems.ContainsKey(SystemTypes.Reactor))
                return;

            if (ReatorEstaEmSabotagem(navio.Systems[SystemTypes.Reactor]))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F6/Room Rush] O reator já está em sabotagem; não será reiniciado nem reparado.");
                return;
            }

            SabotageSystemType sabotagem = navio.Systems[SystemTypes.Sabotage]
                .Cast<SabotageSystemType>();
            if (sabotagem == null)
                return;

            sabotagem.Timer = 0f;
            sabotagem.IsDirty = true;
            navio.RpcUpdateSystem(SystemTypes.Sabotage, (byte)SystemTypes.Reactor);

            reparoReatorPendente = true;
            repararReatorAte = Time.realtimeSinceStartup + 0.5f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F6/Room Rush] Aviso do reator acionado; reparo programado para 0,5 segundo.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao acionar o aviso do reator: " + ex.Message);
        }
    }

    private static bool ReatorEstaEmSabotagem(object sistemaReator)
    {
        if (sistemaReator == null)
            return false;

        try
        {
            Type tipo = sistemaReator.GetType();
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic;

            var propriedadeAtiva = tipo.GetProperty("IsActive", flags);
            if (propriedadeAtiva != null &&
                propriedadeAtiva.PropertyType == typeof(bool))
                return (bool)propriedadeAtiva.GetValue(sistemaReator);

            var campoAtivo = tipo.GetField("IsActive", flags);
            if (campoAtivo != null && campoAtivo.FieldType == typeof(bool))
                return (bool)campoAtivo.GetValue(sistemaReator);

            var propriedadeContagem = tipo.GetProperty("Countdown", flags);
            if (propriedadeContagem != null)
            {
                object valor = propriedadeContagem.GetValue(sistemaReator);
                if (valor != null)
                    return Convert.ToSingle(valor) > 0f;
            }

            var campoContagem = tipo.GetField("Countdown", flags);
            if (campoContagem != null)
            {
                object valor = campoContagem.GetValue(sistemaReator);
                if (valor != null)
                    return Convert.ToSingle(valor) > 0f;
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Não foi possível consultar o estado do reator: " + ex.Message);
            // Na dúvida, não inicia outra sabotagem.
            return true;
        }

        // Se a versão do jogo não expuser o estado conhecido, não arrisca
        // interromper uma sabotagem que já esteja ativa.
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "[F6/Room Rush] Não foi possível identificar o estado do reator; aviso não acionado.");
        return true;
    }

    private static void AtualizarReparoReator()
    {
        if (!reparoReatorPendente || Time.realtimeSinceStartup < repararReatorAte)
            return;

        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (navio != null && navio.Systems.ContainsKey(SystemTypes.Reactor))
            {
                navio.RpcUpdateSystem(SystemTypes.Reactor, 16);
                navio.RpcUpdateSystem(SystemTypes.Reactor, 17);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F6/Room Rush] Reparo do reator enviado.");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao reparar o reator: " + ex.Message);
        }
        finally
        {
            reparoReatorPendente = false;
            repararReatorAte = 0f;
        }
    }

    private static void AplicarEscudoERomperParaTodos()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null)
            return;

        const int corEscudo = 0;
        int aplicados = 0;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer || jogador.OwnerId < 0)
                continue;

            try
            {
                if (jogador.AmOwner)
                {
                    jogador.ProtectPlayer(jogador, corEscudo);
                }
                else
                {
                    MessageWriter escudo = AmongUsClient.Instance.StartRpcImmediately(
                        jogador.NetId,
                        (byte)RpcCalls.ProtectPlayer,
                        SendOption.Reliable,
                        jogador.OwnerId
                    );
                    escudo.WriteNetObject(jogador);
                    escudo.Write(corEscudo);
                    AmongUsClient.Instance.FinishRpcImmediately(escudo);
                }

                // A tentativa de morte é enviada apenas ao cliente dono do jogador.
                // FailedProtected reproduz o rompimento do escudo sem matar.
                MessageWriter quebraEscudo = AmongUsClient.Instance.StartRpcImmediately(
                    jogador.NetId,
                    (byte)RpcCalls.MurderPlayer,
                    SendOption.Reliable,
                    jogador.OwnerId
                );
                quebraEscudo.WriteNetObject(jogador);
                quebraEscudo.Write((int)MurderResultFlags.FailedProtected);
                AmongUsClient.Instance.FinishRpcImmediately(quebraEscudo);
                aplicados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Falha ao reproduzir escudo para PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Efeito de escudo e rompimento enviado para " +
            aplicados + " jogador(es).");
    }

    private static void SincronizarEstado()
    {
        float restante = Mathf.Max(0f, prazo - Time.realtimeSinceStartup);
        YouTubeVoteBridgePlugin.SincronizarRoomRush((byte)salaAlvo, restante, true);
        ReceberEstadoLocal(salaAlvo, restante, true);
        proximaSincronizacao = Time.realtimeSinceStartup + INTERVALO_SINCRONIZACAO;
    }

    internal static void ReceberEstadoLocal(SystemTypes sala, float segundosRestantes, bool eventoAtivo)
    {
        ativoLocal = eventoAtivo;
        if (!eventoAtivo)
        {
            prazoLocal = 0f;
            return;
        }

        salaAlvoLocal = sala;
        prazoLocal = Time.realtimeSinceStartup + Mathf.Max(0f, segundosRestantes);
    }

    internal static string ObterTextoHud()
    {
        if (!ativoLocal || ShipStatus.Instance == null ||
            MeetingHud.Instance != null || ExileController.Instance != null)
            return "";

        int segundos = Mathf.Max(0, Mathf.CeilToInt(prazoLocal - Time.realtimeSinceStartup));
        PlainShipRoom salaJogador = PlayerControl.LocalPlayer == null
            ? null
            : ObterSalaJogador(PlayerControl.LocalPlayer.GetTruePosition());
        bool estaNaSala = salaJogador != null && salaJogador.RoomId == salaAlvoLocal;

        string cor = estaNaSala ? "green" : segundos <= 5 ? "red" : "yellow";
        return "<color=" + cor + ">Sala: " + NomeSala(salaAlvoLocal) +
            " (" + segundos + "S)</color>";
    }

    private static PlainShipRoom ObterSalaJogador(Vector2 posicao)
    {
        ShipStatus navio = ShipStatus.Instance;
        if (navio == null || navio.AllRooms == null)
            return null;

        PlainShipRoom melhorSala = null;
        float menorArea = float.MaxValue;
        foreach (PlainShipRoom sala in navio.AllRooms)
        {
            if (sala == null || sala.roomArea == null ||
                sala.RoomId == SystemTypes.Hallway || sala.RoomId == SystemTypes.Outside)
                continue;

            try
            {
                if (!sala.roomArea.OverlapPoint(posicao))
                    continue;

                Vector2 tamanho = sala.roomArea.bounds.size;
                float area = tamanho.x * tamanho.y;
                if (area < menorArea)
                {
                    menorArea = area;
                    melhorSala = sala;
                }
            }
            catch { }
        }

        return melhorSala;
    }

    private static bool EscolherSala()
    {
        if (ShipStatus.Instance == null || ShipStatus.Instance.AllRooms == null)
            return false;

        List<SystemTypes> salas = new();
        foreach (PlainShipRoom sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || sala.roomArea == null ||
                sala.RoomId.ToString() == "None" ||
                sala.RoomId == SystemTypes.Hallway ||
                sala.RoomId == SystemTypes.Outside)
                continue;

            if (!salas.Contains(sala.RoomId))
                salas.Add(sala.RoomId);
        }

        if (salas.Count == 0)
            return false;

        salaAlvo = salas[System.Random.Shared.Next(salas.Count)];
        return true;
    }

    private static string NomeSala(SystemTypes sala)
    {
        try
        {
            string traduzido = TranslationController.Instance.GetString(sala);
            if (!string.IsNullOrWhiteSpace(traduzido))
                return traduzido;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[F6/Room Rush] Falha ao traduzir nome da sala: " + ex.Message);
        }

        return sala.ToString();
    }

    internal static void Resetar()
    {
        if (ativo && YouTubeVoteBridgePlugin.EhHost())
            YouTubeVoteBridgePlugin.SincronizarRoomRush(0, 0f, false);

        ativo = false;
        if (reparoReatorPendente)
            AtualizarReparoReator();
        reparoReatorPendente = false;
        repararReatorAte = 0f;
        prazo = 0f;
        proximaSincronizacao = 0f;
        ReceberEstadoLocal(salaAlvoLocal, 0f, false);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
internal static class EventoRoomRushAtualizarPatch
{
    private static void Postfix()
    {
        EventoRoomRush.Atualizar();
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class EventoRoomRushFimPatch
{
    private static void Postfix()
    {
        EventoRoomRush.Resetar();
    }
}

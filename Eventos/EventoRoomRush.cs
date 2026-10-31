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

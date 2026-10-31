using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoRoomRush : EventoVotacao
{
    private const float TEMPO_POR_RODADA = 15f;
    private static bool ativo;
    private static float prazo;
    private static SystemTypes salaAlvo;
    private static bool salaEscolhida;

    public override string Nome => "Room Rush (vá para a sala indicada antes do tempo acabar)";

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

        ativo = true;
        salaEscolhida = false;
        prazo = 0f;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Iniciado. Os jogadores precisam alcançar a sala anunciada antes do prazo.");
        Atualizar();
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
        if (!salaEscolhida)
        {
            if (agora < prazo)
                return;

            if (!EscolherSala())
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Não foi possível encontrar salas válidas no mapa.");
                Resetar();
                return;
            }

            salaEscolhida = true;
            prazo = agora + TEMPO_POR_RODADA;
            Anunciar("<color=#FFB347>[ROOM RUSH]</color> Vá para <color=yellow>" +
                NomeSala(salaAlvo) + "</color>! Você tem <color=red>" +
                TEMPO_POR_RODADA.ToString("0") + " segundos</color> ou morrerá!");
            return;
        }

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
                // A sala é obtida pelo mapa usando a posição real do jogador.
                // GetPlainShipRoom() não é um método de PlayerControl nesta versão.
                PlainShipRoom salaJogador = ShipStatus.Instance.GetRoom(jogador.GetTruePosition());
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

        Anunciar("<color=#FFB347>[ROOM RUSH]</color> Tempo esgotado! " +
            mortos + " jogador(es) eliminado(s) por não chegar à sala.");
        salaEscolhida = false;
        prazo = agora + 2f;
    }

    private static bool EscolherSala()
    {
        if (ShipStatus.Instance == null || ShipStatus.Instance.AllRooms == null)
            return false;

        List<SystemTypes> salas = new();
        foreach (PlainShipRoom sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || sala.RoomId.ToString() == "None")
                continue;

            if (!salas.Contains(sala.RoomId))
                salas.Add(sala.RoomId);
        }

        if (salas.Count == 0)
            return false;

        // Evita repetir a mesma sala em rodadas consecutivas quando há alternativas.
        if (salas.Count > 1)
            salas.Remove(salaAlvo);

        salaAlvo = salas[System.Random.Shared.Next(salas.Count)];
        return true;
    }

    private static string NomeSala(SystemTypes sala)
    {
        return sala.ToString() switch
        {
            "Cafeteria" => "Cafeteria",
            "Weapons" => "Armas",
            "Navigation" => "Navegação",
            "O2" => "O2",
            "Shields" => "Escudos",
            "Communications" or "Comms" => "Comunicações",
            "Storage" => "Armazenamento",
            "Admin" => "Administração",
            "Electrical" => "Elétrica",
            "LifeSupp" or "MedBay" or "Medical" => "Enfermaria",
            "Security" => "Segurança",
            "Reactor" => "Reator",
            "LowerEngine" => "Motor inferior",
            "UpperEngine" => "Motor superior",
            "Laboratory" => "Laboratório",
            "Greenhouse" => "Estufa",
            "Balcony" => "Varanda",
            "Dropship" => "Dropship",
            "Decontamination" => "Descontaminação",
            "Launchpad" => "Plataforma de lançamento",
            "Specimens" => "Espécimes",
            "VaultRoom" => "Cofre",
            "Cockpit" => "Cabine",
            "Armory" => "Arsenal",
            "Kitchen" => "Cozinha",
            "ViewingDeck" => "Deque de observação",
            "HallOfPortraits" => "Galeria de retratos",
            "CargoBay" => "Baía de carga",
            "Showers" => "Chuveiros",
            "Lounge" => "Salão",
            "GapRoom" => "Sala do vão",
            "MainHall" => "Salão principal",
            _ => sala.ToString()
        };
    }

    private static void Anunciar(string texto)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.notRealPlayer)
                continue;

            YouTubeVoteBridgePlugin.EnviarMensagemIndividual(jogador, texto);
        }
    }

    internal static void Resetar()
    {
        ativo = false;
        salaEscolhida = false;
        prazo = 0f;
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

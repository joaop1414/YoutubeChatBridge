using System;
using System.Collections.Generic;
using Hazel;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoRoomRush : EventoVotacao
{
    private const float TEMPO_POR_RODADA = 15f;
    private const float INTERVALO_AVISO_NOME = 0.5f;
    private static bool ativo;
    private static float prazo;
    private static float proximaAtualizacaoAvisoNome;
    private static SystemTypes salaAlvo;
    private static readonly Dictionary<byte, string> nomesOriginais = new();
    private static readonly HashSet<byte> nomesAvisadosRede = new();

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

        RestaurarNomes();
        ativo = true;
        prazo = Time.realtimeSinceStartup + TEMPO_POR_RODADA;
        proximaAtualizacaoAvisoNome = 0f;

        if (!EscolherSala())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Não foi possível encontrar salas válidas no mapa.");
            Resetar();
            return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Iniciado. Esta rodada é única; os avisos serão exibidos abaixo dos nomes dos jogadores.");
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
        AtualizarAvisosNosNomes(agora);

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

        // O evento termina aqui: não há escolha de outra sala nem segunda rodada.
        Resetar();
    }

    private static void AtualizarAvisosNosNomes(float agora)
    {
        if (agora < proximaAtualizacaoAvisoNome ||
            PlayerControl.AllPlayerControls == null)
            return;

        proximaAtualizacaoAvisoNome = agora + INTERVALO_AVISO_NOME;
        int segundosRestantes = Math.Max(0, Mathf.CeilToInt(prazo - agora));

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.notRealPlayer)
                continue;

            if (!nomesOriginais.TryGetValue(jogador.PlayerId, out string nomeOriginal))
            {
                nomeOriginal = jogador.Data.PlayerName ??
                    jogador.cosmetics?.nameText?.text ?? "";
                nomesOriginais[jogador.PlayerId] = nomeOriginal;
            }

            PlainShipRoom salaJogador = ObterSalaJogador(jogador.GetTruePosition());
            bool estaNaSala = salaJogador != null && salaJogador.RoomId == salaAlvo;
            string cor = estaNaSala ? "green" : segundosRestantes <= 5 ? "red" : "yellow";
            string aviso = "\\n<color=" + cor + ">ROOM RUSH: " +
                NomeSala(salaAlvo) + " (" + segundosRestantes + "s)</color>";
            string nomeComAviso = nomeOriginal + aviso;

            try
            {
                if (jogador.cosmetics?.nameText != null)
                    jogador.cosmetics.nameText.text = nomeComAviso;

                if (jogador.AmOwner)
                    YouTubeVoteBridgePlugin.DefinirNomeLocal(jogador, nomeComAviso);
                else if (DefinirNomeLocalParaCliente(jogador, nomeComAviso))
                    nomesAvisadosRede.Add(jogador.PlayerId);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[F6/Room Rush] Não foi possível atualizar o aviso de nome: " + ex.Message);
            }
        }
    }

    private static bool DefinirNomeLocalParaCliente(PlayerControl jogador, string nome)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            jogador == null || jogador.Data == null || jogador.OwnerId < 0 ||
            jogador.OwnerId == AmongUsClient.Instance.ClientId)
            return false;

        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(jogador.OwnerId);
            writer.StartMessage(2);
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();
            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao enviar aviso local para PlayerId=" +
                jogador.PlayerId + ": " + ex.Message);
            return false;
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
    }

    private static void RestaurarNomes()
    {
        if (PlayerControl.AllPlayerControls != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null)
                    continue;

                if (!nomesOriginais.TryGetValue(jogador.PlayerId, out string nomeOriginal))
                    continue;

                try
                {
                    if (jogador.AmOwner)
                        YouTubeVoteBridgePlugin.DefinirNomeLocal(jogador, nomeOriginal);

                    if (jogador.cosmetics?.nameText != null)
                        jogador.cosmetics.nameText.text = nomeOriginal;

                    if (YouTubeVoteBridgePlugin.EhHost() &&
                        AmongUsClient.Instance != null &&
                        jogador.OwnerId >= 0 &&
                        jogador.OwnerId != AmongUsClient.Instance.ClientId &&
                        nomesAvisadosRede.Contains(jogador.PlayerId))
                        DefinirNomeLocalParaCliente(jogador, nomeOriginal);
                }
                catch { }
            }
        }

        nomesOriginais.Clear();
        nomesAvisadosRede.Clear();
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
                sala.RoomId == SystemTypes.None ||
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
            // Este overload recebe SystemTypes e usa diretamente a localização
            // ativa do Among Us (incluindo Português do Brasil).
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
        ativo = false;
        prazo = 0f;
        proximaAtualizacaoAvisoNome = 0f;
        RestaurarNomes();
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

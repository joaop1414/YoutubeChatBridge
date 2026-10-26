using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Evento F7: descargas de raio durante a partida. Um jogador atingido
/// comete suicídio usando o mesmo RPC do evento de suicídio aleatório.
/// </summary>
internal sealed class EventoRaio : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static float proximoRaioEm;
    private static readonly System.Random Sorteador = new();

    private const float IntervaloMinimo = 5f;
    private const float IntervaloMaximo = 10f;
    private const float AlcanceAtingido = 0.85f;

    public override string Nome => "Raio";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        armado = true;
        if (ShipStatus.Instance != null && LobbyTagManager.PartidaIniciada())
            IniciarPartida();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Evento armado; ficará ativo até o fim da partida.");
    }

    internal static void IniciarPartida()
    {
        if (!armado || !YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null)
            return;

        ativo = true;
        AgendarProximoRaio();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Evento ativado no início da partida.");
    }

    internal static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        if (MeetingHud.Instance != null)
            return;

        if (Time.realtimeSinceStartup < proximoRaioEm)
            return;

        try
        {
            ExecutarDescarga();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Raio] Falha ao processar descarga: " + ex);
        }
        finally
        {
            AgendarProximoRaio();
        }
    }

    private static void ExecutarDescarga()
    {
        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.IsDead && !jogador.Data.Disconnected)
                vivos.Add(jogador);
        }

        if (vivos.Count == 0)
            return;

        // Escolhe uma coordenada aleatória próxima da área ocupada por um jogador.
        // O acerto depende da distância entre o jogador e o ponto da descarga.
        PlayerControl referencia = vivos[Sorteador.Next(vivos.Count)];
        Vector2 centro = referencia.transform.position;
        Vector2 localDoRaio = centro + new Vector2(
            (float)(Sorteador.NextDouble() * 2.8 - 1.4),
            (float)(Sorteador.NextDouble() * 2.0 - 1.0));

        foreach (PlayerControl jogador in vivos)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            if (Vector2.Distance((Vector2)jogador.transform.position, localDoRaio) > AlcanceAtingido)
                continue;

            try
            {
                // Idêntico ao evento "Um jogador aleatório comete suicídio".
                jogador.RpcMurderPlayer(jogador, true);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Raio] " + jogador.Data.PlayerName +
                    " foi atingido por um raio e cometeu suicídio.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Raio] Falha ao aplicar suicídio em PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }
    }

    private static void AgendarProximoRaio()
    {
        proximoRaioEm = Time.realtimeSinceStartup +
            (float)(IntervaloMinimo +
            Sorteador.NextDouble() * (IntervaloMaximo - IntervaloMinimo));
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        proximoRaioEm = 0f;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTeleporteAleatorio : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    public override string Nome => "Teleporte aleatório para uma sala";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ShipStatus.Instance == null) return;
        List<Component> salas = ObterSalas(ShipStatus.Instance);
        if (salas.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Teleporte aleatório cancelado: não consegui encontrar salas no mapa atual.");
            return;
        }

        int movidos = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null || jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.NetTransform == null || jogador.MyPhysics == null ||
                jogador.inMovingPlat || jogador.onLadder)
                continue;

            Component sala = salas[Sorteador.Next(salas.Count)];
            Vector2 destino = EscolherPontoNaSala(sala);
            try
            {
                jogador.NetTransform.RpcSnapTo(destino);
                movidos++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Falha ao teleportar " + jogador.Data.PlayerName + " para uma sala: " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Teleporte aleatório concluído: " + movidos + " jogadores enviados para salas aleatórias.");
    }

    private static List<Component> ObterSalas(ShipStatus navio)
    {
        List<Component> resultado = new();
        try
        {
            Type tipo = navio.GetType();
            object colecao =
                tipo.GetProperty("AllRooms", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(navio) ??
                tipo.GetField("AllRooms", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(navio);
            if (colecao == null) return resultado;

            if (colecao is IEnumerable enumerable)
            {
                foreach (object item in enumerable)
                    AdicionarSala(resultado, item);
            }
            else
            {
                MethodInfo getEnumerator = colecao.GetType().GetMethod("GetEnumerator",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object enumerador = getEnumerator?.Invoke(colecao, null);
                if (enumerador == null) return resultado;
                MethodInfo moveNext = enumerador.GetType().GetMethod("MoveNext");
                PropertyInfo current = enumerador.GetType().GetProperty("Current");
                if (moveNext == null || current == null) return resultado;
                while ((bool)moveNext.Invoke(enumerador, null))
                    AdicionarSala(resultado, current.GetValue(enumerador));
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Erro ao listar salas do mapa: " + ex.Message);
        }
        return resultado;
    }

    private static void AdicionarSala(List<Component> resultado, object item)
    {
        if (item is not Component sala || sala == null) return;
        object id =
            sala.GetType().GetProperty("RoomId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sala) ??
            sala.GetType().GetField("RoomId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sala);
        if (id != null && id.ToString().Equals("Outside", StringComparison.OrdinalIgnoreCase))
            return;
        if (!resultado.Contains(sala)) resultado.Add(sala);
    }

    private static Vector2 EscolherPontoNaSala(Component sala)
    {
        Collider2D area = null;
        try
        {
            object areaObj =
                sala.GetType().GetProperty("roomArea", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sala) ??
                sala.GetType().GetField("roomArea", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(sala);
            area = areaObj as Collider2D;
            if (area == null) area = sala.GetComponentInChildren<Collider2D>();
        }
        catch { }

        if (area != null)
        {
            Bounds limites = area.bounds;
            for (int tentativa = 0; tentativa < 40; tentativa++)
            {
                Vector2 ponto = new(
                    UnityEngine.Random.Range(limites.min.x, limites.max.x),
                    UnityEngine.Random.Range(limites.min.y, limites.max.y));
                if (area.OverlapPoint(ponto))
                    return ponto;
            }
        }

        return sala.transform.position;
    }
}
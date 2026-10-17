using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Primeiro evento da categoria de eventos que afetam a partida inteira.
/// A ativação fica pendente se selecionada antes da criação do mapa.
/// </summary>
internal sealed class EventoOportunista : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static byte playerIdOportunista = byte.MaxValue;
    private static bool imortal;
    private static bool morreu;
    private static bool avisoTasksEnviado;

    public override string Nome => "Oportunista";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        armado = true;
        ativo = false;
        playerIdOportunista = byte.MaxValue;
        imortal = false;
        morreu = false;
        avisoTasksEnviado = false;

        if (ShipStatus.Instance != null)
            IniciarPartida();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Oportunista] Evento armado. Um tripulante será escolhido quando a partida estiver pronta.");
    }

    public static void IniciarPartida()
    {
        if (!armado || ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null)
            return;

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead ||
                jogador.Data.Role == null || jogador.Data.Role.IsImpostor)
                continue;

            candidatos.Add(jogador);
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Oportunista] Não foi encontrado nenhum tripulante elegível.");
            armado = false;
            return;
        }

        PlayerControl escolhido = candidatos[Random.Shared.Next(candidatos.Count)];
        playerIdOportunista = escolhido.PlayerId;
        ativo = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Oportunista] Escolhido {escolhido.Data.PlayerName} (PlayerId={escolhido.PlayerId}).");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ShipStatus.Instance == null)
        {
            if (ativo || armado)
                Resetar();
            return;
        }

        if (armado && !ativo)
            IniciarPartida();

        if (!ativo)
            return;

        PlayerControl alvo = EncontrarOportunista();
        if (alvo == null || alvo.Data == null)
            return;

        if (alvo.Data.IsDead || alvo.Data.Disconnected)
        {
            if (!morreu)
            {
                morreu = true;
                imortal = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Oportunista] O jogador escolhido morreu; a imunidade foi removida.");
            }
            return;
        }

        if (!imortal && TodasAsTarefasConcluidas(alvo))
        {
            imortal = true;
            avisoTasksEnviado = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Oportunista] {alvo.Data.PlayerName} concluiu todas as tarefas e agora está imune a kills de impostores (não a ejeções).");
            YouTubeVoteBridgePlugin.Anunciar(
                "O Oportunista concluiu todas as tarefas e ficou imune a kills de impostores.");
        }
    }

    internal static bool DeveRecusarKill(PlayerControl alvo)
    {
        return ativo && imortal && !morreu && alvo != null &&
            alvo.PlayerId == playerIdOportunista && alvo.Data != null &&
            !alvo.Data.IsDead && !alvo.Data.Disconnected;
    }

    internal static bool EhOportunista(PlayerControl jogador) =>
        ativo && jogador != null && jogador.PlayerId == playerIdOportunista;

    private static PlayerControl EncontrarOportunista()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerIdOportunista)
                return jogador;
        return null;
    }

    private static bool TodasAsTarefasConcluidas(PlayerControl jogador)
    {
        try
        {
            object tarefas = LerMembro(jogador.Data, "Tasks", "tasks");
            if (tarefas == null)
                tarefas = LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks");
            if (tarefas == null)
                return false;

            int total = 0;
            Type tipo = tarefas.GetType();
            object countValor = LerMembro(tarefas, "Count", "count", "Length", "length");
            if (countValor == null)
                return false;

            int count = Convert.ToInt32(countValor);
            PropertyInfo item = tipo.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getItem = item?.GetGetMethod(true);
            if (getItem == null)
                return false;

            for (int i = 0; i < count; i++)
            {
                object tarefa;
                try { tarefa = getItem.Invoke(tarefas, new object[] { i }); }
                catch { return false; }
                if (tarefa == null)
                    continue;

                total++;
                object completa = LerMembro(tarefa, "Complete", "complete", "IsComplete", "isComplete");
                if (completa == null || !Convert.ToBoolean(completa))
                    return false;
            }

            return total > 0;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Oportunista] Não foi possível verificar as tarefas: " + ex.GetBaseException().Message);
            return false;
        }
    }

    private static object LerMembro(object objeto, params string[] nomes)
    {
        if (objeto == null)
            return null;

        Type tipo = objeto.GetType();
        foreach (string nome in nomes)
        {
            try
            {
                PropertyInfo propriedade = AccessTools.Property(tipo, nome);
                if (propriedade != null)
                    return propriedade.GetValue(objeto);
                FieldInfo campo = AccessTools.Field(tipo, nome);
                if (campo != null)
                    return campo.GetValue(objeto);
            }
            catch { }
        }
        return null;
    }

    private static void Resetar()
    {
        armado = false;
        ativo = false;
        playerIdOportunista = byte.MaxValue;
        imortal = false;
        morreu = false;
        avisoTasksEnviado = false;
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class OportunistaImunidadeKillPatch
{
    public static bool Prefix(PlayerControl __instance, PlayerControl target)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !EventoOportunista.DeveRecusarKill(target))
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Oportunista] Kill recusada: atacante={__instance?.PlayerId}, alvo={target.PlayerId} (imune após concluir tarefas).");
        return false;
    }
}

[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class OportunistaInicioPartidaPatch
{
    public static void Postfix()
    {
        EventoOportunista.IniciarPartida();
    }
}

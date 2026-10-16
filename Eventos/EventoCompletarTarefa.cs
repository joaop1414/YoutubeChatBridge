using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

internal sealed class EventoCompletarTarefa : EventoVotacao
{
    private readonly bool todos;

    public EventoCompletarTarefa(bool todos) => this.todos = todos;

    public override string Nome => todos
        ? "Completar 1 tarefa para todos"
        : "Completar 1 tarefa para um jogador aleatório";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null ||
            PlayerControl.AllPlayerControls == null)
            return;

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;
            jogadores.Add(jogador);
        }

        if (jogadores.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Evento completar tarefa cancelado: nenhum jogador vivo.");
            return;
        }

        if (todos)
        {
            foreach (PlayerControl jogador in jogadores)
                CompletarUmaTarefa(jogador);
        }
        else
        {
            PlayerControl alvo = jogadores[Random.Shared.Next(jogadores.Count)];
            CompletarUmaTarefa(alvo);
        }
    }

    private static void CompletarUmaTarefa(PlayerControl jogador)
    {
        try
        {
            object tarefas = LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks");
            List<object> lista = EnumerarColecaoIl2Cpp(tarefas);
            List<object> incompletas = lista
                .Where(t => t != null && !LerBool(t, "IsComplete", "isComplete", "Complete", "complete"))
                .ToList();

            if (incompletas.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Evento completar tarefa: {jogador.Data.PlayerName} não tem tarefas incompletas.");
                return;
            }

            object tarefa = incompletas[Random.Shared.Next(incompletas.Count)];
            object id = LerMembro(tarefa, "Id", "id", "TaskId", "taskId");
            MethodInfo metodo = typeof(PlayerControl).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "RpcCompleteTask" && m.GetParameters().Length == 1)
                ?? typeof(PlayerControl).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "CompleteTask" && m.GetParameters().Length == 1);

            if (id == null || metodo == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Evento completar tarefa: não encontrei ID ou método de conclusão para {jogador.Data.PlayerName}.");
                return;
            }

            Type tipoParametro = metodo.GetParameters()[0].ParameterType;
            object idConvertido = tipoParametro.IsInstanceOfType(id)
                ? id
                : Convert.ChangeType(id, Nullable.GetUnderlyingType(tipoParametro) ?? tipoParametro);
            metodo.Invoke(jogador, new[] { idConvertido });

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Evento completar tarefa: tarefa ID={id}, tipo={LerMembro(tarefa, "TaskType", "taskType", "Type", "type")}, jogador={jogador.Data.PlayerName}, PlayerId={jogador.PlayerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Evento completar tarefa falhou para PlayerId={jogador?.PlayerId}: {ex.GetBaseException().Message}");
        }
    }

    private static List<object> EnumerarColecaoIl2Cpp(object colecao)
    {
        List<object> itens = new();
        if (colecao == null) return itens;
        try
        {
            Type tipo = colecao.GetType();
            object countObj = LerMembro(colecao, "Count", "count", "Length", "length");
            if (countObj == null) return itens;
            int count = Convert.ToInt32(countObj);
            PropertyInfo indexador = tipo.GetProperty("Item",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getItem = indexador?.GetGetMethod(true);
            if (getItem == null) return itens;
            for (int i = 0; i < count; i++)
            {
                try { itens.Add(getItem.Invoke(colecao, new object[] { i })); }
                catch { break; }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Evento completar tarefa: falha ao ler a lista IL2CPP: " + ex.GetBaseException().Message);
        }
        return itens;
    }

    private static object LerMembro(object objeto, params string[] nomes)
    {
        if (objeto == null) return null;
        Type tipo = objeto.GetType();
        foreach (string nome in nomes)
        {
            try
            {
                PropertyInfo propriedade = AccessTools.Property(tipo, nome);
                if (propriedade != null) return propriedade.GetValue(objeto);
                FieldInfo campo = AccessTools.Field(tipo, nome);
                if (campo != null) return campo.GetValue(objeto);
            }
            catch { }
        }
        return null;
    }

    private static bool LerBool(object objeto, params string[] nomes)
    {
        object valor = LerMembro(objeto, nomes);
        try { return valor != null && Convert.ToBoolean(valor); }
        catch { return false; }
    }
}

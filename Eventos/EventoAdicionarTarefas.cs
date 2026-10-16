using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoAdicionarTarefas : EventoVotacao
{
    private sealed class TarefaConcluidaAnterior
    {
        public string Tipo;
        public int Ocorrencia;
    }

    private static readonly Dictionary<byte, List<TarefaConcluidaAnterior>> ConclusoesPendentes = new();
    private static readonly Dictionary<byte, int> QuantidadeEsperada = new();
    private static readonly Dictionary<byte, float> RestauracoesAgendadas = new();

    // Espera o jogo distribuir e materializar a nova lista antes de reaplicar conclusões.
    public static void AgendarRestauracaoTarefasConcluidas(PlayerControl jogador)
    {
        if (jogador == null || AmongUsClient.Instance == null)
            return;

        // Agenda pelo Update do jogo, evitando depender de coroutines IL2CPP.
        RestauracoesAgendadas[jogador.PlayerId] = Time.time + 0.75f;
    }

    public static void AtualizarRestauracoesAgendadas()
    {
        if (RestauracoesAgendadas.Count == 0)
            return;

        foreach (var agendada in RestauracoesAgendadas.ToArray())
        {
            if (Time.time < agendada.Value)
                continue;

            RestauracoesAgendadas.Remove(agendada.Key);

            PlayerControl jogador = null;
            if (PlayerControl.AllPlayerControls != null)
            {
                foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
                {
                    if (candidato != null && candidato.PlayerId == agendada.Key)
                    {
                        jogador = candidato;
                        break;
                    }
                }
            }

            if (jogador == null)
                continue;

            RestaurarTarefasConcluidas(jogador);

            // Se a lista nova ainda não chegou, tenta novamente em 0,25 s.
            if (ConclusoesPendentes.ContainsKey(agendada.Key))
                RestauracoesAgendadas[agendada.Key] = Time.time + 0.25f;
        }
    }

    // Chamado depois que a lista nova de tarefas já foi aplicada.
    public static void RestaurarTarefasConcluidas(PlayerControl jogador)
    {
        if (jogador == null || !ConclusoesPendentes.TryGetValue(jogador.PlayerId, out var antigas))
            return;

        List<object> atuais = EnumerarColecaoIl2Cpp(LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks"));
        if (atuais.Count == 0 ||
            !QuantidadeEsperada.TryGetValue(jogador.PlayerId, out int quantidadeEsperada) ||
            atuais.Count < quantidadeEsperada)
            return; // Ainda é a lista antiga; o patch tentará novamente quando a nova chegar.

        int restauradas = 0;
        foreach (TarefaConcluidaAnterior antiga in antigas)
        {
            int ocorrencia = 0;
            object tarefaAlvo = null;
            foreach (object tarefa in atuais)
            {
                if (tarefa == null) continue;
                string tipo = LerMembro(tarefa, "TaskType", "taskType", "Type", "type")?.ToString()
                    ?? tarefa.GetType().FullName;
                if (tipo != antiga.Tipo) continue;
                if (ocorrencia++ == antiga.Ocorrencia)
                {
                    tarefaAlvo = tarefa;
                    break;
                }
            }

            if (tarefaAlvo == null || LerBool(tarefaAlvo, "IsComplete", "isComplete", "Complete", "complete"))
                continue;

            object id = LerMembro(tarefaAlvo, "Id", "id", "TaskId", "taskId");
            if (id == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Adicionar tarefas: não encontrei o ID da tarefa '{antiga.Tipo}' para PlayerId={jogador.PlayerId}.");
                continue;
            }

            try
            {
                // EHR completa tarefas de outros jogadores usando RpcCompleteTask.
                // Esse RPC propaga a conclusão pelo protocolo normal do jogo aos clientes,
                // inclusive vanilla. CompleteTask local/GameData.CompleteTask pode alterar
                // a barra no host sem atualizar a tarefa na tela dos demais clientes.
                uint taskId = Convert.ToUInt32(id);
                jogador.RpcCompleteTask(taskId);

                if (LerBool(tarefaAlvo, "IsComplete", "isComplete", "Complete", "complete"))
                    restauradas++;
                else
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"Adicionar tarefas: PlayerControl.CompleteTask ainda não marcou '{antiga.Tipo}' como concluída (PlayerId={jogador.PlayerId}, TaskId={taskId}).");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Adicionar tarefas: falha ao restaurar '{antiga.Tipo}' para PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Adicionar tarefas: PlayerId={jogador.PlayerId}; tarefas concluídas antigas restauradas={restauradas}/{antigas.Count}.");
        ConclusoesPendentes.Remove(jogador.PlayerId);
        QuantidadeEsperada.Remove(jogador.PlayerId);
    }

    private static List<TarefaConcluidaAnterior> CapturarTarefasConcluidas(PlayerControl jogador)
    {
        List<TarefaConcluidaAnterior> resultado = new();
        Dictionary<string, int> ocorrencias = new();
        List<object> tarefas = EnumerarColecaoIl2Cpp(LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks"));
        foreach (object tarefa in tarefas)
        {
            if (tarefa == null) continue;
            string tipo = LerMembro(tarefa, "TaskType", "taskType", "Type", "type")?.ToString()
                ?? tarefa.GetType().FullName;
            ocorrencias.TryGetValue(tipo, out int ocorrencia);
            ocorrencias[tipo] = ocorrencia + 1;
            if (LerBool(tarefa, "IsComplete", "isComplete", "Complete", "complete"))
                resultado.Add(new TarefaConcluidaAnterior { Tipo = tipo, Ocorrencia = ocorrencia });
        }
        return resultado;
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
            MethodInfo getItem = tipo.GetProperty("Item",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetGetMethod(true);
            if (getItem == null) return itens;
            for (int i = 0; i < count; i++)
            {
                try { itens.Add(getItem.Invoke(colecao, new object[] { i })); }
                catch { break; }
            }
        }
        catch { }
        return itens;
    }

    private static object LerMembro(object objeto, params string[] nomes)
    {
        if (objeto == null) return null;
        foreach (string nome in nomes)
        {
            try
            {
                PropertyInfo propriedade = AccessTools.Property(objeto.GetType(), nome);
                if (propriedade != null) return propriedade.GetValue(objeto);
                FieldInfo campo = AccessTools.Field(objeto.GetType(), nome);
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

    private readonly int quantidade;
    private readonly bool todos;

    public EventoAdicionarTarefas(int quantidade, bool todos)
    {
        this.quantidade = quantidade;
        this.todos = todos;
    }

    public override string Nome => todos
        ? $"Adicionar {quantidade} tarefa(s) a todos"
        : $"Adicionar {quantidade} tarefa(s) a um jogador aleatório";

    public void Executar() => Aplicar();

    private void Aplicar()
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
                "Evento de tarefas cancelado: nenhum jogador vivo.");
            return;
        }

        if (!todos)
        {
            PlayerControl alvo = jogadores[System.Random.Shared.Next(jogadores.Count)];
            int adicionadas = AdicionarAoJogador(alvo);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Evento de tarefas: adicionadas {adicionadas}/{quantidade} tarefa(s) para {alvo.Data.PlayerName}.");
            return;
        }

        foreach (PlayerControl jogador in jogadores)
        {
            int adicionadas = AdicionarAoJogador(jogador);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Evento de tarefas: adicionadas {adicionadas}/{quantidade} tarefa(s) para {jogador.Data.PlayerName}.");
        }
    }

    // O EHR adiciona tarefas durante a atribuição normal, interceptando RpcSetTasks.
    // Guardamos a quantidade extra e deixamos o jogo criar a lista que será replicada aos clientes.
    private static readonly Dictionary<byte, int> AdicoesDuranteAtribuicao = new();

    public static void AplicarAdicoesAntesDeEnviar(
        NetworkedPlayerInfo dados,
        ref Il2CppStructArray<byte> taskTypeIds)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || dados == null || taskTypeIds == null)
            return;

        PlayerControl jogador = dados.Object;
        if (jogador == null ||
            !AdicoesDuranteAtribuicao.TryGetValue(jogador.PlayerId, out int quantidadeExtra) ||
            quantidadeExtra <= 0 ||
            ShipStatus.Instance == null)
            return;

        List<byte> catalogo = new();
        AdicionarTipos(ShipStatus.Instance.ShortTasks, catalogo);
        AdicionarTipos(ShipStatus.Instance.LongTasks, catalogo);
        catalogo = catalogo.Distinct().ToList();

        if (catalogo.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"Adicionar tarefas: catálogo vazio ao atribuir tarefas para PlayerId={jogador.PlayerId}.");
            AdicoesDuranteAtribuicao.Remove(jogador.PlayerId);
            return;
        }

        List<byte> tipos = taskTypeIds.ToList();
        HashSet<byte> existentes = new(tipos);
        List<byte> novos = catalogo.Where(id => !existentes.Contains(id)).ToList();
        Embaralhar(novos);
        Embaralhar(catalogo);

        int adicionadas = 0;
        foreach (byte tipo in novos)
        {
            if (adicionadas >= quantidadeExtra) break;
            tipos.Add(tipo);
            existentes.Add(tipo);
            adicionadas++;
        }

        // Caso o mapa não tenha tipos suficientes, repete tipos válidos, como fallback.
        for (int i = 0; adicionadas < quantidadeExtra; i++)
        {
            tipos.Add(catalogo[i % catalogo.Count]);
            adicionadas++;
        }

        taskTypeIds = new Il2CppStructArray<byte>(tipos.Count);
        for (int i = 0; i < tipos.Count; i++)
            taskTypeIds[i] = tipos[i];

        QuantidadeEsperada[jogador.PlayerId] = tipos.Count;
        AdicoesDuranteAtribuicao.Remove(jogador.PlayerId);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"EHR-style RpcSetTasks: PlayerId={jogador.PlayerId}, extras={adicionadas}, total={tipos.Count}.");
    }

    private int AdicionarAoJogador(PlayerControl jogador)
    {
        try
        {
            if (jogador?.Data == null || ShipStatus.Instance == null)
                return 0;

            List<byte> catalogo = new();
            AdicionarTipos(ShipStatus.Instance.ShortTasks, catalogo);
            AdicionarTipos(ShipStatus.Instance.LongTasks, catalogo);
            if (catalogo.Count == 0)
                return 0;

            List<TarefaConcluidaAnterior> concluidasAntes = CapturarTarefasConcluidas(jogador);
            ConclusoesPendentes[jogador.PlayerId] = concluidasAntes;
            AdicoesDuranteAtribuicao[jogador.PlayerId] = quantidade;
            QuantidadeEsperada.Remove(jogador.PlayerId);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"EHR-style: capturadas {concluidasAntes.Count} tarefas concluídas de {jogador.Data.PlayerName}; solicitando redistribuição com +{quantidade} tarefa(s).");

            // Este é o fluxo usado pela role Capitalist do EHR:
            // RpcResetTasks dispara a atribuição normal, e o Prefix de RpcSetTasks
            // acrescenta as tarefas antes de o RPC ser enviado aos clientes.
            jogador.RpcResetTasks(false);

            AgendarRestauracaoTarefasConcluidas(jogador);
            return quantidade;
        }
        catch (Exception ex)
        {
            AdicoesDuranteAtribuicao.Remove(jogador?.PlayerId ?? byte.MaxValue);
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha ao adicionar tarefas via fluxo EHR para {jogador?.Data?.PlayerName}: {ex}");
            return 0;
        }
    }

    private static void AdicionarTipos(
        Il2CppReferenceArray<NormalPlayerTask> tarefas,
        List<byte> destino)
    {
        if (tarefas == null) return;

        foreach (NormalPlayerTask tarefa in tarefas)
        {
            if (tarefa == null) continue;
            destino.Add((byte)tarefa.TaskType);
        }
    }

    private static void Embaralhar<T>(List<T> lista)
    {
        for (int i = lista.Count - 1; i > 0; i--)
        {
            int j = System.Random.Shared.Next(i + 1);
            (lista[i], lista[j]) = (lista[j], lista[i]);
        }
    }
}

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
                // GameData.CompleteTask é o ponto autoritativo que o jogo usa
                // para registrar a conclusão. RpcCompleteTask pode ser rejeitado
                // quando o host tenta completar uma tarefa pertencente a outro cliente.
                uint taskId = Convert.ToUInt32(id);
                if (GameData.Instance == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Adicionar tarefas: GameData.Instance está nulo; tentarei restaurar novamente.");
                    continue;
                }

                GameData.Instance.CompleteTask(jogador, taskId);

                if (LerBool(tarefaAlvo, "IsComplete", "isComplete", "Complete", "complete"))
                    restauradas++;
                else
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"Adicionar tarefas: CompleteTask não marcou '{antiga.Tipo}' como concluída (PlayerId={jogador.PlayerId}, TaskId={taskId}).");
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

    private int AdicionarAoJogador(PlayerControl jogador)
    {
        try
        {
            if (jogador?.Data == null || ShipStatus.Instance == null)
                return 0;

            // Guarda quais tarefas já estavam concluídas antes de recriar a lista.
            // RpcSetTasks recebe apenas os tipos e gera IDs novos, por isso reaplicamos
            // as conclusões depois que a lista nova estiver disponível.
            List<TarefaConcluidaAnterior> concluidasAntes = CapturarTarefasConcluidas(jogador);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Adicionar tarefas: capturadas {concluidasAntes.Count} conclusão(ões) antigas para {jogador.Data.PlayerName}.");

            // RpcSetTasks recebe a lista completa: mantemos as tarefas existentes.
            List<byte> ids = new();
            foreach (NetworkedPlayerInfo.TaskInfo tarefaExistente in jogador.Data.Tasks)
                ids.Add(tarefaExistente.TypeId);

            // Como no EHR, usamos o catálogo real de tarefas do mapa atual.
            List<byte> catalogo = new();
            AdicionarTipos(ShipStatus.Instance.ShortTasks, catalogo);
            AdicionarTipos(ShipStatus.Instance.LongTasks, catalogo);

            if (catalogo.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Não há tarefas disponíveis no mapa para {jogador.Data.PlayerName}.");
                return 0;
            }

            HashSet<byte> existentes = new(ids);
            List<byte> novas = catalogo
                .Where(id => !existentes.Contains(id))
                .Distinct()
                .ToList();

            Embaralhar(novas);

            int adicionadas = 0;
            int unicas = Math.Min(quantidade, novas.Count);

            // Primeiro, atribui tipos que o jogador ainda não tem.
            for (int i = 0; i < unicas; i++)
            {
                ids.Add(novas[i]);
                existentes.Add(novas[i]);
                adicionadas++;
            }

            // Se faltarem tarefas depois de esgotar os tipos inéditos,
            // permite repetir tipos válidos do mapa.
            int faltam = quantidade - adicionadas;
            if (faltam > 0)
            {
                List<byte> repetiveis = catalogo.Distinct().ToList();
                Embaralhar(repetiveis);

                for (int i = 0; i < faltam; i++)
                {
                    ids.Add(repetiveis[i % repetiveis.Count]);
                    adicionadas++;
                }
            }

            if (adicionadas == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Não foi possível adicionar tarefas para {jogador.Data.PlayerName}.");
                return 0;
            }

            Il2CppStructArray<byte> novaLista = new(ids.Count);
            for (int i = 0; i < ids.Count; i++)
                novaLista[i] = ids[i];

            if (concluidasAntes.Count > 0)
            {
                ConclusoesPendentes[jogador.PlayerId] = concluidasAntes;
                QuantidadeEsperada[jogador.PlayerId] = ids.Count;
            }

            jogador.Data.RpcSetTasks(novaLista);
            AgendarRestauracaoTarefasConcluidas(jogador);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Atribuição de tarefas para {jogador.Data.PlayerName}: novas únicas={unicas}, repetidas={adicionadas - unicas}, total adicionadas={adicionadas}.");
            return adicionadas;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha ao adicionar tarefas para {jogador?.Data?.PlayerName}: {ex}");
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

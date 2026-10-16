using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoAdicionarTarefas : EventoVotacao
{
    private sealed class TarefaConcluidaAnterior
    {
        public byte Tipo;
        public int Ocorrencia;
        public uint TaskId;
    }

    private static readonly Dictionary<byte, List<uint>> HistoricoRpcConclusoes = new();
    private static readonly Dictionary<byte, List<TarefaConcluidaAnterior>> ConclusoesPendentes = new();
    private static readonly Dictionary<byte, int> QuantidadeEsperada = new();
    private static readonly Dictionary<byte, float> RestauracoesAgendadas = new();
    private static readonly HashSet<byte> ListaNovaDetectada = new();

    // Reenvios limitados: tenta sincronizar clientes que possam ter ignorado o primeiro RPC.
    private static readonly Dictionary<byte, List<uint>> RpcConclusoesParaRepetir = new();
    private static readonly Dictionary<byte, float> ProximoReenvioRpc = new();
    private static readonly Dictionary<byte, int> ReenviosRestantes = new();

    public static void RegistrarRpcConclusao(PlayerControl jogador, uint taskId)
    {
        if (jogador == null) return;
        if (!HistoricoRpcConclusoes.TryGetValue(jogador.PlayerId, out List<uint> ids))
        {
            ids = new List<uint>();
            HistoricoRpcConclusoes[jogador.PlayerId] = ids;
        }
        if (!ids.Contains(taskId))
        {
            ids.Add(taskId);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Adicionar tarefas: RPC CompleteTask registrado: PlayerId={jogador.PlayerId}, TaskId={taskId}.");
        }
    }

    public static void LimparHistoricoRpcConclusoes() => HistoricoRpcConclusoes.Clear();

    private static List<TarefaConcluidaAnterior> CapturarRpcConclusoes(PlayerControl jogador)
    {
        List<TarefaConcluidaAnterior> resultado = new();
        if (jogador == null || !HistoricoRpcConclusoes.TryGetValue(jogador.PlayerId, out List<uint> ids))
            return resultado;
        foreach (uint id in ids)
            resultado.Add(new TarefaConcluidaAnterior { TaskId = id });
        return resultado;
    }

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
        if (RestauracoesAgendadas.Count > 0)
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

            // Espera a lista nova aparecer antes de iniciar o atraso de 1 segundo.
            if (!ListaNovaDetectada.Contains(agendada.Key))
            {
                List<object> tarefasAtuais = EnumerarColecaoIl2Cpp(
                    LerMembro(jogador.Data, "Tasks", "tasks"));

                if (!QuantidadeEsperada.TryGetValue(agendada.Key, out int esperada) ||
                    tarefasAtuais.Count < esperada)
                {
                    RestauracoesAgendadas[agendada.Key] = Time.time + 0.25f;
                    continue;
                }

                ListaNovaDetectada.Add(agendada.Key);

                // RpcSetTasks substitui a lista, mas o contador global da barra pode
                // continuar com o valor anterior. Recalcula-o a partir das listas novas
                // antes de reaplicar as conclusões antigas.
                try
                {
                    GameData.Instance?.RecomputeTaskCounts();
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        $"Adicionar tarefas: contador da barra recalculado após nova lista de PlayerId={agendada.Key}.");
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"Adicionar tarefas: falha ao recalcular a barra após reset: {ex.GetBaseException().Message}");
                }

                RestauracoesAgendadas[agendada.Key] = Time.time + 1f;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Adicionar tarefas: lista nova aplicada para PlayerId={agendada.Key} " +
                    $"({tarefasAtuais.Count} tarefas); RPC de conclusão em 1 segundo.");
                continue;
            }

            RestaurarTarefasConcluidas(jogador);

            if (ConclusoesPendentes.ContainsKey(agendada.Key))
                RestauracoesAgendadas[agendada.Key] = Time.time + 0.25f;
            else
                ListaNovaDetectada.Remove(agendada.Key);
        }

        // Repete os RPCs em quatro rodadas adicionais, espaçadas em 0,5 s.
        // Não chama CompleteTask localmente nessas repetições para evitar somar
        // o progresso do host várias vezes.
        foreach (var item in ProximoReenvioRpc.ToArray())
        {
            if (Time.time < item.Value) continue;

            if (!RpcConclusoesParaRepetir.TryGetValue(item.Key, out List<uint> ids) ||
                !ReenviosRestantes.TryGetValue(item.Key, out int restantes) ||
                restantes <= 0)
            {
                ProximoReenvioRpc.Remove(item.Key);
                RpcConclusoesParaRepetir.Remove(item.Key);
                ReenviosRestantes.Remove(item.Key);
                continue;
            }

            PlayerControl alvo = null;
            if (PlayerControl.AllPlayerControls != null)
            {
                foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
                    if (candidato != null && candidato.PlayerId == item.Key)
                    { alvo = candidato; break; }
            }

            if (alvo != null)
            {
                int enviados = 0;
                foreach (uint taskId in ids)
                {
                    MessageWriter writer = null;
                    try
                    {
                        writer = AmongUsClient.Instance.StartRpcImmediately(
                            alvo.NetId, (byte)RpcCalls.CompleteTask, SendOption.Reliable, -1);
                        writer.WritePacked(taskId);
                        AmongUsClient.Instance.FinishRpcImmediately(writer);
                        writer = null;
                        enviados++;
                    }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            $"Adicionar tarefas: falha no reenvio extra CompleteTask, PlayerId={item.Key}, TaskId={taskId}: {ex.GetBaseException().Message}");
                    }
                    finally
                    {
                        if (writer != null) writer.Recycle();
                    }
                }

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Adicionar tarefas: rodada extra de RPC CompleteTask {5 - restantes}/4 para PlayerId={item.Key}; enviados={enviados}.");
            }

            restantes--;
            if (restantes <= 0)
            {
                ProximoReenvioRpc.Remove(item.Key);
                RpcConclusoesParaRepetir.Remove(item.Key);
                ReenviosRestantes.Remove(item.Key);
            }
            else
            {
                ReenviosRestantes[item.Key] = restantes;
                ProximoReenvioRpc[item.Key] = Time.time + 0.5f;
            }
        }
    }

    // Reenvia os mesmos RPCs CompleteTask recebidos durante a partida após a redistribuição.
    public static void RestaurarTarefasConcluidas(PlayerControl jogador)
    {
        if (jogador == null || !ConclusoesPendentes.TryGetValue(jogador.PlayerId, out var antigas))
            return;

        List<object> atuais = EnumerarColecaoIl2Cpp(LerMembro(jogador.Data, "Tasks", "tasks"));
        if (atuais.Count == 0 ||
            !QuantidadeEsperada.TryGetValue(jogador.PlayerId, out int quantidadeEsperada) ||
            atuais.Count < quantidadeEsperada)
            return;

        int reenviadas = 0;
        List<uint> idsParaRepetir = new();
        foreach (TarefaConcluidaAnterior antiga in antigas)
        {
            bool idExiste = false;
            foreach (object tarefa in atuais)
            {
                object idObj = LerMembro(tarefa, "Id", "id", "TaskId", "taskId");
                try
                {
                    if (idObj != null && Convert.ToUInt32(idObj) == antiga.TaskId)
                    { idExiste = true; break; }
                }
                catch { }
            }

            if (!idExiste)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Adicionar tarefas: TaskId original {antiga.TaskId} não existe na nova lista de PlayerId={jogador.PlayerId}; RPC não reenviado.");
                continue;
            }

            MessageWriter writer = null;
            try
            {
                // Atualiza o estado autoritativo do host.
                if (GameData.Instance != null)
                    GameData.Instance.CompleteTask(jogador, antiga.TaskId);

                // Usa o caminho oficial de RPC do Among Us em vez de montar manualmente
                // um pacote GameData. A montagem manual pode deixar o estado/progresso
                // de tarefas inconsistente entre host e clientes.
                writer = AmongUsClient.Instance.StartRpcImmediately(
                    jogador.NetId,
                    (byte)RpcCalls.CompleteTask,
                    SendOption.Reliable,
                    -1);
                writer.WritePacked(antiga.TaskId);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                writer = null;
                reenviadas++;
                if (!idsParaRepetir.Contains(antiga.TaskId))
                    idsParaRepetir.Add(antiga.TaskId);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Adicionar tarefas: RPC CompleteTask reenviado para PlayerId={jogador.PlayerId}, NetId={jogador.NetId}, TaskId={antiga.TaskId}.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Adicionar tarefas: falha ao reenviar CompleteTask para PlayerId={jogador.PlayerId}, TaskId={antiga.TaskId}: {ex.GetBaseException().Message}");
            }
            finally
            {
                if (writer != null) writer.Recycle();
            }
        }

        // CompleteTask altera o estado individual; força o contador agregado da barra
        // a ser reconstruído a partir do estado final de todos os jogadores.
        try
        {
            GameData.Instance?.RecomputeTaskCounts();
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Adicionar tarefas: contador da barra recalculado após restaurar PlayerId={jogador.PlayerId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"Adicionar tarefas: falha ao recalcular a barra após restauração: {ex.GetBaseException().Message}");
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Adicionar tarefas: PlayerId={jogador.PlayerId}; RPCs CompleteTask reenviados={reenviadas}/{antigas.Count}.");

        if (idsParaRepetir.Count > 0)
        {
            RpcConclusoesParaRepetir[jogador.PlayerId] = idsParaRepetir;
            ReenviosRestantes[jogador.PlayerId] = 4;
            ProximoReenvioRpc[jogador.PlayerId] = Time.time + 0.5f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Adicionar tarefas: agendadas 4 rodadas extras de CompleteTask para PlayerId={jogador.PlayerId}.");
        }

        ConclusoesPendentes.Remove(jogador.PlayerId);
        QuantidadeEsperada.Remove(jogador.PlayerId);
        ListaNovaDetectada.Remove(jogador.PlayerId);
        RestauracoesAgendadas.Remove(jogador.PlayerId);
    }

    private static List<TarefaConcluidaAnterior> CapturarTarefasConcluidas(PlayerControl jogador)
    {
        List<TarefaConcluidaAnterior> resultado = new();
        Dictionary<byte, int> ocorrencias = new();
        List<object> tarefas = EnumerarColecaoIl2Cpp(LerMembro(jogador?.Data, "Tasks", "tasks"));
        foreach (object tarefa in tarefas)
        {
            if (tarefa == null) continue;
            object tipoObj = LerMembro(tarefa, "TypeId", "typeId", "TaskType", "taskType");
            if (tipoObj == null) continue;
            byte tipo = ConverterByte(tipoObj);
            ocorrencias.TryGetValue(tipo, out int ocorrencia);
            ocorrencias[tipo] = ocorrencia + 1;
            if (LerBool(tarefa, "Complete", "complete"))
                resultado.Add(new TarefaConcluidaAnterior { Tipo = tipo, Ocorrencia = ocorrencia });
        }
        return resultado;
    }

    private static List<byte> CapturarTiposTarefas(PlayerControl jogador)
    {
        List<byte> tipos = new();
        List<object> tarefas = EnumerarColecaoIl2Cpp(LerMembro(jogador?.Data, "Tasks", "tasks"));

        foreach (object tarefa in tarefas)
        {
            if (tarefa == null) continue;
            object tipo = LerMembro(tarefa, "TypeId", "typeId", "TaskType", "taskType");
            if (tipo != null) tipos.Add(ConverterByte(tipo));
        }

        return tipos;
    }

    private static byte ConverterByte(object valor)
    {
        try { return Convert.ToByte(valor); }
        catch
        {
            try { return (byte)Convert.ToInt32(valor); }
            catch { return byte.MaxValue; }
        }
    }

    private static object EncontrarTaskInfo(List<object> tarefas, byte tipoProcurado, int ocorrenciaProcurada)
    {
        int ocorrencia = 0;
        foreach (object tarefa in tarefas)
        {
            if (tarefa == null) continue;
            object tipoObj = LerMembro(tarefa, "TypeId", "typeId", "TaskType", "taskType");
            if (tipoObj == null || ConverterByte(tipoObj) != tipoProcurado) continue;
            if (ocorrencia++ == ocorrenciaProcurada) return tarefa;
        }

        return null;
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
    // RpcSetTasks recebe uma lista vazia no reset; guardamos os tipos anteriores
    // para que o Prefix possa preservá-los e acrescentar somente as tarefas novas.
    private static readonly Dictionary<byte, List<byte>> TiposOriginaisDuranteAtribuicao = new();

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

        List<byte> tipos;
        if (TiposOriginaisDuranteAtribuicao.TryGetValue(jogador.PlayerId, out List<byte> tiposOriginais))
            tipos = new List<byte>(tiposOriginais);
        else
            tipos = taskTypeIds.ToList();

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
        TiposOriginaisDuranteAtribuicao.Remove(jogador.PlayerId);

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

            List<TarefaConcluidaAnterior> concluidasAntes = CapturarRpcConclusoes(jogador);
            List<byte> tiposOriginais = CapturarTiposTarefas(jogador);
            ConclusoesPendentes[jogador.PlayerId] = concluidasAntes;
            TiposOriginaisDuranteAtribuicao[jogador.PlayerId] = tiposOriginais;
            AdicoesDuranteAtribuicao[jogador.PlayerId] = quantidade;
            QuantidadeEsperada.Remove(jogador.PlayerId);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"EHR-style: capturados {concluidasAntes.Count} RPCs CompleteTask de {jogador.Data.PlayerName}; solicitando redistribuição com +{quantidade} tarefa(s).");

            // Este é o fluxo usado pela role Capitalist do EHR:
            // RpcResetTasks dispara a atribuição normal, e o Prefix de RpcSetTasks
            // acrescenta as tarefas antes de o RPC ser enviado aos clientes.
            // Equivalente ao RpcResetTasks do EHR: envia uma lista vazia pelo RPC nativo.
            // O Prefix de NetworkedPlayerInfo.RpcSetTasks acrescenta os tipos extras antes do envio.
            jogador.Data.RpcSetTasks(new Il2CppStructArray<byte>(0));

            AgendarRestauracaoTarefasConcluidas(jogador);
            return quantidade;
        }
        catch (Exception ex)
        {
            AdicoesDuranteAtribuicao.Remove(jogador?.PlayerId ?? byte.MaxValue);
            TiposOriginaisDuranteAtribuicao.Remove(jogador?.PlayerId ?? byte.MaxValue);
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

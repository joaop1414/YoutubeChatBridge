using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace YouTubeVoteBridge;

internal sealed class EventoAdicionarTarefas : EventoVotacao
{
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
            PlayerControl alvo = jogadores[Random.Shared.Next(jogadores.Count)];
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
            // Copia a lista já atribuída para não substituir as tarefas existentes.
            List<byte> ids = new();
            foreach (byte id in jogador.Data.Tasks)
                ids.Add(id);

            HashSet<byte> existentes = new(ids);

            // Usa apenas tarefas disponibilizadas pelo mapa atual.
            List<byte> disponiveis = new();
            foreach (NormalPlayerTask tarefa in ShipStatus.Instance.ShortTasks)
            {
                if (tarefa == null) continue;
                byte id = (byte)tarefa.TaskType;
                if (!existentes.Contains(id) && !disponiveis.Contains(id))
                    disponiveis.Add(id);
            }
            foreach (NormalPlayerTask tarefa in ShipStatus.Instance.LongTasks)
            {
                if (tarefa == null) continue;
                byte id = (byte)tarefa.TaskType;
                if (!existentes.Contains(id) && !disponiveis.Contains(id))
                    disponiveis.Add(id);
            }

            // Embaralha para que a seleção não dependa da ordem interna do mapa.
            for (int i = disponiveis.Count - 1; i > 0; i--)
            {
                int j = Random.Shared.Next(i + 1);
                (disponiveis[i], disponiveis[j]) = (disponiveis[j], disponiveis[i]);
            }

            int adicionar = Math.Min(quantidade, disponiveis.Count);
            for (int i = 0; i < adicionar; i++)
                ids.Add(disponiveis[i]);

            if (adicionar == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"Não há tipos de tarefa novos disponíveis para {jogador.Data.PlayerName}.");
                return 0;
            }

            // RpcSetTasks sincroniza a lista completa com os clientes.
            // Os IDs existentes permanecem na lista; apenas acrescentamos novos IDs.
            Il2CppStructArray<byte> novaLista = new(ids.Count);
            for (int i = 0; i < ids.Count; i++)
                novaLista[i] = ids[i];

            jogador.Data.RpcSetTasks(novaLista);
            return adicionar;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"Falha ao adicionar tarefas para {jogador?.Data?.PlayerName}: {ex}");
            return 0;
        }
    }
}

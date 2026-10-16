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
            if (jogador?.Data == null || ShipStatus.Instance == null)
                return 0;

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

            jogador.Data.RpcSetTasks(novaLista);

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
            int j = Random.Shared.Next(i + 1);
            (lista[i], lista[j]) = (lista[j], lista[i]);
        }
    }
}

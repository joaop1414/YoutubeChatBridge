using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoTarefaMortal : EventoVotacao
{
    private static bool armado;
    private static bool ativo;

    public override string Nome => "Ao concluir uma tarefa, há chance de matar alguém ou a si mesmo";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null) ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Tarefa mortal] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida()
    {
        if (armado) ativo = true;
    }

    internal static void AoCompletarTarefa(PlayerControl executor)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || executor == null ||
            executor.Data == null || executor.Data.IsDead || executor.Data.Disconnected ||
            MeetingHud.Instance != null || ShipStatus.Instance == null)
            return;

        // 25%: matar outro jogador; 10%: suicídio; 65%: nada acontece.
        int sorteio = System.Random.Shared.Next(100);
        if (sorteio >= 35) return;

        try
        {
            if (sorteio < 25)
            {
                List<PlayerControl> alvos = new();
                foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                    if (p != null && p.Data != null && p.PlayerId != executor.PlayerId &&
                        !p.Data.IsDead && !p.Data.Disconnected)
                        alvos.Add(p);
                if (alvos.Count == 0) return;
                PlayerControl alvo = alvos[System.Random.Shared.Next(alvos.Count)];
                executor.RpcMurderPlayer(alvo, true);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[F7/Tarefa mortal] {executor.Data.PlayerName} concluiu uma tarefa e matou {alvo.Data.PlayerName}.");
            }
            else
            {
                executor.RpcMurderPlayer(executor, true);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[F7/Tarefa mortal] {executor.Data.PlayerName} concluiu uma tarefa e cometeu suicídio.");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Tarefa mortal] Falha: " + ex.Message);
        }
    }

    internal static void Resetar() { ativo = false; armado = false; }
}

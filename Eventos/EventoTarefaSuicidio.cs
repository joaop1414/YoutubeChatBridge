using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoTarefaSuicidio : EventoVotacao
{
    private static bool ativo;
    private static readonly Random Sorteador = new();
    public override string Nome => "Quem completar uma tarefa se mata";
    public void Executar() => Ativar();
    public static void Ativar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Evento ativado: cada tarefa concluída fará o próprio executor morrer até a próxima reunião.");
    }
    public static void Desativar() { ativo = false; }
    public static void AoCompletarTarefa(PlayerControl executor)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() || executor == null || executor.Data == null || executor.Data.IsDead || executor.Data.Disconnected || MeetingHud.Instance != null || ShipStatus.Instance == null) return;
        List<PlayerControl> alvos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && jogador.PlayerId != executor.PlayerId && !jogador.Data.IsDead && !jogador.Data.Disconnected) alvos.Add(jogador);
        if (alvos.Count == 0) return;
        PlayerControl alvo = alvos[Sorteador.Next(alvos.Count)];
        try
        {
            executor.RpcMurderPlayer(executor, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Evento de suicídio por tarefa: " + executor.Data.PlayerName + " concluiu uma tarefa e cometeu suicídio.");
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao executar morte por tarefa: " + ex); }
    }
}

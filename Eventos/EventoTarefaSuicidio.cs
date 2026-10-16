using System;

namespace YouTubeVoteBridge;

internal sealed class EventoTarefaSuicidio : EventoVotacao
{
    private static bool ativo;
    public override string Nome => "Quem completar uma tarefa se mata (até a próxima reunião)";
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
        try
        {
            executor.RpcMurderPlayer(executor, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Evento de suicídio por tarefa: " + executor.Data.PlayerName + " concluiu uma tarefa e cometeu suicídio.");
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao executar morte por tarefa: " + ex); }
    }
}

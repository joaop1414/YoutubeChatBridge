using System;
using AmongUs.GameOptions;

namespace YouTubeVoteBridge;

internal sealed class EventoImpostorSuicidaAoTentarMatar : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static bool processando;

    public override string Nome => "O impostor não dá o teleporte ao matar";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;

        armado = true;
        if (LobbyTagManager.PartidaIniciada()) ativo = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Impostor suicida] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida()
    {
        if (armado) ativo = true;
    }

    internal static bool AoTentarMatar(PlayerControl impostor, PlayerControl alvo)
    {
        if (!ativo || processando || !YouTubeVoteBridgePlugin.EhHost() ||
            impostor == null || impostor.Data == null ||
            impostor.Data.IsDead || impostor.Data.Disconnected ||
            impostor.Data.Role == null || !impostor.Data.Role.IsImpostor ||
            alvo == null || alvo.Data == null ||
            alvo.Data.IsDead || alvo.Data.Disconnected ||
            MeetingHud.Instance != null || ShipStatus.Instance == null)
            return false;

        try
        {
            processando = true;

            // Reutiliza a mesma sequência do escudo: o efeito FailedProtected
            // reduz o cooldown local, usando temporariamente opções dobradas.
            EventoEscudoTemporario.AplicarEscudoEResetarCooldown(impostor);

            // Depois da reação do escudo, mata o próprio impostor de forma
            // autoritativa para que a morte seja sincronizada com os jogadores.
            impostor.RpcMurderPlayer(impostor, true);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Impostor suicida] PlayerId={impostor.PlayerId} tentou matar " +
                $"PlayerId={alvo.PlayerId} e cometeu suicídio.");
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Impostor suicida] Falha ao processar tentativa de assassinato: " + ex);
            return false;
        }
        finally
        {
            processando = false;
        }
    }

    internal static void Resetar()
    {
        armado = false;
        ativo = false;
        processando = false;
    }
}

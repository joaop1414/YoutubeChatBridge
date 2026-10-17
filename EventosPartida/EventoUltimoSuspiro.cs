using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoUltimoSuspiro : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static bool aplicandoEfeito;
    public override string Nome => "Quando alguém morrer, há 35% de chance de outro jogador morrer também";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null) ativo = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Último suspiro] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida() { if (armado) ativo = true; }

    internal static void AoMorrer(PlayerControl morto)
    {
        if (!ativo || aplicandoEfeito || !YouTubeVoteBridgePlugin.EhHost() ||
            morto == null || morto.Data == null || !morto.Data.IsDead ||
            MeetingHud.Instance != null || ShipStatus.Instance == null ||
            System.Random.Shared.Next(100) >= 35)
            return;

        List<PlayerControl> alvos = new();
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            if (p != null && p.Data != null && p.PlayerId != morto.PlayerId &&
                !p.Data.IsDead && !p.Data.Disconnected)
                alvos.Add(p);
        if (alvos.Count == 0) return;

        PlayerControl alvo = alvos[System.Random.Shared.Next(alvos.Count)];
        try
        {
            aplicandoEfeito = true;
            alvo.RpcMurderPlayer(alvo, true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Último suspiro] Após a morte de PlayerId={morto.PlayerId}, PlayerId={alvo.PlayerId} também morreu.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Último suspiro] Falha: " + ex.Message);
        }
        finally { aplicandoEfeito = false; }
    }

    internal static void Resetar() { ativo = false; armado = false; aplicandoEfeito = false; }
}

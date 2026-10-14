using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Evento de teste que cria um NPC chamado NPC e o mantém seguindo o host.
/// Usa FakePlayerAPI para spawn, associação de rede, aparência e movimento.
/// </summary>
internal sealed class EventoNpcSeguidor : EventoVotacao
{
    public override string Nome => "Criar NPC seguidor";

    private static PlayerControl npc;
    private static float proximaAtualizacao;
    private const float IntervaloSeguimento = 0.20f;
    private const float DistanciaSeguimento = 0.85f;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null ||
            ShipStatus.Instance == null)
            return;

        if (npc != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC seguidor já existe; não vou criar outro para evitar duplicatas.");
            return;
        }

        Vector2 posicao = PlayerControl.LocalPlayer.GetTruePosition() +
            new Vector2(-DistanciaSeguimento, 0f);

        npc = FakePlayerAPI.Criar(posicao, "NPC", 0);
        if (npc == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC seguidor não foi criado. Consulte os logs da FakePlayerAPI.");
            return;
        }

        proximaAtualizacao = 0f;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Evento NPC seguidor: spawn solicitado; NetId=" + npc.NetId + ".");
    }

    public static void Atualizar()
    {
        if (npc == null)
            return;

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            ShipStatus.Instance == null || PlayerControl.LocalPlayer == null)
            return;

        if (Time.realtimeSinceStartup < proximaAtualizacao)
            return;

        proximaAtualizacao = Time.realtimeSinceStartup + IntervaloSeguimento;

        try
        {
            Vector2 alvo = PlayerControl.LocalPlayer.GetTruePosition() +
                new Vector2(-DistanciaSeguimento, 0f);

            // Não envia movimento se o NPC já estiver suficientemente perto.
            if (Vector2.Distance(npc.GetTruePosition(), alvo) > 0.25f)
                FakePlayerAPI.Mover(npc, alvo);
        }
        catch (System.Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC seguidor: falha ao atualizar posição: " + ex.Message);
        }
    }

    public static void Limpar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (npc != null)
            FakePlayerAPI.Destruir(npc);

        npc = null;
        proximaAtualizacao = 0f;
    }
}

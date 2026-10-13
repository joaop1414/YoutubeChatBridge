using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

internal sealed class EventoSabotagemAleatoria : EventoVotacao
{
    private static readonly Random Sorteador = new();

    // Estes são os tipos de sabotagem nativos usados pelo Among Us.
    // A presença do sistema no mapa serve para excluir sabotagens incompatíveis.
    private static readonly SystemTypes[] SabotagensPossiveis =
    {
        SystemTypes.Electrical,
        SystemTypes.Reactor,
        SystemTypes.Laboratory,
        SystemTypes.LifeSupp,
        SystemTypes.Comms,
        SystemTypes.HeliSabotage,
        SystemTypes.MushroomMixupSabotage
    };

    public override string Nome => "Sabotagem aleatória";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Sabotagem aleatória ignorada: somente o host pode iniciar sabotagens.");
            return;
        }

        ShipStatus navio = ShipStatus.Instance;
        PlayerControl jogador = PlayerControl.LocalPlayer;

        if (navio == null || jogador == null || jogador.Data == null ||
            jogador.Data.Disconnected || jogador.Data.IsDead)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Sabotagem aleatória ignorada: partida ou jogador host indisponível.");
            return;
        }

        if (!navio.Systems.TryGetValue(SystemTypes.Sabotage, out ISystemType sistema) ||
            sistema is not SabotageSystemType sistemaSabotagem)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Sabotagem aleatória: o sistema de sabotagem não está disponível neste mapa.");
            return;
        }

        if (sistemaSabotagem.AnyActive)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Sabotagem aleatória ignorada: já existe uma sabotagem ativa.");
            return;
        }

        List<SystemTypes> disponiveis = new();

        foreach (SystemTypes tipo in SabotagensPossiveis)
        {
            // Os mapas só registram os sistemas que realmente suportam.
            if (navio.Systems.ContainsKey(tipo))
                disponiveis.Add(tipo);
        }

        if (disponiveis.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Sabotagem aleatória: não encontrei sabotagens compatíveis com o mapa atual.");
            return;
        }

        SystemTypes escolhida = disponiveis[Sorteador.Next(disponiveis.Count)];

        try
        {
            // O evento de votação deve funcionar mesmo durante o cooldown normal.
            // Zera o temporizador antes do RPC nativo; o próprio jogo aplica o cooldown
            // novamente quando a sabotagem é processada.
            sistemaSabotagem.Timer = 0f;
            sistemaSabotagem.IsDirty = true;

            // Assim, clientes vanilla também recebem a sabotagem pelo host.
            navio.RpcUpdateSystem(SystemTypes.Sabotage, (byte)escolhida);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Evento executado: sabotagem aleatória iniciada. Tipo=" + escolhida +
                "; opções compatíveis=" + disponiveis.Count + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao iniciar a sabotagem aleatória: " + ex);
        }
    }
}

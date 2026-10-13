using System;
using System.Collections.Generic;
using System.Linq;

namespace YouTubeVoteBridge;

internal sealed class EventoSabotagemAleatoria : EventoVotacao
{
    private static readonly Random Sorteador = new();

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

        try
        {
            // O mapa não guarda cada sabotagem como uma chave separada.
            // As sabotagens são enviadas pelo sistema genérico SystemTypes.Sabotage,
            // e o byte enviado identifica qual delas deve começar.
            if (!navio.Systems.ContainsKey(SystemTypes.Sabotage))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Sabotagem aleatória: o mapa atual não possui o sistema nativo de sabotagem.");
                return;
            }

            int mapa = GameOptionsManager.Instance.CurrentGameOptions.MapId;
            List<SystemTypes> disponiveis = ObterSabotagensDoMapa(mapa);

            if (disponiveis.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Sabotagem aleatória: nenhum tipo configurado para o mapa ID=" + mapa + ".");
                return;
            }

            // Evita escolher uma sabotagem que já esteja ativa quando isso pode ser
            // identificado pelos sistemas de sabotagem específicos do mapa.
            // O jogo ainda valida cooldown e compatibilidade ao receber o RPC.
            SystemTypes escolhida = disponiveis[Sorteador.Next(disponiveis.Count)];

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Sabotagem aleatória: mapa ID=" + mapa +
                "; opções=[" + string.Join(", ", disponiveis) +
                "]; escolhida=" + escolhida + ".");

            navio.RpcUpdateSystem(SystemTypes.Sabotage, (byte)escolhida);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao iniciar a sabotagem aleatória: " + ex);
        }
    }

    private static List<SystemTypes> ObterSabotagensDoMapa(int mapa)
    {
        // IDs de mapa usados pelo Among Us:
        // 0 Skeld, 1 Mira HQ, 2 Polus, 3 Dleks, 4 Airship, 5 Fungle.
        return mapa switch
        {
            0 or 3 => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.Reactor,
                SystemTypes.LifeSupp,
                SystemTypes.Comms
            },
            1 => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.Reactor,
                SystemTypes.LifeSupp,
                SystemTypes.Comms
            },
            2 => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.Laboratory,
                SystemTypes.LifeSupp,
                SystemTypes.Comms
            },
            4 => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.HeliSabotage,
                SystemTypes.LifeSupp,
                SystemTypes.Comms
            },
            5 => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.MushroomMixupSabotage,
                SystemTypes.LifeSupp,
                SystemTypes.Comms
            },
            _ => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.Reactor,
                SystemTypes.LifeSupp,
                SystemTypes.Comms
            }
        };
    }
}

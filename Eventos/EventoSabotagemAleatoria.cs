using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;

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
            if (!navio.Systems.ContainsKey(SystemTypes.Sabotage))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Sabotagem aleatória: o mapa atual não possui o sistema nativo de sabotagem.");
                return;
            }

            // No IL2CPP, Systems armazena ISystemType. O EHR também converte
            // esse objeto para SabotageSystemType antes de ler Timer/AnyActive.
            SabotageSystemType sistema = navio.Systems[SystemTypes.Sabotage]
                .Cast<SabotageSystemType>();

            if (sistema == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Sabotagem aleatória: não foi possível obter SabotageSystemType.");
                return;
            }

            if (sistema.AnyActive)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Sabotagem aleatória ignorada: já existe uma sabotagem ativa.");
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

            SystemTypes escolhida = disponiveis[Sorteador.Next(disponiveis.Count)];

            // O jogo recusa novas sabotagens enquanto Timer > 0. Como este evento
            // foi acionado pela votação, zeramos o cooldown nativo antes do RPC.
            // O próprio jogo definirá o cooldown novamente quando aceitar a sabotagem.
            float cooldownAnterior = sistema.Timer;
            sistema.Timer = 0f;
            sistema.IsDirty = true;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Sabotagem aleatória: mapa ID=" + mapa +
                "; opções=[" + string.Join(", ", disponiveis) +
                "]; escolhida=" + escolhida +
                "; cooldown anterior=" + cooldownAnterior + "s.");

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
        // IDs de mapa do Among Us: 0 Skeld, 1 Mira HQ, 2 Polus,
        // 3 Dleks, 4 Airship, 5 Fungle.
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

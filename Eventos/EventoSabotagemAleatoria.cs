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

            int mapaOpcoes = GameOptionsManager.Instance.CurrentGameOptions.MapId;
            int mapaLogica = GameManager.Instance.LogicOptions.MapId;
            int mapa = DetectarMapaCarregado(navio, mapaLogica);
            List<SystemTypes> disponiveis = ObterSabotagensDoMapa(mapa);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Detecção de mapa: ShipStatus=" + navio.GetType().Name +
                "; mapa detectado=" + mapa +
                "; LogicOptions.MapId=" + mapaLogica +
                "; CurrentGameOptions.MapId=" + mapaOpcoes + ".");

            if (disponiveis.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Sabotagem aleatória: mapa não reconhecido ou sem lista configurada. MapId=" + mapa + ".");
                return;
            }

            SystemTypes escolhida = disponiveis[Sorteador.Next(disponiveis.Count)];

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

    private static int DetectarMapaCarregado(ShipStatus navio, int mapaFallback)
    {
        // Primeiro usa sistemas exclusivos do mapa realmente carregado.
        // IDs oficiais: 0 Skeld, 1 Mira HQ, 2 Polus, 3 Dleks, 4 Airship, 5 Fungle.
        if (navio.Systems.ContainsKey(SystemTypes.HeliSabotage))
            return 4;

        if (navio.Systems.ContainsKey(SystemTypes.MushroomMixupSabotage))
            return 5;

        if (navio.Systems.ContainsKey(SystemTypes.Laboratory))
            return 2;

        // Os mapas clássicos compartilham vários sistemas; nesses casos,
        // o tipo real do objeto ShipStatus ajuda a distinguir Skeld de Mira.
        string tipoNavio = navio.GetType().Name;
        if (tipoNavio.IndexOf("Airship", StringComparison.OrdinalIgnoreCase) >= 0)
            return 4;
        if (tipoNavio.IndexOf("Fungle", StringComparison.OrdinalIgnoreCase) >= 0)
            return 5;
        if (tipoNavio.IndexOf("Polus", StringComparison.OrdinalIgnoreCase) >= 0)
            return 2;
        if (tipoNavio.IndexOf("Mira", StringComparison.OrdinalIgnoreCase) >= 0)
            return 1;
        if (tipoNavio.IndexOf("Skeld", StringComparison.OrdinalIgnoreCase) >= 0)
            return 0;
        if (tipoNavio.IndexOf("Dleks", StringComparison.OrdinalIgnoreCase) >= 0)
            return 3;

        // Só recorre às opções se não houver assinatura reconhecível do mapa carregado.
        return mapaFallback;
    }

    private static List<SystemTypes> ObterSabotagensDoMapa(int mapa)
    {
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
                SystemTypes.Comms
            },
            5 => new List<SystemTypes>
            {
                SystemTypes.Electrical,
                SystemTypes.MushroomMixupSabotage,
                SystemTypes.Comms
            },
            _ => new List<SystemTypes>()
        };
    }
}

using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Testa a atribuição de funções durante a seleção inicial do Vanilla.
/// Inspirado no fluxo do EHR: intercepta RpcSetRole enquanto a partida está
/// sendo preparada e mantém canOverrideRole=true para os dois jogadores escolhidos.
/// </summary>
[HarmonyPatch]
internal static class EscondeEscondeRoleSelectionPatch
{
    private static PlayerControl cacador;
    private static PlayerControl fugitivo;
    private static bool selecionandoPapeis;
    private static readonly System.Random Sorteador = new();

    // Este Prefix roda antes do fluxo de início do host, quando os jogadores
    // ainda estão disponíveis para a seleção inicial de papéis.
    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGameHost))]
    [HarmonyPrefix]
    private static void AntesDeIniciarPartida()
    {
        selecionandoPapeis = false;
        cacador = null;
        fugitivo = null;

        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data != null && !jogador.Data.Disconnected && !jogador.Data.IsDead)
                candidatos.Add(jogador);
        }

        if (candidatos.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[RoleSelectionTest] Menos de dois jogadores; atribuição especial ignorada.");
            return;
        }

        int indiceCacador = Sorteador.Next(candidatos.Count);
        int indiceFugitivo;
        do { indiceFugitivo = Sorteador.Next(candidatos.Count); }
        while (indiceFugitivo == indiceCacador);

        cacador = candidatos[indiceCacador];
        fugitivo = candidatos[indiceFugitivo];
        selecionandoPapeis = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[RoleSelectionTest] Seleção inicial preparada: caçador={cacador.Data.PlayerName} " +
            $"(id={cacador.PlayerId}), fugitivo={fugitivo.Data.PlayerName} (id={fugitivo.PlayerId}). " +
            "Durante a atribuição inicial, serão usados Impostor/Engineer com canOverrideRole=true.");
    }

    // O seletor de papéis do jogo continua rodando normalmente. Só substituímos
    // o papel atribuído aos dois jogadores escolhidos enquanto a seleção inicial
    // estiver acontecendo.
    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole), new Type[] { typeof(RoleTypes), typeof(bool) })]
    [HarmonyPrefix]
    private static void AntesDeDefinirPapel(PlayerControl __instance, ref RoleTypes __0, ref bool __1)
    {
        if (!selecionandoPapeis || __instance == null)
            return;

        if (cacador != null && __instance.PlayerId == cacador.PlayerId)
        {
            __0 = RoleTypes.Impostor;
            __1 = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSelectionTest] RpcSetRole interceptado: jogador={__instance.PlayerId}, papel=Impostor, canOverrideRole=true.");
        }
        else if (fugitivo != null && __instance.PlayerId == fugitivo.PlayerId)
        {
            __0 = RoleTypes.Engineer;
            __1 = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSelectionTest] RpcSetRole interceptado: jogador={__instance.PlayerId}, papel=Engineer, canOverrideRole=true.");
        }
    }

    // O navio só inicia depois que a atribuição de papéis foi concluída.
    // Desativamos a interceptação para não bloquear mudanças de papel durante a partida.
    [HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
    [HarmonyPostfix]
    private static void DepoisDeIniciarNave()
    {
        if (selecionandoPapeis)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[RoleSelectionTest] Fim da seleção inicial; interceptação de papéis desativada.");
        }

        selecionandoPapeis = false;
        cacador = null;
        fugitivo = null;
    }

    internal static void Resetar()
    {
        selecionandoPapeis = false;
        cacador = null;
        fugitivo = null;
    }
}

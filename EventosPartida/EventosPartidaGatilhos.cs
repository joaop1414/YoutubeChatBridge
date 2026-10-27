using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Os eventos F7 podem ser armados no lobby; só são ativados depois de ShipStatus.Start.
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
internal static class EventosPartidaInicioPatch
{
    private static void Postfix()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        EventoTeletransportarAlvoParaMetamorfoPartida.IniciarPartida();
        EventoTeletransportarJogadorAoCompletarTarefa.IniciarPartida();
        EventoTarefaMortal.IniciarPartida();
        EventoAssassinatoDesviado.IniciarPartida();
        EventoTrocaDeDestinos.IniciarPartida();
        EventoUltimoSuspiro.IniciarPartida();
        EventoDutosInstaveis.IniciarPartida();
        EventoImpostorSuicidaAoTentarMatar.IniciarPartida();
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
internal static class EventosPartidaAtualizarPatch
{
    private static void Postfix()
    {
        EventoTrocaDeDestinos.Atualizar();
        EventoDutosInstaveis.Atualizar();
    }
}

[HarmonyPatch(typeof(PlayerControl), "CompleteTask")]
internal static class EventoPartidaTarefaMortalPatch
{
    private static void Postfix(PlayerControl __instance)
    {
        EventoTarefaMortal.AoCompletarTarefa(__instance);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class EventoPartidaAssassinatoDesviadoPatch
{
    private static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (EventoImpostorSuicidaAoTentarMatar.AoTentarMatar(__instance, target)) return false;
        return !EventoAssassinatoDesviado.AoTentarMatar(__instance, target);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcMurderPlayer))]
internal static class EventoPartidaUltimoSuspiroPatch
{
    private static void Postfix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (target != null && target.Data != null && target.Data.IsDead)
            EventoUltimoSuspiro.AoMorrer(target);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class EventosPartidaFimPatch
{
    private static void Postfix()
    {
        YouTubeVoteBridgePlugin.LimparEventoF7Pendente();
        EventoTeletransportarAlvoParaMetamorfoPartida.Resetar();
        EventoTeletransportarJogadorAoCompletarTarefa.Desativar("fim da partida");
        EventoTarefaMortal.Resetar();
        EventoAssassinatoDesviado.Resetar();
        EventoTrocaDeDestinos.Resetar();
        EventoUltimoSuspiro.Resetar();
        EventoDutosInstaveis.Resetar();
        EventoImpostorSuicidaAoTentarMatar.Resetar();
    }
}

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
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
internal static class EventosPartidaAtualizarPatch
{
    private static void Postfix()
    {
        EventoTrocaDeDestinos.Atualizar();
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

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.RpcEnterVent))]
internal static class EventoPartidaDutosInstaveisPatch
{
    private static void Postfix(PlayerPhysics __instance, [HarmonyArgument(0)] Vent vent)
    {
        EventoDutosInstaveis.AoEntrarNoDuto(__instance, vent);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class EventosPartidaFimPatch
{
    private static void Postfix()
    {
        EventoTeletransportarAlvoParaMetamorfoPartida.Resetar();
        EventoTeletransportarJogadorAoCompletarTarefa.Desativar("fim da partida");
        EventoTarefaMortal.Resetar();
        EventoAssassinatoDesviado.Resetar();
        EventoTrocaDeDestinos.Resetar();
        EventoUltimoSuspiro.Resetar();
        EventoDutosInstaveis.Resetar();
    }
}

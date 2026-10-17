using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

// O EHR usa RpcMakeInvisible() sem argumento para ativar a invisibilidade.
[HarmonyPatch]
internal static class EventoInterceptarRpcPhantom
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase[] metodos = typeof(PlayerControl).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.GetParameters().Length == 0 &&
                (m.Name.IndexOf("MakeInvisible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 m.Name.IndexOf("Invisib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 m.Name.IndexOf("Vanish", StringComparison.OrdinalIgnoreCase) >= 0))
            .OrderByDescending(m => m.Name.IndexOf("Rpc", StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();
        if (metodos.Length > 0) return metodos;

        MethodBase fallback = AccessTools.Method(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift));
        if (fallback != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Phantom/teleporte] RpcMakeInvisible() não foi encontrado em PlayerControl; o teleporte não poderá disparar até identificarmos o método real.");
            return new[] { fallback };
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogError(
            "[Phantom/teleporte] Não foi encontrado método de invisibilidade nem CheckShapeshift para fallback.");
        return Array.Empty<MethodBase>();
    }

    private static void Prefix(PlayerControl __instance, MethodBase __originalMethod)
    {
        if (__instance == null || __originalMethod == null ||
            (!__originalMethod.Name.Contains("Invisib", StringComparison.OrdinalIgnoreCase) &&
             !__originalMethod.Name.Contains("Vanish", StringComparison.OrdinalIgnoreCase)) ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;
        EventoTeletransportarAleatorioParaPhantom.AoAtivarInvisibilidade(__instance);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

// A habilidade do Phantom passa pelo método nativo SetRoleInvisibility(bool, bool, bool).
[HarmonyPatch]
internal static class EventoInterceptarRpcPhantom
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        MethodBase[] metodos = typeof(PlayerControl).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase) &&
                m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(bool))
            .Cast<MethodBase>()
            .ToArray();

        if (metodos.Length > 0) return metodos;

        MethodBase fallback = AccessTools.Method(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift));
        if (fallback != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Phantom/teleporte] SetRoleInvisibility não foi encontrado; o teleporte do Espectro não será disparado até identificar o método usado nesta versão.");
            return new[] { fallback };
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogError(
            "[Phantom/teleporte] Não foi encontrado SetRoleInvisibility nem CheckShapeshift para fallback.");
        return Array.Empty<MethodBase>();
    }

    private static void Prefix(PlayerControl __instance, object[] __args, MethodBase __originalMethod)
    {
        if (__instance == null || __args == null || __args.Length == 0 ||
            __originalMethod == null ||
            !string.Equals(__originalMethod.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase) ||
            __args[0] is not bool invisivel || !invisivel ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;

        EventoTeletransportarAleatorioParaPhantom.AoAtivarInvisibilidade(__instance);
    }
}

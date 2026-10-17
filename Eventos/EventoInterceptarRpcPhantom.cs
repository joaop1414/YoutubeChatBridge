using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

// O nome exato do RPC mudou entre builds; encontra dinamicamente os RPCs
// de invisibilidade com um único bool, sem depender de um nome hardcoded.
[HarmonyPatch]
internal static class EventoInterceptarRpcPhantom
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(PlayerControl).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m =>
                (m.Name.StartsWith("Rpc", StringComparison.OrdinalIgnoreCase) &&
                 (m.Name.IndexOf("Invisib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                  m.Name.IndexOf("Vanish", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                string.Equals(m.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase))
            .Where(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 1 && p[0].ParameterType == typeof(bool);
            })
            .OrderByDescending(m => m.Name.StartsWith("Rpc", StringComparison.OrdinalIgnoreCase));
    }

    private static void Prefix(PlayerControl __instance, object[] __args)
    {
        if (__instance == null || __args == null || __args.Length != 1 ||
            __args[0] is not bool invisivel || !invisivel ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;

        EventoTeletransportarAleatorioParaPhantom.AoAtivarInvisibilidade(__instance);
    }
}

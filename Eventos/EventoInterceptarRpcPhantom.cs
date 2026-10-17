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
        MethodBase[] metodos = typeof(PlayerControl).GetMethods(
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
            .OrderByDescending(m => m.Name.StartsWith("Rpc", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (metodos.Length > 0)
            return metodos;

        // Algumas versões do Among Us não expõem o RPC de invisibilidade como
        // método de PlayerControl. Harmony exige pelo menos um alvo válido:
        // usa CheckShapeshift como fallback inofensivo para não impedir o mod
        // inteiro de carregar. O Prefix ignora chamadas sem argumento bool.
        MethodBase fallback = AccessTools.Method(
            typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift));

        if (fallback != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Phantom/teleporte] Nenhum método RPC de invisibilidade foi encontrado; usando fallback seguro. O gatilho do Phantom precisa ser identificado para esta versão do jogo.");
            return new[] { fallback };
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogError(
            "[Phantom/teleporte] Não foi encontrado RPC de invisibilidade nem CheckShapeshift para fallback.");
        return Array.Empty<MethodBase>();
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

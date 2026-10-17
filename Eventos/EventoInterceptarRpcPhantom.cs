using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

// Intercepta a invisibilidade nativa para bloquear a habilidade do Phantom.
// Não dispara teleporte: esse efeito foi removido para impedir que a invisibilidade
// global seja confundida com a habilidade do Phantom.
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
                "[Phantom] SetRoleInvisibility não foi encontrado; o bloqueio da habilidade não será aplicado nesta versão.");
            return new[] { fallback };
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogError(
            "[Phantom] Não foi encontrado SetRoleInvisibility nem CheckShapeshift para fallback.");
        return Array.Empty<MethodBase>();
    }

    private static bool Prefix()
    {
        // O EHR não bloqueia SetRoleInvisibility(true) aqui. O cliente pode
        // iniciar o estado visual nativo, e a recuperação do host usa
        // SetRole(Phantom, CanOverrideRole=true) + ProtectPlayer para desfazê-lo.
        // Bloquear esta chamada isoladamente pode deixar animação/estado visual
        // do Phantom dessincronizados e resultar em tela preta.
        return true;
    }
}

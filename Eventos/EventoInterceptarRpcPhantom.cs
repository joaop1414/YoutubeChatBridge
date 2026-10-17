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

    private static bool Prefix(PlayerControl __instance, object[] __args, MethodBase __originalMethod)
    {
        // O evento global precisa usar o mecanismo nativo em todos os jogadores;
        // somente chamadas normais da habilidade do Phantom devem ser bloqueadas.
        if (EventoTodosFicamInvisiveis.AplicandoEvento)
            return true;

        if (__instance == null || __args == null || __args.Length == 0 ||
            __originalMethod == null ||
            !string.Equals(__originalMethod.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase) ||
            __args[0] is not bool invisivel || !invisivel)
            return true;

        string papel = __instance.Data?.Role?.GetType().Name + " " + __instance.Data?.Role?.ToString();
        bool ePhantom = !string.IsNullOrEmpty(papel) &&
            papel.IndexOf("Phantom", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!ePhantom) return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] Invisibilidade bloqueada para PlayerId={__instance.PlayerId}.");
        return false;
    }
}

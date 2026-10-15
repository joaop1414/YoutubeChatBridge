using HarmonyLib;

namespace YouTubeVoteBridge;

// Solicita Host Authority usando o sinal +25 do protocolo de rede.
// Isso deve ser testado em salas vanilla: não garante que a expulsão seja corrigida.
[HarmonyPatch(typeof(Constants), nameof(Constants.GetBroadcastVersion))]
public static class HostAuthorityPatch
{
    public static void Postfix(ref int __result)
    {
        __result += 25;
    }
}

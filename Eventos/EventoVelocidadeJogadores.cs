using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Altera a velocidade física por jogador. O estado precisa existir em cada cliente
// para afetar o dono do movimento; esta implementação ainda não sincroniza RPCs.
internal static class EventoVelocidadeJogadores
{
    private static readonly Dictionary<byte, float> Multiplicadores = new();
    private static readonly Dictionary<byte, float> ExpiraEm = new();

    public static void AplicarMultiplicador(byte playerId, float multiplicador, float duracao)
    {
        Multiplicadores[playerId] = multiplicador;
        ExpiraEm[playerId] = Time.realtimeSinceStartup + duracao;
    }

    public static void Limpar(byte playerId)
    {
        Multiplicadores.Remove(playerId);
        ExpiraEm.Remove(playerId);
    }

    public static void Atualizar()
    {
        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, float> item in ExpiraEm)
            if (agora >= item.Value)
                expirados.Add(item.Key);

        foreach (byte id in expirados)
            Limpar(id);
    }

    public static float ObterMultiplicador(PlayerControl jogador)
    {
        if (jogador == null)
            return 1f;

        return Multiplicadores.TryGetValue(jogador.PlayerId, out float valor)
            ? valor
            : 1f;
    }
}

[HarmonyPatch(typeof(PlayerPhysics), nameof(PlayerPhysics.FixedUpdate))]
internal static class EventoVelocidadePhysicsPatch
{
    public static void Postfix(PlayerPhysics __instance)
    {
        if (__instance == null || __instance.myPlayer == null || !__instance.AmOwner)
            return;

        float multiplicador = EventoVelocidadeJogadores.ObterMultiplicador(__instance.myPlayer);
        if (multiplicador != 1f && __instance.body != null)
            __instance.body.velocity *= multiplicador;
    }
}

[HarmonyPatch(typeof(CustomNetworkTransform), nameof(CustomNetworkTransform.FixedUpdate))]
internal static class EventoVelocidadeNetworkPatch
{
    public static void Postfix(CustomNetworkTransform __instance)
    {
        if (__instance == null || __instance.AmOwner || __instance.body == null)
            return;

        PlayerControl jogador = __instance.GetComponent<PlayerControl>();
        if (jogador == null)
            return;

        float multiplicador = EventoVelocidadeJogadores.ObterMultiplicador(jogador);
        if (multiplicador != 1f)
            __instance.body.velocity *= multiplicador;
    }
}

internal static class EventoVelocidadeTemporaria
{
    private const float DuracaoSegundos = 15f;

    public static void Supervelocidade() => AplicarATodos(2.5f);
    public static void VelocidadeReduzida() => AplicarATodos(0.35f);

    private static void AplicarATodos(float multiplicador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        int afetados = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            EventoVelocidadeJogadores.AplicarMultiplicador(
                jogador.PlayerId, multiplicador, DuracaoSegundos);
            afetados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Evento de velocidade aplicado: multiplicador={multiplicador:0.####}x, " +
            $"jogadores={afetados}, duração={DuracaoSegundos:0}s.");
    }
}

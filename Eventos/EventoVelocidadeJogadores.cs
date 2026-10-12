using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Overrides the final movement speed per player. Because movement is calculated on
// each player's client, every modded client applies the same override locally.
internal static class EventoVelocidadeJogadores
{
    private static readonly Dictionary<byte, float> VelocidadesAbsolutas = new();
    private static readonly Dictionary<byte, float> Multiplicadores = new();
    private static readonly Dictionary<byte, float> ExpiraEm = new();

    public static void DefinirVelocidadeAbsoluta(byte playerId, float velocidade)
    {
        VelocidadesAbsolutas[playerId] = velocidade;
    }

    public static void LimparVelocidadeAbsoluta(byte playerId)
    {
        VelocidadesAbsolutas.Remove(playerId);
    }

    public static void AplicarMultiplicador(byte playerId, float multiplicador, float duracao)
    {
        Multiplicadores[playerId] = multiplicador;
        ExpiraEm[playerId] = Time.realtimeSinceStartup + duracao;
    }

    public static void Atualizar()
    {
        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, float> item in ExpiraEm)
            if (agora >= item.Value)
                expirados.Add(item.Key);

        foreach (byte id in expirados)
        {
            ExpiraEm.Remove(id);
            Multiplicadores.Remove(id);
        }
    }

    public static float ModificarVelocidade(PlayerControl jogador, float velocidade)
    {
        if (jogador == null)
            return velocidade;

        if (VelocidadesAbsolutas.TryGetValue(jogador.PlayerId, out float absoluta))
            return absoluta;

        if (Multiplicadores.TryGetValue(jogador.PlayerId, out float multiplicador))
            return velocidade * multiplicador;

        return velocidade;
    }
}

[HarmonyPatch(typeof(PlayerControl), "GetModifiedSpeed")]
internal static class EventoVelocidadePatch
{
    public static void Postfix(PlayerControl __instance, ref float __result)
    {
        __result = EventoVelocidadeJogadores.ModificarVelocidade(__instance, __result);
    }
}

internal static class EventoVelocidadeTemporaria
{
    private const float DuracaoSegundos = 15f;

    public static void Supervelocidade()
    {
        AplicarATodos(2.5f);
    }

    public static void VelocidadeReduzida()
    {
        AplicarATodos(0.35f);
    }

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
            $"Evento de velocidade aplicado: multiplicador={multiplicador:0.##}x, " +
            $"jogadores={afetados}, duração={DuracaoSegundos:0}s.");
    }
}

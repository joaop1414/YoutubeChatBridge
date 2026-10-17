using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Mantém isolada a parte visual do Shapeshifter que copia um jogador Rainbow.
// Não altera RPCs nem a cor de rede: copia apenas as cores interpoladas dos
// materiais, localmente, para que a transformação acompanhe o arco-íris do alvo.
internal static class RainbowShapeshiftController
{
    private sealed class Transformacao
    {
        public byte AlvoId;
        public Transformacao(byte alvoId) => AlvoId = alvoId;
    }

    private static readonly Dictionary<byte, Transformacao> Transformacoes = new();

    internal static void Iniciar(PlayerControl shapeshifter, PlayerControl alvo)
    {
        if (shapeshifter == null || alvo == null || shapeshifter.PlayerId == alvo.PlayerId)
            return;

        Transformacoes[shapeshifter.PlayerId] = new Transformacao(alvo.PlayerId);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Rainbow shapeshift visual: iniciado; jogador={shapeshifter.PlayerId}, alvo={alvo.PlayerId}.");
    }

    internal static void Encerrar(byte playerId)
    {
        if (Transformacoes.Remove(playerId))
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Rainbow shapeshift visual: encerrado; jogador={playerId}.");
    }

    internal static void EncerrarTodos()
    {
        Transformacoes.Clear();
    }

    internal static void Atualizar(PlayerControl shapeshifter)
    {
        if (shapeshifter == null || shapeshifter.Data == null ||
            shapeshifter.cosmetics == null ||
            !Transformacoes.TryGetValue(shapeshifter.PlayerId, out Transformacao transformacao))
            return;

        PlayerControl alvo = EncontrarJogador(transformacao.AlvoId);
        if (alvo == null || alvo.Data == null || alvo.cosmetics == null)
        {
            Encerrar(shapeshifter.PlayerId);
            return;
        }

        try
        {
            var corpoAlvo = alvo.cosmetics.currentBodySprite;
            var corpoJogador = shapeshifter.cosmetics.currentBodySprite;
            if (corpoAlvo?.BodySprite == null || corpoJogador?.BodySprite == null)
                return;

            Material materialAlvo = corpoAlvo.BodySprite.material;
            Material materialJogador = corpoJogador.BodySprite.material;
            if (materialAlvo == null || materialJogador == null)
                return;

            // Copia a cor renderizada, incluindo a interpolação suave e a sombra.
            if (materialAlvo.HasProperty("_BodyColor") && materialJogador.HasProperty("_BodyColor"))
                materialJogador.SetColor("_BodyColor", materialAlvo.GetColor("_BodyColor"));
            if (materialAlvo.HasProperty("_BackColor") && materialJogador.HasProperty("_BackColor"))
                materialJogador.SetColor("_BackColor", materialAlvo.GetColor("_BackColor"));
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                $"Rainbow shapeshift visual: falha ao copiar material de PlayerId={shapeshifter.PlayerId}: {ex.Message}");
        }
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        if (PlayerControl.AllPlayerControls == null)
            return null;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;

        return null;
    }
}

[HarmonyPatch(typeof(PlayerControl), "FixedUpdate")]
internal static class RainbowShapeshiftControllerPatch
{
    private static void Postfix(PlayerControl __instance)
    {
        RainbowShapeshiftController.Atualizar(__instance);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class RainbowShapeshiftMeetingCleanupPatch
{
    private static void Prefix()
    {
        RainbowShapeshiftController.EncerrarTodos();
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class RainbowShapeshiftGameEndCleanupPatch
{
    private static void Prefix()
    {
        RainbowShapeshiftController.EncerrarTodos();
    }
}

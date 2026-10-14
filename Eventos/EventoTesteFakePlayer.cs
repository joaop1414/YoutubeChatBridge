using System;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Evento temporário para validar o ciclo de vida da FakePlayerAPI.
/// Cria um fake perto do host, move-o entre pontos e remove-o após 8 segundos.
/// </summary>
internal sealed class EventoTesteFakePlayer : EventoVotacao
{
    public override string Nome => "TESTE - Fake Player API";

    private static PlayerControl falso;
    private static Vector2 origem;
    private static float inicio;
    private static float proximaMovimentacao;
    private static int etapa;
    private static bool ativo;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null ||
            ShipStatus.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[FakePlayerAPI Teste] Entre em uma partida como host para executar.");
            return;
        }

        if (ativo)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[FakePlayerAPI Teste] O teste já está ativo.");
            return;
        }

        try
        {
            origem = PlayerControl.LocalPlayer.transform.position;
            falso = FakePlayerAPI.Criar(origem + new Vector2(1.5f, 0f));

            if (falso == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[FakePlayerAPI Teste] FakePlayerAPI.Criar retornou null.");
                return;
            }

            inicio = Time.realtimeSinceStartup;
            proximaMovimentacao = inicio + 1.0f;
            etapa = 0;
            ativo = true;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[FakePlayerAPI Teste] Criado: NetId={falso.NetId}, " +
                $"PlayerId={falso.PlayerId}. Duração prevista: 8 segundos.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[FakePlayerAPI Teste] Falha ao iniciar: " + ex);
            Finalizar();
        }
    }

    internal static void Atualizar()
    {
        if (!ativo)
            return;

        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            ShipStatus.Instance == null ||
            falso == null)
        {
            Finalizar();
            return;
        }

        float agora = Time.realtimeSinceStartup;

        if (agora - inicio >= 8.0f)
        {
            Finalizar();
            return;
        }

        if (agora < proximaMovimentacao)
            return;

        Vector2[] pontos =
        {
            origem + new Vector2(1.5f, 0f),
            origem + new Vector2(1.5f, 1.0f),
            origem + new Vector2(0f, 1.0f),
            origem + new Vector2(-1.0f, 0f)
        };

        etapa = (etapa + 1) % pontos.Length;
        bool enviado = FakePlayerAPI.Mover(falso, pontos[etapa]);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[FakePlayerAPI Teste] Movimento {etapa + 1}/{pontos.Length}: " +
            $"posição=({pontos[etapa].x:0.00}, {pontos[etapa].y:0.00}), enviado={enviado}.");

        proximaMovimentacao = agora + 1.0f;
    }

    private static void Finalizar()
    {
        ativo = false;
        proximaMovimentacao = 0f;

        if (falso != null)
        {
            try
            {
                bool removido = FakePlayerAPI.Destruir(falso);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[FakePlayerAPI Teste] Remoção solicitada. Resultado=" + removido + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[FakePlayerAPI Teste] Falha ao remover fake: " + ex);
            }
        }

        falso = null;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class EventoTesteFakePlayerAtualizacaoPatch
{
    public static void Postfix()
    {
        EventoTesteFakePlayer.Atualizar();
    }
}

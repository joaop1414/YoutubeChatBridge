using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;
using AmongUs.GameOptions;

namespace YouTubeVoteBridge;

internal sealed class EventoEscudoTemporario : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    private static readonly System.Random Sorteador = new();
    private static readonly Dictionary<byte, float> ProtecoesAtivas = new();

    public override string Nome => "Escudo temporário";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        List<PlayerControl> candidatos = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                ProtecoesAtivas.ContainsKey(jogador.PlayerId))
                continue;

            candidatos.Add(jogador);
        }

        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Escudo temporário cancelado: não há jogadores vivos sem escudo.");
            return;
        }

        PlayerControl alvo = candidatos[Sorteador.Next(candidatos.Count)];
        ProtecoesAtivas[alvo.PlayerId] = Time.realtimeSinceStartup + DuracaoSegundos;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Escudo temporário ativado para {alvo.Data.PlayerName} " +
            $"(PlayerId={alvo.PlayerId}) por {DuracaoSegundos:0} segundos.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ProtecoesAtivas.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, float> item in ProtecoesAtivas)
        {
            PlayerControl jogador = EncontrarJogador(item.Key);

            if (agora >= item.Value || jogador == null ||
                jogador.Data == null || jogador.Data.IsDead ||
                jogador.Data.Disconnected)
            {
                expirados.Add(item.Key);
            }
        }

        foreach (byte id in expirados)
        {
            ProtecoesAtivas.Remove(id);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Escudo temporário encerrado para PlayerId={id}.");
        }
    }

    private static PlayerControl EncontrarJogador(byte id)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.PlayerId == id)
                return jogador;
        }

        return null;
    }

    internal static bool EstaProtegido(byte id)
    {
        return ProtecoesAtivas.TryGetValue(id, out float fim) &&
               Time.realtimeSinceStartup < fim;
    }
}

// Intercepta a tentativa de assassinato no fluxo autoritativo do host.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class EventoEscudoTemporarioMortePatch
{
    private static readonly HashSet<byte> ReacaoEmAndamento = new();

    public static bool Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || __instance == null || alvo == null)
            return true;

        if (!EventoEscudoTemporario.EstaProtegido(alvo.PlayerId))
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Tentativa de morte bloqueada pelo escudo temporário: " +
            $"{alvo.Data?.PlayerName ?? "jogador desconhecido"} " +
            $"(PlayerId={alvo.PlayerId}, atacante={__instance.Data?.PlayerName ?? "desconhecido"}).");

        // Só aplica a reação especial quando um impostor tenta matar o alvo protegido.
        if (__instance.Data != null &&
            !__instance.Data.IsDead &&
            !__instance.Data.Disconnected &&
            __instance.Data.Role != null &&
            __instance.Data.Role.IsImpostor &&
            !ReacaoEmAndamento.Contains(__instance.PlayerId))
        {
            ReacaoEmAndamento.Add(__instance.PlayerId);
            try
            {
                AplicarEscudoEResetarCooldown(__instance);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao aplicar a reação do Anjo da Guarda: " + ex);
            }
            finally
            {
                ReacaoEmAndamento.Remove(__instance.PlayerId);
            }
        }

        // A tentativa original contra o jogador protegido nunca prossegue.
        return false;
    }

    private static void AplicarEscudoEResetarCooldown(PlayerControl impostor)
    {
        if (AmongUsClient.Instance == null || impostor == null)
            return;

        float cooldownOriginal = GameOptionsManager.Instance != null
            ? GameOptionsManager.Instance.CurrentGameOptions.GetFloat(FloatOptionNames.KillCooldown)
            : 30f;

        impostor.SetKillTimer(cooldownOriginal * 2f);

        const int corEscudo = 0;

        // O RPC do escudo é direcionado ao cliente dono do impostor.
        // Assim a proteção chega antes da tentativa de suicídio no mesmo canal confiável.
        if (impostor.AmOwner)
        {
            impostor.ProtectPlayer(impostor, corEscudo);
        }
        else
        {
            MessageWriter escudo = AmongUsClient.Instance.StartRpcImmediately(
                impostor.NetId,
                (byte)RpcCalls.ProtectPlayer,
                SendOption.Reliable,
                impostor.OwnerId
            );
            escudo.WriteNetObject(impostor);
            escudo.Write(corEscudo);
            AmongUsClient.Instance.FinishRpcImmediately(escudo);
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Escudo do Anjo da Guarda enviado ao cliente do impostor " +
            $"{impostor.Data?.PlayerName} (PlayerId={impostor.PlayerId}).");

        // Envia a tentativa de auto-morte SOMENTE ao cliente que controla o impostor.
        // Não chamar CheckMurder nem RpcMurderPlayer aqui, pois essas chamadas
        // podem propagar a morte para todos os clientes.
        MessageWriter suicidio = AmongUsClient.Instance.StartRpcImmediately(
            impostor.NetId,
            (byte)RpcCalls.MurderPlayer,
            SendOption.Reliable,
            impostor.OwnerId
        );
        suicidio.WriteNetObject(impostor);
        suicidio.Write(true);
        AmongUsClient.Instance.FinishRpcImmediately(suicidio);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"RPC de auto-morte enviado somente ao cliente do impostor " +
            $"{impostor.Data?.PlayerName} (PlayerId={impostor.PlayerId}); " +
            $"cooldown temporário dobrado para {cooldownOriginal * 2f:0.0}s.");

        // Reverte a duplicação deixando o timer no cooldown normal completo.
        impostor.SetKillTimer(cooldownOriginal);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Cooldown do impostor restaurado para {cooldownOriginal:0.0}s.");
    }
}

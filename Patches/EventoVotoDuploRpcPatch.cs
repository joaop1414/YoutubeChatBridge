using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using InnerNet;

namespace YouTubeVoteBridge;

// O EHR aplica os votos adicionais dentro de CheckForEndVoting,
// antes de calcular a ejeção e antes de enviar RpcVotingComplete.
// Fazemos o mesmo para que o voto extra conte de verdade e apareça
// como uma segunda entrada para os clientes vanilla.
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.CheckForEndVoting))]
[HarmonyPriority(Priority.First)]
internal static class EventoVotoDuploRpcPatch
{
    public static bool Prefix(MeetingHud __instance)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            __instance == null ||
            __instance.playerStates == null ||
            !EventoVotoDuplo.TentarObterJogador(out byte jogadorComVotoDuplo))
            return true;

        try
        {
            if (__instance.state == MeetingHud.MeetingStates.Results ||
                __instance.state == MeetingHud.MeetingStates.Proceeding)
                return true;

            // Só assumimos o cálculo quando todos os jogadores vivos e conectados
            // já votaram. Antes disso, o CheckForEndVoting vanilla continua normal.
            bool todosVotaram = true;
            foreach (PlayerVoteArea area in __instance.playerStates)
            {
                if (area == null || area.AmDead || area.DidVote)
                    continue;

                byte id = area.PlayerId.Value;
                NetworkedPlayerInfo dados = GameData.Instance?.GetPlayerById(id);
                PlayerControl jogador = dados?.Object;
                if (dados == null || jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || jogador.Data.IsDead)
                    continue;

                todosVotaram = false;
                break;
            }

            if (!todosVotaram)
                return true;

            // Se a habilidade do Juiz venceu, deixamos o fluxo vanilla resolver
            // a prioridade do Juiz sem interferência do evento de voto duplo.
            if (__instance.TryGetWinningOverrule(
                out JudgeOverrule _,
                out NetworkedPlayerInfo _,
                out NetworkedPlayerInfo _))
                return true;

            var estados = new List<MeetingHud.VoterState>();
            var contagem = new Dictionary<byte, int>();
            bool votoDuploAplicado = false;

            foreach (PlayerVoteArea area in __instance.playerStates)
            {
                if (area == null || area.AmDead)
                    continue;

                byte eleitorId = area.PlayerId.Value;
                NetworkedPlayerInfo dadosEleitor = GameData.Instance?.GetPlayerById(eleitorId);
                PlayerControl eleitor = dadosEleitor?.Object;
                if (dadosEleitor == null || eleitor == null || eleitor.Data == null ||
                    eleitor.Data.Disconnected || eleitor.Data.IsDead || !area.DidVote)
                    continue;

                byte alvoId = area.VotedForId;
                var voto = new MeetingHud.VoterState
                {
                    VoterId = eleitorId,
                    VotedForId = alvoId
                };
                estados.Add(voto);

                // 253 é skip e conta na votação; 254/255 representam ausência de voto.
                if (alvoId < 254)
                {
                    contagem.TryGetValue(alvoId, out int atual);
                    contagem[alvoId] = atual + 1;
                }

                if (eleitorId == jogadorComVotoDuplo && alvoId < 254)
                {
                    // TESTE: representa o voto extra com um VoterId reservado que não pertence a nenhum jogador.
                    // 252 fica fora da faixa normal de PlayerId (0-14) e não é um marcador de skip/sem voto.
                    estados.Add(new MeetingHud.VoterState
                    {
                        VoterId = 252,
                        VotedForId = alvoId
                    });
                    contagem.TryGetValue(alvoId, out int atualDuplicado);
                    contagem[alvoId] = atualDuplicado + 1;
                    votoDuploAplicado = true;
                }
            }

            if (!votoDuploAplicado)
                return true;

            byte vencedor = byte.MaxValue;
            int maior = 0;
            bool empate = false;

            foreach (KeyValuePair<byte, int> voto in contagem)
            {
                if (voto.Value > maior)
                {
                    maior = voto.Value;
                    vencedor = voto.Key;
                    empate = false;
                }
                else if (voto.Value == maior)
                {
                    vencedor = byte.MaxValue;
                    empate = true;
                }
            }

            NetworkedPlayerInfo exilado = null;
            if (!empate && vencedor < 253)
                exilado = GameData.Instance?.GetPlayerById(vencedor);

            // 253 = skip. Sem vencedor ou em empate, ninguém é ejetado.
            if (vencedor == 253)
                exilado = null;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Voto duplo] CheckForEndVoting: jogador={jogadorComVotoDuplo}, " +
                $"voto duplicado, entradas={estados.Count}, alvo vencedor={vencedor}, " +
                $"maior={maior}, empate={empate}, exilado={exilado?.Object?.Data?.PlayerName ?? "nenhum"}.");

            __instance.RpcVotingComplete(estados.ToArray(), exilado, empate, false, 0);
            return false;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Voto duplo] Falha ao processar CheckForEndVoting; usando fluxo vanilla: " + ex);
            return true;
        }
    }
}

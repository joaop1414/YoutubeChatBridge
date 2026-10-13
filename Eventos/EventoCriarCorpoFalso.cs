using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoCriarCorpoFalso : EventoVotacao
{
    public override string Nome => "Criar corpo falso";

    private static readonly System.Random Sorteador = new();

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Corpo falso cancelado: já existe uma reunião em andamento.");
            return;
        }

        try
        {
            if (GameManager.Instance == null ||
                GameManager.Instance.deadBodyPrefab == null ||
                GameManager.Instance.deadBodyPrefab.Length == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Não foi possível criar corpo falso: prefab de corpo não encontrado.");
                return;
            }

            List<PlayerControl> candidatos = new();

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || jogador.Data.IsDead ||
                    jogador.KillAnimations == null ||
                    jogador.KillAnimations.Length == 0)
                    continue;

                candidatos.Add(jogador);
            }

            if (candidatos.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Corpo falso cancelado: não há jogadores vivos que possam servir de modelo visual.");
                return;
            }

            PlayerControl modelo = candidatos[Sorteador.Next(candidatos.Count)];
            Vector3 posicao = modelo.transform.position;
            Vector3 deslocamento = modelo.KillAnimations[0].BodyOffset;
            Vector3 posicaoCorpo = posicao + deslocamento;
            posicaoCorpo.z = posicaoCorpo.y / 1000f;

            int corOriginal = modelo.Data.DefaultOutfit.ColorId;
            DeadBody corpo = UnityEngine.Object.Instantiate(
                GameManager.Instance.deadBodyPrefab[0]);

            corpo.enabled = false;
            corpo.ParentId = modelo.PlayerId;

            foreach (SpriteRenderer renderer in corpo.bodyRenderers)
                modelo.SetPlayerMaterialColors(renderer);

            modelo.SetPlayerMaterialColors(corpo.bloodSplatter);
            corpo.transform.position = posicaoCorpo;
            modelo.Data.DefaultOutfit.ColorId = corOriginal;

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"Protótipo de corpo falso criado localmente em ({posicaoCorpo.x:0.00}, " +
                $"{posicaoCorpo.y:0.00}) usando PlayerId={modelo.PlayerId}. " +
                "Nesta etapa, a criação é apenas visual no host; ainda não foi sincronizada " +
                "com os clientes vanilla nem é garantido que possa ser reportada.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao criar protótipo de corpo falso: " + ex);
        }
    }
}

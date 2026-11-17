using System;
using System.Linq;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Adiciona um botão visível no lobby para abrir a tela nativa de configurações.
/// O botão reutiliza a ação do botão Editar do próprio Among Us; assim, as
/// configurações do mod aparecem na aba Jogo já criada por
/// YouTubeVoteLobbySettingsPatch.
/// </summary>
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
internal static class YouTubeVoteConfiguracoesLobbyButtonPatch
{
    private const string NOME_BOTAO = "YouTubeVoteBridge_ConfigButton";
    private static PassiveButton botaoCriado;
    private static GameStartManager gerenteAssociado;

    public static void Postfix(GameStartManager __instance)
    {
        try
        {
            if (__instance == null)
                return;

            bool podeExibir =
                AmongUsClient.Instance != null &&
                AmongUsClient.Instance.AmHost &&
                !AmongUsClient.Instance.IsGameStarted &&
                __instance.EditButton != null;

            if (!podeExibir)
            {
                if (botaoCriado != null)
                    botaoCriado.gameObject.SetActive(false);
                return;
            }

            if (botaoCriado == null ||
                gerenteAssociado != __instance ||
                botaoCriado.gameObject == null)
            {
                CriarBotao(__instance);
            }

            if (botaoCriado != null)
                botaoCriado.gameObject.SetActive(true);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurações] Falha ao atualizar o botão do lobby: " + ex);
        }
    }

    private static void CriarBotao(GameStartManager gerente)
    {
        if (gerente?.EditButton == null)
            return;

        if (botaoCriado != null && botaoCriado.gameObject != null)
            UnityEngine.Object.Destroy(botaoCriado.gameObject);

        PassiveButton original = gerente.EditButton;
        botaoCriado = UnityEngine.Object.Instantiate(
            original, original.transform.parent);

        gerenteAssociado = gerente;
        botaoCriado.name = NOME_BOTAO;

        // Mantém o visual do botão nativo, posicionando a cópia logo abaixo
        // do botão Editar para não substituir nem deslocar os controles vanilla.
        botaoCriado.transform.localPosition =
            original.transform.localPosition + new Vector3(0f, -0.72f, 0f);
        botaoCriado.transform.localScale = original.transform.localScale * 0.82f;

        // O clone não deve executar também os listeners originais de Editar.
        // Em vez disso, encaminha o clique ao botão original, que abre a tela
        // de configurações nativa do Among Us.
        botaoCriado.OnClick.RemoveAllListeners();
        botaoCriado.OnClick.AddListener((Action)(() =>
        {
            if (AmongUsClient.Instance == null ||
                !AmongUsClient.Instance.AmHost ||
                AmongUsClient.Instance.IsGameStarted ||
                gerente == null ||
                gerente.EditButton == null)
                return;

            gerente.EditButton.OnClick.Invoke();
        }));

        TMP_Text texto = botaoCriado.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(t => t != null);

        if (texto != null)
            texto.text = "Config. Mod";

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Configurações] Botão 'Config. Mod' adicionado ao lobby.");
    }
}

using System;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Cria um botão Unity independente no lobby. Nenhum componente visual do
/// botão Editar é clonado ou reutilizado; ele serve somente como referência
/// de posição e como ação para abrir a tela nativa de configurações.
/// </summary>
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
internal static class YouTubeVoteConfiguracoesLobbyButtonPatch
{
    private const string NOME_BOTAO = "YouTubeVoteBridge_ConfigButton";
    private static GameObject objetoBotao;
    private static PassiveButton botao;
    private static GameStartManager gerenteAssociado;
    private static Sprite spriteBotao;

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
                if (objetoBotao != null)
                    objetoBotao.SetActive(false);
                return;
            }

            if (objetoBotao == null ||
                gerenteAssociado != __instance)
            {
                CriarBotao(__instance);
            }

            if (objetoBotao != null)
                objetoBotao.SetActive(true);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurações] Falha ao atualizar o botão Unity do lobby: " + ex);
        }
    }

    private static void CriarBotao(GameStartManager gerente)
    {
        if (gerente == null || gerente.EditButton == null)
            return;

        if (objetoBotao != null)
            UnityEngine.Object.Destroy(objetoBotao);

        PassiveButton referencia = gerente.EditButton;
        Transform pai = referencia.transform.parent;

        // Cria um GameObject novo, sem Instantiate/clone de qualquer botão
        // existente no jogo.
        objetoBotao = new GameObject(NOME_BOTAO);
        objetoBotao.layer = referencia.gameObject.layer;
        objetoBotao.transform.SetParent(pai, false);
        objetoBotao.transform.localPosition =
            referencia.transform.localPosition + new Vector3(0f, -0.78f, 0f);
        objetoBotao.transform.localRotation = Quaternion.identity;
        objetoBotao.transform.localScale = referencia.transform.localScale * 0.85f;

        SpriteRenderer renderer = objetoBotao.AddComponent<SpriteRenderer>();
        renderer.sprite = ObterSpriteBotao();
        renderer.color = new Color(0.12f, 0.34f, 0.55f, 1f);

        SpriteRenderer rendererReferencia =
            referencia.GetComponent<SpriteRenderer>();
        if (rendererReferencia != null)
        {
            renderer.sortingLayerID = rendererReferencia.sortingLayerID;
            renderer.sortingOrder = rendererReferencia.sortingOrder + 2;
        }

        BoxCollider2D colisor = objetoBotao.AddComponent<BoxCollider2D>();
        colisor.isTrigger = true;
        colisor.size = new Vector2(2.5f, 0.72f);

        botao = objetoBotao.AddComponent<PassiveButton>();
        botao.OnClick.AddListener((Action)(() => AbrirConfiguracoes(gerente)));

        CriarTexto(objetoBotao.transform, renderer.sortingOrder + 1);

        gerenteAssociado = gerente;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Configurações] Botão Unity independente criado no lobby.");
    }

    private static void CriarTexto(Transform pai, int ordem)
    {
        GameObject objetoTexto = new GameObject("Texto");
        objetoTexto.layer = pai.gameObject.layer;
        objetoTexto.transform.SetParent(pai, false);
        objetoTexto.transform.localPosition = new Vector3(0f, 0f, -0.05f);
        objetoTexto.transform.localRotation = Quaternion.identity;
        objetoTexto.transform.localScale = Vector3.one;

        TextMeshPro texto = objetoTexto.AddComponent<TextMeshPro>();
        texto.text = "CONFIG. MOD";
        texto.alignment = TextAlignmentOptions.Center;
        texto.fontSize = 2.15f;
        texto.fontStyle = FontStyles.Bold;
        texto.color = Color.white;
        texto.enableWordWrapping = false;
        texto.rectTransform.sizeDelta = new Vector2(2.5f, 0.72f);

        if (TMP_Settings.defaultFontAsset != null)
            texto.font = TMP_Settings.defaultFontAsset;

        MeshRenderer mesh = objetoTexto.GetComponent<MeshRenderer>();
        if (mesh != null)
        {
            mesh.sortingLayerID = pai.GetComponent<SpriteRenderer>().sortingLayerID;
            mesh.sortingOrder = ordem;
        }
    }

    private static Sprite ObterSpriteBotao()
    {
        if (spriteBotao != null)
            return spriteBotao;

        const int largura = 128;
        const int altura = 48;
        const int raio = 8;

        Texture2D textura = new Texture2D(
            largura, altura, TextureFormat.RGBA32, false);
        textura.name = "YouTubeVoteBridge_ConfigButton_Texture";
        textura.filterMode = FilterMode.Bilinear;
        textura.wrapMode = TextureWrapMode.Clamp;

        Color[] pixels = new Color[largura * altura];
        Color preenchimento = Color.white;

        for (int y = 0; y < altura; y++)
        {
            for (int x = 0; x < largura; x++)
            {
                int cx = x < raio ? raio : (x >= largura - raio ? largura - raio - 1 : x);
                int cy = y < raio ? raio : (y >= altura - raio ? altura - raio - 1 : y);
                int dx = x - cx;
                int dy = y - cy;
                bool dentro = dx * dx + dy * dy <= raio * raio;
                pixels[y * largura + x] = dentro ? preenchimento : Color.clear;
            }
        }

        textura.SetPixels(pixels);
        textura.Apply(false, true);

        spriteBotao = Sprite.Create(
            textura,
            new Rect(0f, 0f, largura, altura),
            new Vector2(0.5f, 0.5f),
            64f);

        spriteBotao.name = "YouTubeVoteBridge_ConfigButton_Sprite";
        return spriteBotao;
    }

    private static void AbrirConfiguracoes(GameStartManager gerente)
    {
        if (AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            AmongUsClient.Instance.IsGameStarted ||
            gerente == null ||
            gerente.EditButton == null)
            return;

        // Usa somente a ação do botão nativo para abrir a tela de opções.
        // O botão que o jogador clica é o GameObject Unity criado acima.
        gerente.EditButton.OnClick.Invoke();
    }
}

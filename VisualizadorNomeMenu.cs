using System;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using UnityEngine.UI;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Visualizador TextMeshPro local, com botão próprio no menu principal.
/// Não usa F8 nem janela Win32.
/// </summary>
internal sealed class VisualizadorNomeMenu : MonoBehaviour
{
    public VisualizadorNomeMenu(IntPtr ptr) : base(ptr) { }

    private static VisualizadorNomeMenu instancia;
    private static bool registrado;
    private static bool painelAberto;
    private static Canvas canvasPreview;
    private static TMP_Text textoPreview;
    private static string textoEditor = "<color=#ff4444>Teste</color> <b>TextMeshPro</b>";
    private Rect painel = new Rect(0, 0, 500, 390);

    public static void Inicializar()
    {
        if (!registrado)
        {
            ClassInjector.RegisterTypeInIl2Cpp<VisualizadorNomeMenu>();
            registrado = true;
        }

        if (instancia == null)
        {
            var go = new GameObject("YouTubeVoteBridge_TextMeshProPreview");
            UnityEngine.Object.DontDestroyOnLoad(go);
            instancia = go.AddComponent<VisualizadorNomeMenu>();
        }
    }

    private static void CriarPreviewTMP(TMP_Text labelModelo)
    {
        if (canvasPreview != null && textoPreview != null) return;

        var canvasObj = new GameObject("YouTubeVoteBridge_TMPPreviewCanvas");
        UnityEngine.Object.DontDestroyOnLoad(canvasObj);
        canvasPreview = canvasObj.AddComponent<Canvas>();
        canvasPreview.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasPreview.sortingOrder = 32000;
        canvasObj.AddComponent<CanvasScaler>();
        canvasObj.AddComponent<GraphicRaycaster>();

        var previewObj = new GameObject(
            "TextMeshProPreview",
            Il2CppInterop.Runtime.Il2CppType.Of<RectTransform>(),
            Il2CppInterop.Runtime.Il2CppType.Of<TextMeshProUGUI>()
        );
        previewObj.transform.SetParent(canvasObj.transform, false);
        var rect = previewObj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, -115f);
        rect.sizeDelta = new Vector2(1000f, 180f);
        textoPreview = previewObj.GetComponent<TMP_Text>();
        textoPreview.font = TMPro.TMP_Settings.defaultFontAsset;
        textoPreview.enableWordWrapping = true;
        textoPreview.richText = true;
        textoPreview.alignment = TextAlignmentOptions.Center;
        textoPreview.fontSize = 32f;
        textoPreview.gameObject.SetActive(true);
    }

    private void OnGUI()
    {
        if (PlayerControl.LocalPlayer != null)
            return;

        try
        {
            if (!painelAberto)
            {
                float buttonW = 300f;
                float buttonH = 42f;
                float buttonX = (Screen.width - buttonW) / 2f;
                float buttonY = Screen.height * 0.5f + 115f;
                if (GUI.Button(new Rect(buttonX, buttonY, buttonW, buttonH), "Visualizador TextMeshPro"))
                {
                    painelAberto = true;
                    CriarPreviewTMP(null);
                    if (textoPreview != null)
                    {
                        textoPreview.text = textoEditor ?? string.Empty;
                        textoPreview.gameObject.SetActive(true);
                    }
                }
                return;
            }

            float w = 500f;
            float h = 390f;
            painel.x = (Screen.width - w) / 2f;
            painel.y = (Screen.height - h) / 2f;

            GUI.depth = -100;
            GUI.Box(painel, "YouTubeVoteBridge — Visualizador TextMeshPro");
            GUILayout.BeginArea(new Rect(painel.x + 14, painel.y + 35, w - 28, h - 48));

            GUILayout.Label("Digite ou cole texto com tags TextMeshPro:");
            textoEditor = GUILayout.TextArea(textoEditor ?? string.Empty, GUILayout.Height(210));

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Atualizar prévia", GUILayout.Height(32)))
            {
                if (textoPreview != null)
                    textoPreview.text = textoEditor ?? string.Empty;
            }
            if (GUILayout.Button("Limpar", GUILayout.Height(32)))
            {
                textoEditor = string.Empty;
                if (textoPreview != null) textoPreview.text = string.Empty;
            }
            if (GUILayout.Button("Fechar", GUILayout.Height(32)))
            {
                painelAberto = false;
                if (textoPreview != null) textoPreview.gameObject.SetActive(false);
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            GUILayout.Label("A prévia TextMeshPro aparece no centro da tela.");
            GUILayout.Label("Esta prévia é local e não altera o nome do jogador.");
            GUILayout.EndArea();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Visualizador TMP] Erro no painel: " + ex);
        }
    }
}

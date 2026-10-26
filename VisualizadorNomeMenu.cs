using System;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Visualizador TextMeshPro integrado ao menu principal, usando um botão nativo
/// clonado do quitButton, seguindo o padrão de MainMenuManagerPatch do EHR.
/// Não usa F8 nem janela Win32.
/// </summary>
internal sealed class VisualizadorNomeMenu : MonoBehaviour
{
    public VisualizadorNomeMenu(IntPtr ptr) : base(ptr) { }

    private static VisualizadorNomeMenu instancia;
    private static bool registrado;
    private static PassiveButton botaoMenu;
    private static bool painelAberto;
    private static Canvas canvasPreview;
    private static TMP_Text textoPreview;
    private static string textoEditor = "<color=#ff4444>Teste</color> <b>TextMeshPro</b>";
    private static Vector2 scroll;
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

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    public static void AoAbrirMenuPrincipal(MainMenuManager __instance)
    {
        try
        {
            if (__instance == null || __instance.quitButton == null)
                return;

            if (botaoMenu != null)
                UnityEngine.Object.Destroy(botaoMenu.gameObject);

            // EHR usa um botão existente como modelo para preservar aparência,
            // collider e comportamento de clique do menu de Among Us.
            botaoMenu = UnityEngine.Object.Instantiate(
                __instance.quitButton, __instance.quitButton.transform.parent);
            botaoMenu.name = "YouTubeVoteBridge_TextMeshProButton";
            UnityEngine.Object.Destroy(botaoMenu.GetComponent<AspectPosition>());
            botaoMenu.transform.localPosition =
                __instance.quitButton.transform.localPosition + new Vector3(0f, 0.72f, 0f);
            botaoMenu.transform.localScale = __instance.quitButton.transform.localScale;
            botaoMenu.OnClick = new UnityEngine.Events.UnityEvent();
            botaoMenu.OnClick.AddListener((Action)(() =>
            {
                painelAberto = !painelAberto;
                if (painelAberto)
                {
                    CriarPreviewTMP(labelModelo: botaoMenu.GetComponentInChildren<TMP_Text>(true));
                    if (textoPreview != null)
                    {
                        textoPreview.text = textoEditor ?? string.Empty;
                        textoPreview.gameObject.SetActive(true);
                    }
                }
                else if (textoPreview != null)
                    textoPreview.gameObject.SetActive(false);
            }));

            TMP_Text label = botaoMenu.GetComponentInChildren<TMP_Text>(true);
            if (label != null)
            {
                label.DestroyTranslator();
                label.text = "Visualizador TextMeshPro";
                label.enableWordWrapping = false;
                label.fontSize = 2.1f;
                label.fontSizeMin = 1.6f;
                label.fontSizeMax = 2.1f;
            }

            botaoMenu.gameObject.SetActive(true);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Visualizador TMP] Botão criado no menu principal usando o botão nativo do jogo.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Visualizador TMP] Não foi possível criar o botão do menu: " + ex);
        }
    }

    private static void CriarPreviewTMP(TMP_Text labelModelo)
    {
        if (canvasPreview != null && textoPreview != null) return;
        if (labelModelo == null) return;

        var canvasObj = new GameObject("YouTubeVoteBridge_TMPPreviewCanvas");
        UnityEngine.Object.DontDestroyOnLoad(canvasObj);
        canvasPreview = canvasObj.AddComponent<Canvas>();
        canvasPreview.renderMode = RenderMode.ScreenSpaceOverlay;
        canvasPreview.sortingOrder = 32000;
        canvasObj.AddComponent<UnityEngine.UI.CanvasScaler>();
        canvasObj.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        var previewObj = UnityEngine.Object.Instantiate(labelModelo.gameObject, canvasObj.transform);
        previewObj.name = "TextMeshProPreview";
        var rect = previewObj.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, -115f);
        rect.sizeDelta = new Vector2(1000f, 180f);
        textoPreview = previewObj.GetComponent<TMP_Text>();
        textoPreview.enableWordWrapping = true;
        textoPreview.richText = true;
        textoPreview.alignment = TextAlignmentOptions.Center;
        textoPreview.fontSize = 32f;
        textoPreview.gameObject.SetActive(true);
    }

    private void OnGUI()
    {
        if (!painelAberto || PlayerControl.LocalPlayer != null)
            return;

        try
        {
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

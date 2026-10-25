using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime.Injection;

namespace YouTubeVoteBridge;

/// <summary>
/// Editor de texto F8. Permite colar tags do TMP e visualizar o resultado
/// dentro do jogo, usando TextMeshProUGUI para interpretar a formatação.
/// </summary>
internal sealed class VisualizadorNomeMenu : MonoBehaviour
{
    public VisualizadorNomeMenu(IntPtr ptr) : base(ptr) { }

    private static VisualizadorNomeMenu instancia;
    private static bool registrado;
    private static bool menuAberto;
    private static bool previewVisivel;
    private static string textoEntrada = "";
    private static string textoAplicado = "";

    private const int ID_JANELA = 57341;
    private Rect janela = new Rect(80, 70, 650, 570);
    private Vector2 scrollTexto;
    private GUIStyle estiloTitulo;
    private GUIStyle estiloAjuda;
    private GUIStyle estiloBotao;
    private GUIStyle estiloCampo;
    private bool estilosProntos;

    private GameObject previewCanvasObject;
    private GameObject previewPanelObject;
    private TextMeshProUGUI previewText;

    public static void Inicializar()
    {
        if (!registrado)
        {
            ClassInjector.RegisterTypeInIl2Cpp<VisualizadorNomeMenu>();
            registrado = true;
        }

        if (instancia != null) return;

        var objeto = new GameObject("YouTubeVoteBridge_VisualizadorNome");
        UnityEngine.Object.DontDestroyOnLoad(objeto);
        instancia = objeto.AddComponent<VisualizadorNomeMenu>();
        instancia.CriarPreview();
    }

    private void AlternarMenu()
    {
        menuAberto = !menuAberto;
        if (!menuAberto && previewVisivel)
            DefinirPreviewVisivel(true);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Visualizador F8] Menu " + (menuAberto ? "aberto" : "fechado") + ".");
    }

    public static void AtualizarPreview()
    {
        if (instancia == null || instancia.previewText == null) return;

        if (instancia.previewText.text != textoAplicado)
            instancia.previewText.text = textoAplicado;
        if (instancia.previewCanvasObject != null)
            instancia.previewCanvasObject.SetActive(previewVisivel);
    }

    private void Update()
    {
        // Ler a tecla no próprio MonoBehaviour evita depender do Harmony Postfix
        // de AmongUsClient.Update para abrir a interface.
        if (Input.GetKeyDown(KeyCode.F8))
            AlternarMenu();

        if (previewCanvasObject != null && previewCanvasObject.activeSelf != previewVisivel)
            previewCanvasObject.SetActive(previewVisivel);
    }

    private void OnGUI()
    {
        if (!menuAberto) return;

        CriarEstilosSeNecessario();
        janela = GUI.Window(
            ID_JANELA,
            janela,
            (GUI.WindowFunction)DesenharJanela,
            "YouTubeChatBridge — Visualizador de Nome (F8)"
        );
    }

    private void DesenharJanela(int id)
    {
        GUILayout.BeginVertical();
        GUILayout.Label("Cole abaixo o texto gerado pelo W Paint ou qualquer texto com tags TextMeshPro.", estiloAjuda);
        GUILayout.Space(8);

        GUILayout.Label("Texto com tags TMP:", estiloTitulo);
        scrollTexto = GUILayout.BeginScrollView(scrollTexto, GUILayout.Height(300));
        textoEntrada = GUILayout.TextArea(textoEntrada, estiloCampo, GUILayout.ExpandHeight(true));
        GUILayout.EndScrollView();

        GUILayout.Space(8);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Visualizar no jogo", estiloBotao, GUILayout.Height(34)))
        {
            textoAplicado = textoEntrada ?? "";
            previewVisivel = true;
            AtualizarPreview();
        }
        if (GUILayout.Button("Ocultar prévia", estiloBotao, GUILayout.Height(34)))
        {
            previewVisivel = false;
            AtualizarPreview();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Limpar texto", estiloBotao, GUILayout.Height(28)))
        {
            textoEntrada = "";
            scrollTexto = Vector2.zero;
        }
        if (GUILayout.Button("Fechar (F8)", estiloBotao, GUILayout.Height(28)))
        {
            menuAberto = false;
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(5);
        GUILayout.Label(
            "A prévia é renderizada no jogo com TextMeshPro. O texto não altera o nome de nenhum jogador.",
            estiloAjuda
        );
        GUILayout.EndVertical();

        GUI.DragWindow(new Rect(0, 0, 10000, 26));
    }

    private void CriarEstilosSeNecessario()
    {
        if (estilosProntos) return;

        estiloTitulo = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.white },
            wordWrap = true
        };

        estiloAjuda = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12,
            normal = { textColor = new Color(0.78f, 0.82f, 0.9f) },
            wordWrap = true
        };

        estiloBotao = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            wordWrap = true
        };

        estiloCampo = new GUIStyle(GUI.skin.textArea)
        {
            fontSize = 12,
            wordWrap = false,
            alignment = TextAnchor.UpperLeft
        };

        estilosProntos = true;
    }

    private void CriarPreview()
    {
        try
        {
            previewCanvasObject = new GameObject("YouTubeVoteBridge_NomePreviewCanvas");
            UnityEngine.Object.DontDestroyOnLoad(previewCanvasObject);

            var canvas = previewCanvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;

            var scaler = previewCanvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            previewCanvasObject.AddComponent<GraphicRaycaster>();

            previewPanelObject = new GameObject("PainelPreview");
            previewPanelObject.transform.SetParent(previewCanvasObject.transform, false);

            var panelRect = previewPanelObject.AddComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(1100, 700);

            var background = previewPanelObject.AddComponent<Image>();
            background.color = new Color(0.035f, 0.045f, 0.065f, 0.94f);
            background.raycastTarget = false;

            var textObject = new GameObject("TextoTMP");
            textObject.transform.SetParent(previewPanelObject.transform, false);

            var textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0.03f, 0.04f);
            textRect.anchorMax = new Vector2(0.97f, 0.96f);
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;

            previewText = textObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                previewText.font = TMP_Settings.defaultFontAsset;

            previewText.text = "";
            previewText.richText = true;
            previewText.fontSize = 24;
            previewText.alignment = TextAlignmentOptions.Center;
            previewText.enableWordWrapping = false;
            previewText.overflowMode = TextOverflowModes.Overflow;
            previewText.color = Color.white;
            previewText.raycastTarget = false;

            previewCanvasObject.SetActive(false);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Visualizador F8] Não foi possível criar a prévia TextMeshPro: " + ex);
        }
    }

    private void DefinirPreviewVisivel(bool visivel)
    {
        previewVisivel = visivel;
        if (previewCanvasObject != null)
            previewCanvasObject.SetActive(visivel);
    }
}

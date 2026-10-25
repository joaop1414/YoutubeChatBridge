using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Il2CppInterop.Runtime.Injection;

namespace YouTubeVoteBridge;

/// <summary>
/// Editor de texto F8. Permite colar tags do TMP e visualizar o resultado
/// como UI 2D posicionada acima do jogador local.
/// </summary>
internal sealed class VisualizadorNomeMenu : MonoBehaviour
{
    public VisualizadorNomeMenu(IntPtr ptr) : base(ptr) { }

    private static VisualizadorNomeMenu instancia;
    private static bool registrado;
    private const string CLASSE_JANELA = "YouTubeVoteBridge_VisualizadorNomeWindow";
    private const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, WM_APP_TOGGLE = 0x8001;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_VSCROLL = 0x00200000;
    private const uint ES_MULTILINE = 0x0004, ES_AUTOVSCROLL = 0x0040, ES_WANTRETURN = 0x1000;
    private const int SW_HIDE = 0, SW_RESTORE = 9, WM_SETFONT = 0x0030;
    private const int ID_CAMPO = 1001, ID_PREVIEW = 1002, ID_OCULTAR = 1003, ID_LIMPAR = 1004;
    private static IntPtr janelaNativa, campoNativo, fonteNativa;
    private static Thread threadJanela;
    private static readonly object travaTexto = new();
    private static string textoPendente = "";
    private static bool aplicarTextoPendente;
    private static readonly WndProcDelegate procedimentoJanela = ProcedimentoJanela;
    private static bool menuAberto;
    private static bool f8FisicoAnterior;
    private static bool previewVisivel;
    private static bool? visibilidadePendente;
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
    private GameObject previewTextoObject;
    private Canvas previewCanvas;
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
        threadJanela = new Thread(CriarJanelaNativa) { IsBackground = true, Name = "YouTubeVoteBridge Visualizador F8" };
        threadJanela.Start();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Visualizador F8] Janela nativa inicializada (padrão F6/F7).");
    }

    public static void VerificarTecla()
    {
        // Unity input (mesmo caminho do F6/F7) + leitura física do Windows.
        bool f8Fisico = (GetAsyncKeyState(0x77) & 0x8000) != 0; // VK_F8
        bool pressionouF8 = Input.GetKeyDown(KeyCode.F8) || (f8Fisico && !f8FisicoAnterior);
        f8FisicoAnterior = f8Fisico;
        if (!pressionouF8) return;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Visualizador F8] Tecla F8 detectada.");
        IntPtr hwnd = janelaNativa;
        if (hwnd == IntPtr.Zero)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Visualizador F8] F8 detectado, mas a janela ainda não foi criada. Confira os erros de inicialização acima.");
            return;
        }

        bool enviado = PostMessageW(hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
        if (enviado)
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Visualizador F8] Pedido para abrir/ocultar janela enviado.");
        else
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Visualizador F8] PostMessage falhou. Código Win32: " + Marshal.GetLastWin32Error());
    }

    public static void AtualizarPreview()
    {
        if (instancia == null) return;

        string novoTexto = null;
        bool? novaVisibilidade;
        lock (travaTexto)
        {
            if (aplicarTextoPendente)
            {
                novoTexto = textoPendente;
                aplicarTextoPendente = false;
            }
            novaVisibilidade = visibilidadePendente;
            visibilidadePendente = null;
        }

        if (novoTexto != null)
        {
            textoAplicado = novoTexto;
            previewVisivel = !string.IsNullOrWhiteSpace(novoTexto);
        }
        if (novaVisibilidade.HasValue)
            previewVisivel = novaVisibilidade.Value;

        if (instancia.previewText != null && instancia.previewText.text != textoAplicado)
            instancia.previewText.text = textoAplicado;
        if (instancia.previewTextoObject != null && instancia.previewTextoObject.activeSelf != previewVisivel)
            instancia.previewTextoObject.SetActive(previewVisivel);

        // A prévia acompanha o jogador local e fica acima da cabeça dele.
        try
        {
            PlayerControl jogador = PlayerControl.LocalPlayer;
            if (jogador != null && instancia.previewTextoObject != null)
            {
                Camera camera = Camera.main;
                if (camera != null)
                {
                    Vector3 posicaoTela = camera.WorldToScreenPoint(
                        jogador.transform.position + Vector3.up * 0.85f);
                    bool naFrenteDaCamera = posicaoTela.z > 0f;
                    if (instancia.previewTextoObject.activeSelf != (previewVisivel && naFrenteDaCamera))
                        instancia.previewTextoObject.SetActive(previewVisivel && naFrenteDaCamera);

                    if (naFrenteDaCamera)
                        instancia.previewTextoObject.transform.position =
                            new Vector3(posicaoTela.x, posicaoTela.y, 0f);
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug("[Visualizador F8] Não foi possível posicionar a prévia: " + ex.Message);
        }
    }

    private void CriarPreview()
    {
        try
        {
            // UI 2D em Screen Space Overlay; convertemos a posição do jogador para a tela.
            previewCanvasObject = new GameObject("YouTubeVoteBridge_NomePreviewCanvas");
            UnityEngine.Object.DontDestroyOnLoad(previewCanvasObject);
            previewCanvas = previewCanvasObject.AddComponent<Canvas>();
            previewCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            previewCanvas.sortingOrder = 32767;

            previewTextoObject = new GameObject("YouTubeVoteBridge_NomePreviewScreen");
            previewTextoObject.transform.SetParent(previewCanvasObject.transform, false);
            previewText = previewTextoObject.AddComponent<TextMeshProUGUI>();
            if (TMP_Settings.defaultFontAsset != null)
                previewText.font = TMP_Settings.defaultFontAsset;

            RectTransform rect = previewTextoObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(900f, 120f);
            rect.position = Vector3.zero;

            previewText.text = "";
            previewText.richText = true;
            previewText.fontSize = 36f;
            previewText.alignment = TextAlignmentOptions.Center;
            previewText.enableWordWrapping = false;
            previewText.overflowMode = TextOverflowModes.Overflow;
            previewText.color = Color.white;
            previewText.outlineWidth = 0.15f;
            previewText.outlineColor = Color.black;
            previewTextoObject.SetActive(false);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Visualizador F8] Prévia UI 2D criada; será posicionada na tela acima do jogador local.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Visualizador F8] Não foi possível criar a prévia UI: " + ex);
        }
    }

    private static void CriarJanelaNativa()
    {
        try
        {
            IntPtr mod = GetModuleHandleW(null);
            WNDCLASSEX wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = procedimentoJanela,
                hInstance = mod, hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)), hbrBackground = new IntPtr(6), lpszClassName = CLASSE_JANELA };
            if (RegisterClassExW(ref wc) == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Visualizador F8] RegisterClassEx falhou: " + Marshal.GetLastWin32Error());
                return;
            }
            IntPtr hwnd = CreateWindowExW(0, CLASSE_JANELA, "YouTubeChatBridge - Visualizador de Nome (F8)",
                WS_OVERLAPPEDWINDOW, unchecked((int)0x80000000), unchecked((int)0x80000000),
                760, 590, IntPtr.Zero, IntPtr.Zero, mod, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Visualizador F8] CreateWindowEx falhou: " + Marshal.GetLastWin32Error());
                return;
            }
            janelaNativa = hwnd;
            ShowWindow(hwnd, SW_HIDE);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Visualizador F8] Janela do editor criada com sucesso (HWND=" + hwnd + ").");
            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Visualizador F8] Janela nativa: " + ex); }
    }

    private static IntPtr ProcedimentoJanela(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp)
    {
        try
        {
            switch (msg)
            {
                case 0x0001:
                    fonteNativa = CreateFontW(-16, 0, 0, 0, 400, false, false, false, 1, 0, 0, 5, 0, "Segoe UI");
                    CriarControle("STATIC", "Cole o texto do W Paint ou texto com tags TextMeshPro:", WS_CHILD | WS_VISIBLE, 18, 15, 700, 26, hwnd, IntPtr.Zero);
                    campoNativo = CriarControle("EDIT", "", WS_CHILD | WS_VISIBLE | WS_VSCROLL | ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN,
                        18, 48, 708, 390, hwnd, new IntPtr(ID_CAMPO));
                    CriarControle("BUTTON", "Visualizar no jogo", WS_CHILD | WS_VISIBLE, 18, 455, 170, 36, hwnd, new IntPtr(ID_PREVIEW));
                    CriarControle("BUTTON", "Ocultar prévia", WS_CHILD | WS_VISIBLE, 198, 455, 150, 36, hwnd, new IntPtr(ID_OCULTAR));
                    CriarControle("BUTTON", "Limpar texto", WS_CHILD | WS_VISIBLE, 358, 455, 130, 36, hwnd, new IntPtr(ID_LIMPAR));
                    CriarControle("STATIC", "A prévia aparece no jogo e não altera nomes de jogadores.", WS_CHILD | WS_VISIBLE, 18, 505, 700, 28, hwnd, IntPtr.Zero);
                    return IntPtr.Zero;
                case WM_APP_TOGGLE:
                    if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_HIDE);
                    else { ShowWindow(hwnd, SW_RESTORE); SetForegroundWindow(hwnd); }
                    return IntPtr.Zero;
                case WM_COMMAND:
                    int id = wp.ToInt32() & 0xFFFF;
                    if (id == ID_PREVIEW)
                    {
                        int n = Math.Max(0, GetWindowTextLengthW(campoNativo));
                        StringBuilder sb = new StringBuilder(n + 1);
                        GetWindowTextW(campoNativo, sb, sb.Capacity);
                        lock (travaTexto)
                        {
                            textoPendente = sb.ToString();
                            aplicarTextoPendente = true;
                            visibilidadePendente = true;
                        }
                        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Visualizador F8] Botão Visualizar acionado; texto enviado para a prévia.");
                    }
                    else if (id == ID_OCULTAR)
                    {
                        lock (travaTexto) { visibilidadePendente = false; }
                        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Visualizador F8] Botão Ocultar prévia acionado.");
                    }
                    else if (id == ID_LIMPAR)
                    {
                        SetWindowTextW(campoNativo, "");
                        lock (travaTexto)
                        {
                            textoPendente = "";
                            aplicarTextoPendente = true;
                            visibilidadePendente = false;
                        }
                        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Visualizador F8] Botão Limpar texto acionado.");
                    }
                    else
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[Visualizador F8] WM_COMMAND recebido com ID inesperado: " + id);
                    }
                    return IntPtr.Zero;
                case WM_CLOSE: ShowWindow(hwnd, SW_HIDE); return IntPtr.Zero;
            }
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Visualizador F8] WndProc: " + ex); }
        return DefWindowProcW(hwnd, msg, wp, lp);
    }

    private static IntPtr CriarControle(string classe, string texto, uint estilo, int x, int y, int w, int h, IntPtr pai, IntPtr id)
    {
        IntPtr ctl = CreateWindowExW(0, classe, texto, estilo, x, y, w, h, pai, id, GetModuleHandleW(null), IntPtr.Zero);
        if (ctl != IntPtr.Zero && fonteNativa != IntPtr.Zero) SendMessageW(ctl, WM_SETFONT, fonteNativa, new IntPtr(1));
        return ctl;
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style; public WndProcDelegate lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG
    {
        public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; public uint lPrivate;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", SetLastError = true)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)] private static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW", SetLastError = true)] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr cursor);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW", SetLastError = true)] private static extern bool SetWindowTextW(IntPtr hwnd, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW", SetLastError = true)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)] private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "TranslateMessage")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW", SetLastError = true)] private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, bool italic, bool underline, bool strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);

}

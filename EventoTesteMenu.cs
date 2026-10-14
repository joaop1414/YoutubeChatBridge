using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTesteMenu : MonoBehaviour
{
    private const string CLASSE_JANELA = "YouTubeVoteBridge_EventoTesteWindow";
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_APP_TOGGLE = 0x8001;
    private const uint WM_APP_STATUS = 0x8002;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VSCROLL = 0x00200000;
    private const uint WM_VSCROLL = 0x0115;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint SW_SCROLLCHILDREN = 0x0001;
    private const uint SW_INVALIDATE = 0x0002;
    private const int SB_VERT = 1;
    private const uint SIF_RANGE = 0x0001;
    private const uint SIF_PAGE = 0x0002;
    private const uint SIF_POS = 0x0004;
    private const uint SIF_ALL = SIF_RANGE | SIF_PAGE | SIF_POS | 0x0010;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint SS_LEFT = 0x00000000;
    private const uint BS_PUSHBUTTON = 0x00000000;
    private const uint SS_ETCHEDHORZ = 0x00000010;
    private const uint WM_SETFONT = 0x0030;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int ID_BOTAO_BASE = 1000;
    private const int ALTURA_LINHA = 38;
    private const int LARGURA_JANELA = 620;
    private const int ALTURA_JANELA = 620;
    private static IntPtr fonteNormal;
    private static IntPtr fonteTitulo;

    private static bool registrado;
    private static bool janelaIniciada;
    private static IntPtr janela;
    private static IntPtr textoStatus;
    private static string mensagem = "Pressione F6 para abrir/fechar a janela.";
    private static IReadOnlyList<EventoVotacao> eventos = Array.Empty<EventoVotacao>();
    private static readonly ConcurrentQueue<int> filaExecucao = new();
    private static readonly WndProcDelegate procedimentoJanela = ProcedimentoJanela;
    private static Thread threadJanela;

    public EventoTesteMenu(IntPtr ptr) : base(ptr) { }

    public static void Inicializar()
    {
        if (!registrado)
        {
            ClassInjector.RegisterTypeInIl2Cpp<EventoTesteMenu>();
            registrado = true;
        }

        GameObject objeto = new GameObject("YouTubeVoteBridge_EventoTesteMenu");
        UnityEngine.Object.DontDestroyOnLoad(objeto);
        objeto.AddComponent<EventoTesteMenu>();

        eventos = RegistroEventos.ObterTodos();
        threadJanela = new Thread(CriarJanelaNativa)
        {
            IsBackground = true,
            Name = "YouTubeVoteBridge janela de testes"
        };
        threadJanela.Start();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Menu de teste] Inicializado. Pressione F6 para abrir a janela nativa.");
    }

    public static void VerificarTecla()
    {
        if (!Input.GetKeyDown(KeyCode.F6))
            return;

        IntPtr hwnd = janela;
        if (hwnd == IntPtr.Zero)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Menu de teste] F6 detectado, mas a janela nativa ainda não está pronta.");
            return;
        }

        PostMessageW(hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Menu de teste] F6 detectado; alternando a janela nativa.");
    }

    // Chamado pelo Harmony no ciclo de atualização do jogo, garantindo
    // que os eventos sejam executados na thread principal do Unity.
    public static void ProcessarFilaExecucao()
    {
        while (filaExecucao.TryDequeue(out int indice))
        {
            if (indice < 0 || indice >= eventos.Count)
                continue;

            try
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Menu de teste] Processando clique do evento: " + eventos[indice].Nome);
                ExecutarEvento(eventos[indice]);
            }
            catch (Exception ex)
            {
                MostrarMensagem("Erro inesperado: " + ex.Message);
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[Menu de teste] Erro ao processar clique: " + ex);
            }
        }
    }

    private static void CriarJanelaNativa()
    {
        try
        {
            IntPtr instancia = GetModuleHandleW(null);
            WNDCLASSEX wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = procedimentoJanela,
                hInstance = instancia,
                hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                hbrBackground = new IntPtr(6), // COLOR_WINDOW + 1
                lpszClassName = CLASSE_JANELA
            };

            ushort classeAtom = RegisterClassExW(ref wc);
            if (classeAtom == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[Menu de teste] RegisterClassEx falhou. Código Win32: " +
                    Marshal.GetLastWin32Error());
                return;
            }

            IntPtr hwnd = CreateWindowExW(
                0,
                CLASSE_JANELA,
                "YouTubeChatBridge - Teste de eventos",
                WS_OVERLAPPEDWINDOW | WS_VSCROLL,
                unchecked((int)0x80000000), // CW_USEDEFAULT
                unchecked((int)0x80000000),
                LARGURA_JANELA,
                ALTURA_JANELA,
                IntPtr.Zero,
                IntPtr.Zero,
                instancia,
                IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[Menu de teste] CreateWindowEx falhou. Código Win32: " +
                    Marshal.GetLastWin32Error());
                return;
            }

            janela = hwnd;
            janelaIniciada = true;
            ShowWindow(hwnd, SW_HIDE);

            MSG msg;
            while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Menu de teste] Erro ao criar janela nativa: " + ex);
        }
    }

    private static IntPtr ProcedimentoJanela(
        IntPtr hwnd, uint mensagemJanela, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (mensagemJanela)
            {
                case 0x0001: // WM_CREATE
                    CriarFontes();
                    CriarControles(hwnd);
                    return IntPtr.Zero;

                case WM_APP_TOGGLE:
                    if (IsWindowVisible(hwnd))
                    {
                        ShowWindow(hwnd, SW_HIDE);
                    }
                    else
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                    return IntPtr.Zero;

                case WM_APP_STATUS:
                    if (textoStatus != IntPtr.Zero)
                        SetWindowTextW(textoStatus, "STATUS: " + (mensagem ?? ""));
                    return IntPtr.Zero;

                case WM_VSCROLL:
                    ProcessarRolagem(hwnd, wParam.ToInt32() & 0xFFFF, (wParam.ToInt32() >> 16) & 0xFFFF);
                    return IntPtr.Zero;

                case WM_MOUSEWHEEL:
                    int deltaRoda = unchecked((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                    ProcessarRolagem(hwnd, deltaRoda > 0 ? 0 : 1, 0);
                    return IntPtr.Zero;

                case WM_COMMAND:
                    int id = wParam.ToInt32() & 0xFFFF;
                    int indice = id - ID_BOTAO_BASE;
                    if (indice >= 0 && indice < eventos.Count)
                    {
                        filaExecucao.Enqueue(indice);
                        mensagem = "Solicitação recebida: " + eventos[indice].Nome + "...";
                        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                            "[Menu de teste] Clique recebido para: " + eventos[indice].Nome);
                        if (textoStatus != IntPtr.Zero)
                            SetWindowTextW(textoStatus, "STATUS: " + mensagem);
                    }
                    return IntPtr.Zero;

                case WM_CLOSE:
                    ShowWindow(hwnd, SW_HIDE);
                    return IntPtr.Zero;

                case 0x0002: // WM_DESTROY
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Menu de teste] Erro na janela nativa: " + ex);
        }

        return DefWindowProcW(hwnd, mensagemJanela, wParam, lParam);
    }

    private static void CriarControles(IntPtr hwnd)
    {
        IntPtr instancia = GetModuleHandleW(null);

        IntPtr titulo = CriarControle("STATIC",
            "PAINEL DE TESTE DE EVENTOS",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            18, 14, 520, 28, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(titulo, fonteTitulo);

        IntPtr subtitulo = CriarControle("STATIC",
            "Clique em Testar para executar no jogo. É necessário ser o host.",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            20, 44, 535, 25, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(subtitulo, fonteNormal);

        CriarControle("STATIC", "",
            WS_CHILD | WS_VISIBLE | SS_ETCHEDHORZ,
            18, 76, 545, 2, hwnd, IntPtr.Zero, instancia);

        if (eventos.Count == 0)
        {
            IntPtr vazio = CriarControle("STATIC",
                "Nenhum evento foi registrado.",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                22, 94, 520, 30, hwnd, IntPtr.Zero, instancia);
            AplicarFonte(vazio, fonteNormal);
        }

        for (int i = 0; i < eventos.Count; i++)
        {
            int y = 88 + i * ALTURA_LINHA;
            IntPtr nome = CriarControle("STATIC",
                $"{i + 1:00}   {eventos[i]?.Nome ?? "(evento inválido)"}",
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                22, y + 8, 365, 24, hwnd, IntPtr.Zero, instancia);
            AplicarFonte(nome, fonteNormal);

            IntPtr botao = CriarControle("BUTTON", "▶  Testar",
                WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                405, y + 2, 140, 30, hwnd,
                new IntPtr(ID_BOTAO_BASE + i), instancia);
            AplicarFonte(botao, fonteNormal);

            if (i < eventos.Count - 1)
            {
                CriarControle("STATIC", "",
                    WS_CHILD | WS_VISIBLE | SS_ETCHEDHORZ,
                    22, y + ALTURA_LINHA - 1, 520, 2, hwnd, IntPtr.Zero, instancia);
            }
        }

        int statusY = 96 + eventos.Count * ALTURA_LINHA;
        CriarControle("STATIC", "",
            WS_CHILD | WS_VISIBLE | SS_ETCHEDHORZ,
            18, statusY, 545, 2, hwnd, IntPtr.Zero, instancia);

        textoStatus = CriarControle("STATIC", "STATUS: " + (mensagem ?? ""),
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            22, statusY + 12, 520, 44, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(textoStatus, fonteNormal);
        ConfigurarRolagem(hwnd);
    }

    private static void ConfigurarRolagem(IntPtr hwnd)
    {
        int alturaConteudo = 150 + eventos.Count * ALTURA_LINHA + 90;
        SCROLLINFO info = new()
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = SIF_RANGE | SIF_PAGE | SIF_POS,
            nMin = 0,
            nMax = Math.Max(ALTURA_JANELA, alturaConteudo),
            nPage = ALTURA_JANELA - 45,
            nPos = 0
        };
        SetScrollInfo(hwnd, SB_VERT, ref info, true);
    }

    private static void ProcessarRolagem(IntPtr hwnd, int codigo, int posicaoInformada)
    {
        SCROLLINFO info = new()
        {
            cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(),
            fMask = SIF_ALL
        };
        GetScrollInfo(hwnd, SB_VERT, ref info);
        int anterior = info.nPos;
        int maximo = Math.Max(0, info.nMax - (int)info.nPage + 1);

        switch (codigo)
        {
            case 0: info.nPos -= ALTURA_LINHA; break; // SB_LINEUP
            case 1: info.nPos += ALTURA_LINHA; break; // SB_LINEDOWN
            case 2: info.nPos -= (int)info.nPage; break; // SB_PAGEUP
            case 3: info.nPos += (int)info.nPage; break; // SB_PAGEDOWN
            case 4: info.nPos = posicaoInformada; break; // SB_THUMBPOSITION
            case 5: info.nPos = posicaoInformada; break; // SB_THUMBTRACK
            case 6: info.nPos = 0; break; // SB_TOP
            case 7: info.nPos = maximo; break; // SB_BOTTOM
            default: return;
        }

        info.nPos = Math.Max(0, Math.Min(maximo, info.nPos));
        info.fMask = SIF_POS;
        SetScrollInfo(hwnd, SB_VERT, ref info, true);
        int novaPosicao = info.nPos;
        int deslocamento = anterior - novaPosicao;
        if (deslocamento != 0)
        {
            RECT retangulo;
            ScrollWindowEx(hwnd, 0, deslocamento, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, out retangulo, SW_SCROLLCHILDREN | SW_INVALIDATE);
        }
    }

    private static void CriarFontes()
    {
        fonteNormal = CreateFontW(
            18, 0, 0, 0, 400, false, false, false,
            1, 0, 0, 5, 0, "Segoe UI");
        fonteTitulo = CreateFontW(
            25, 0, 0, 0, 700, false, false, false,
            1, 0, 0, 5, 0, "Segoe UI Semibold");
    }

    private static void AplicarFonte(IntPtr controle, IntPtr fonte)
    {
        if (controle != IntPtr.Zero && fonte != IntPtr.Zero)
            SendMessageW(controle, WM_SETFONT, fonte, new IntPtr(1));
    }

    private static IntPtr CriarControle(
        string classe, string texto, uint estilo,
        int x, int y, int largura, int altura,
        IntPtr pai, IntPtr id, IntPtr instancia)
    {
        return CreateWindowExW(
            0, classe, texto, estilo,
            x, y, largura, altura,
            pai, id, instancia, IntPtr.Zero);
    }

    private static void ExecutarEvento(EventoVotacao evento)
    {
        if (evento == null)
            return;

        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null)
        {
            MostrarMensagem("Entre em uma partida antes de executar eventos.");
            return;
        }

        if (!YouTubeVoteBridgePlugin.EhHost())
        {
            MostrarMensagem("Entre como host para testar os eventos.");
            return;
        }

        try
        {
            Type tipo = evento.GetType();
            MethodInfo executar = tipo.GetMethod(
                "Executar",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

            if (executar != null)
            {
                executar.Invoke(evento, null);
                MostrarMensagem("Evento executado: " + evento.Nome);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Menu de teste] Executado: " + evento.Nome);
                return;
            }

            MethodInfo ativar = tipo.GetMethod(
                "Ativar",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

            if (ativar != null)
            {
                ativar.Invoke(null, null);
                MostrarMensagem("Evento ativado: " + evento.Nome);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Menu de teste] Ativado: " + evento.Nome);
                return;
            }

            MostrarMensagem("Este evento ainda não tem efeito implementado.");
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Menu de teste] Sem método Executar/Ativar: " + evento.Nome);
        }
        catch (TargetInvocationException ex)
        {
            Exception causa = ex.InnerException ?? ex;
            MostrarMensagem("Erro no evento: " + causa.Message);
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Menu de teste] Falha em " + evento.Nome + ": " + causa);
        }
        catch (Exception ex)
        {
            MostrarMensagem("Erro no evento: " + ex.Message);
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Menu de teste] Falha em " + evento.Nome + ": " + ex);
        }
    }

    private static void MostrarMensagem(string texto)
    {
        mensagem = texto ?? "";
        IntPtr hwnd = janela;
        if (hwnd != IntPtr.Zero)
            PostMessageW(hwnd, WM_APP_STATUS, IntPtr.Zero, IntPtr.Zero);
    }

    private delegate IntPtr WndProcDelegate(
        IntPtr hwnd, uint mensagemJanela, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SCROLLINFO
    {
        public uint cbSize;
        public uint fMask;
        public int nMin;
        public int nMax;
        public uint nPage;
        public int nPos;
        public int nTrackPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW", SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
        IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "LoadCursorW", SetLastError = true)]
    private static extern IntPtr LoadCursorW(IntPtr hInstance, IntPtr lpCursorName);

    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW", SetLastError = true)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW", SetLastError = true)]
    private static extern IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        bool bItalic, bool bUnderline, bool bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll", EntryPoint = "SetScrollInfo", SetLastError = true)]
    private static extern int SetScrollInfo(IntPtr hwnd, int nBar, ref SCROLLINFO lpsi, bool redraw);

    [DllImport("user32.dll", EntryPoint = "GetScrollInfo", SetLastError = true)]
    private static extern bool GetScrollInfo(IntPtr hwnd, int nBar, ref SCROLLINFO lpsi);

    [DllImport("user32.dll", EntryPoint = "ScrollWindowEx", SetLastError = true)]
    private static extern int ScrollWindowEx(
        IntPtr hwnd, int dx, int dy, IntPtr prcScroll, IntPtr prcClip,
        IntPtr hrgnUpdate, out RECT prcUpdate, uint flags);
}

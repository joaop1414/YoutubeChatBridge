using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Painel nativo de configurações. Usa a mesma abordagem Win32 do EventoTesteMenu,
/// evitando depender do OnGUI em um MonoBehaviour IL2CPP.
/// F9 abre/fecha a janela enquanto o usuário está no lobby como host.
/// </summary>
internal static class ConfiguracoesModLobbyUI
{
    private const string CLASSE_JANELA = "YouTubeVoteBridge_ConfigWindow";
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_APP_TOGGLE = 0x8001;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint SS_LEFT = 0x00000000;
    private const uint BS_PUSHBUTTON = 0x00000000;
    private const uint SS_ETCHEDHORZ = 0x00000010;
    private const uint WM_SETFONT = 0x0030;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int ID_TOGGLE_BASE = 100;
    private const int ID_INT_MINUS_BASE = 200;
    private const int ID_INT_PLUS_BASE = 210;
    private const int LARGURA_JANELA = 650;
    private const int ALTURA_JANELA = 650;

    private static readonly ConcurrentQueue<int> filaAlteracoes = new();
    private static readonly bool[] valoresBooleanos = new bool[6];
    private static readonly int[] valoresInteiros = new int[3];
    private static readonly IntPtr[] botoesBooleanos = new IntPtr[6];
    private static readonly IntPtr[] textosInteiros = new IntPtr[3];
    private static readonly WndProcDelegate procedimentoJanela = ProcedimentoJanela;

    private static bool registrado;
    private static IntPtr janela;
    private static IntPtr fonteNormal;
    private static IntPtr fonteTitulo;
    private static Thread threadJanela;

    private static readonly string[] nomesBooleanos =
    {
        "Sem fim de jogo",
        "Pular fase de votos quando o juiz usa sua habilidade",
        "Multitransmissão",
        "Permitir IDs de outros jogadores",
        "Escolher mapa aleatório ao iniciar (Dleks: 20%)",
        "Permitir que o NPC Assassino use ventilações"
    };

    public static void Inicializar()
    {
        if (registrado)
            return;

        registrado = true;
        CarregarValoresIniciais();

        threadJanela = new Thread(CriarJanelaNativa)
        {
            IsBackground = true,
            Name = "YouTubeVoteBridge configurações"
        };
        threadJanela.Start();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Configurações] Janela nativa iniciada. Pressione F9 no lobby como host.");
    }

    private static void CarregarValoresIniciais()
    {
        valoresInteiros[0] = YouTubeVoteBridgePlugin.TempoVotacao?.Value ?? 40;
        valoresInteiros[1] = YouTubeVoteBridgePlugin.IntervaloRodadas?.Value ?? 0;
        valoresInteiros[2] = YouTubeVoteBridgePlugin.PreparacaoChat?.Value ?? 0;

        valoresBooleanos[0] = YouTubeVoteBridgePlugin.SemFimDeJogo?.Value ?? false;
        valoresBooleanos[1] = YouTubeVoteBridgePlugin.PularFaseVotosJuiz?.Value ?? false;
        valoresBooleanos[2] = YouTubeVoteBridgePlugin.MultiTransmissaoAtiva?.Value ?? false;
        valoresBooleanos[3] = YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores?.Value ?? false;
        valoresBooleanos[4] = YouTubeVoteBridgePlugin.TrocarMapaAoIniciar?.Value ?? false;
        valoresBooleanos[5] = YouTubeVoteBridgePlugin.PermitirVentilacaoNPC?.Value ?? false;
    }

    public static void VerificarTecla()
    {
        if (!Input.GetKeyDown(KeyCode.F9))
            return;

        if (AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            ShipStatus.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Configurações] F9 só abre o painel para o host no lobby.");
            return;
        }

        IntPtr hwnd = janela;
        if (hwnd == IntPtr.Zero)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Configurações] A janela nativa ainda não está pronta.");
            return;
        }

        PostMessageW(hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Executado no Update do jogo. Todas as alterações de ConfigEntry
    /// acontecem na thread principal, nunca na thread da janela Win32.
    /// </summary>
    public static void ProcessarFilaExecucao()
    {
        bool precisaSalvar = false;

        while (filaAlteracoes.TryDequeue(out int id))
        {
            if (id >= ID_TOGGLE_BASE && id < ID_TOGGLE_BASE + 6)
            {
                int indice = id - ID_TOGGLE_BASE;
                ConfigEntry<bool> entrada = ObterBooleano(indice);
                if (entrada == null)
                    continue;

                entrada.Value = valoresBooleanos[indice];
                precisaSalvar = true;
                continue;
            }

            if (id >= ID_INT_MINUS_BASE && id < ID_INT_MINUS_BASE + 3)
            {
                int indice = id - ID_INT_MINUS_BASE;
                AplicarInteiro(indice, -5);
                precisaSalvar = true;
                continue;
            }

            if (id >= ID_INT_PLUS_BASE && id < ID_INT_PLUS_BASE + 3)
            {
                int indice = id - ID_INT_PLUS_BASE;
                AplicarInteiro(indice, 5);
                precisaSalvar = true;
            }
        }

        if (!precisaSalvar)
            return;

        try
        {
            YouTubeVoteBridgePlugin.Instance?.Config.Save();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurações] Não foi possível salvar as opções: " + ex);
        }
    }

    private static ConfigEntry<bool> ObterBooleano(int indice)
    {
        return indice switch
        {
            0 => YouTubeVoteBridgePlugin.SemFimDeJogo,
            1 => YouTubeVoteBridgePlugin.PularFaseVotosJuiz,
            2 => YouTubeVoteBridgePlugin.MultiTransmissaoAtiva,
            3 => YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores,
            4 => YouTubeVoteBridgePlugin.TrocarMapaAoIniciar,
            5 => YouTubeVoteBridgePlugin.PermitirVentilacaoNPC,
            _ => null
        };
    }

    private static void AplicarInteiro(int indice, int delta)
    {
        ConfigEntry<int> entrada = indice switch
        {
            0 => YouTubeVoteBridgePlugin.TempoVotacao,
            1 => YouTubeVoteBridgePlugin.IntervaloRodadas,
            2 => YouTubeVoteBridgePlugin.PreparacaoChat,
            _ => null
        };

        if (entrada == null)
            return;

        int minimo = indice == 0 ? 5 : 0;
        int maximo = indice == 0 ? 600 : indice == 1 ? 600 : 120;
        entrada.Value = Mathf.Clamp(entrada.Value + delta, minimo, maximo);
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
                hbrBackground = new IntPtr(6),
                lpszClassName = CLASSE_JANELA
            };

            if (RegisterClassExW(ref wc) == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[Configurações] RegisterClassEx falhou. Código Win32: " +
                    Marshal.GetLastWin32Error());
                return;
            }

            IntPtr hwnd = CreateWindowExW(
                0, CLASSE_JANELA, "YouTubeChatBridge - Configurações do mod",
                WS_OVERLAPPEDWINDOW,
                unchecked((int)0x80000000), unchecked((int)0x80000000),
                LARGURA_JANELA, ALTURA_JANELA,
                IntPtr.Zero, IntPtr.Zero, instancia, IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[Configurações] CreateWindowEx falhou. Código Win32: " +
                    Marshal.GetLastWin32Error());
                return;
            }

            janela = hwnd;
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
                "[Configurações] Erro ao criar janela nativa: " + ex);
        }
    }

    private static IntPtr ProcedimentoJanela(
        IntPtr hwnd, uint mensagem, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (mensagem)
            {
                case 0x0001: // WM_CREATE
                    CriarFontes();
                    CriarControles(hwnd);
                    return IntPtr.Zero;

                case WM_APP_TOGGLE:
                    if (IsWindowVisible(hwnd))
                        ShowWindow(hwnd, SW_HIDE);
                    else
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                        SetForegroundWindow(hwnd);
                    }
                    return IntPtr.Zero;

                case WM_COMMAND:
                    ProcessarClique(wParam.ToInt32() & 0xFFFF);
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
                "[Configurações] Erro na janela: " + ex);
        }

        return DefWindowProcW(hwnd, mensagem, wParam, lParam);
    }

    private static void CriarControles(IntPtr hwnd)
    {
        IntPtr instancia = GetModuleHandleW(null);

        IntPtr titulo = CriarControle("STATIC",
            "CONFIGURAÇÕES DO MOD - SOMENTE HOST",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            20, 16, 580, 32, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(titulo, fonteTitulo);

        IntPtr subtitulo = CriarControle("STATIC",
            "As alterações são salvas automaticamente. F9 abre/fecha este painel no lobby.",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            22, 52, 590, 24, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(subtitulo, fonteNormal);

        CriarControle("STATIC", "",
            WS_CHILD | WS_VISIBLE | SS_ETCHEDHORZ,
            20, 82, 590, 2, hwnd, IntPtr.Zero, instancia);

        CriarLinhaInteiro(hwnd, instancia, 0, "Tempo da votação", 102);
        CriarLinhaInteiro(hwnd, instancia, 1, "Intervalo entre rodadas", 158);
        CriarLinhaInteiro(hwnd, instancia, 2, "Preparação do chat", 214);

        CriarControle("STATIC", "",
            WS_CHILD | WS_VISIBLE | SS_ETCHEDHORZ,
            20, 270, 590, 2, hwnd, IntPtr.Zero, instancia);

        for (int i = 0; i < nomesBooleanos.Length; i++)
        {
            int y = 286 + i * 48;
            IntPtr nome = CriarControle("STATIC", nomesBooleanos[i],
                WS_CHILD | WS_VISIBLE | SS_LEFT,
                24, y + 7, 455, 26, hwnd, IntPtr.Zero, instancia);
            AplicarFonte(nome, fonteNormal);

            botoesBooleanos[i] = CriarControle("BUTTON",
                valoresBooleanos[i] ? "ATIVADO" : "DESATIVADO",
                WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
                490, y, 120, 32, hwnd,
                new IntPtr(ID_TOGGLE_BASE + i), instancia);
            AplicarFonte(botoesBooleanos[i], fonteNormal);
        }

        IntPtr rodape = CriarControle("STATIC",
            "Feche a janela pelo X. Ela continuará disponível pelo F9.",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            24, 586, 580, 24, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(rodape, fonteNormal);
    }

    private static void CriarLinhaInteiro(
        IntPtr hwnd, IntPtr instancia, int indice, string nome, int y)
    {
        IntPtr texto = CriarControle("STATIC",
            nome + ": " + valoresInteiros[indice] + " s",
            WS_CHILD | WS_VISIBLE | SS_LEFT,
            24, y + 5, 390, 28, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(texto, fonteNormal);
        textosInteiros[indice] = texto;

        IntPtr menos = CriarControle("BUTTON", "- 5",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            440, y, 75, 32, hwnd,
            new IntPtr(ID_INT_MINUS_BASE + indice), instancia);
        AplicarFonte(menos, fonteNormal);

        IntPtr mais = CriarControle("BUTTON", "+ 5",
            WS_CHILD | WS_VISIBLE | BS_PUSHBUTTON,
            525, y, 75, 32, hwnd,
            new IntPtr(ID_INT_PLUS_BASE + indice), instancia);
        AplicarFonte(mais, fonteNormal);
    }

    private static void ProcessarClique(int id)
    {
        if (id >= ID_TOGGLE_BASE && id < ID_TOGGLE_BASE + 6)
        {
            int indice = id - ID_TOGGLE_BASE;
            valoresBooleanos[indice] = !valoresBooleanos[indice];
            SetWindowTextW(botoesBooleanos[indice],
                valoresBooleanos[indice] ? "ATIVADO" : "DESATIVADO");
            filaAlteracoes.Enqueue(id);
            return;
        }

        if (id >= ID_INT_MINUS_BASE && id < ID_INT_MINUS_BASE + 3)
        {
            int indice = id - ID_INT_MINUS_BASE;
            AlterarValorExibido(indice, -5);
            filaAlteracoes.Enqueue(id);
            return;
        }

        if (id >= ID_INT_PLUS_BASE && id < ID_INT_PLUS_BASE + 3)
        {
            int indice = id - ID_INT_PLUS_BASE;
            AlterarValorExibido(indice, 5);
            filaAlteracoes.Enqueue(id);
        }
    }

    private static void AlterarValorExibido(int indice, int delta)
    {
        int minimo = indice == 0 ? 5 : 0;
        int maximo = indice == 0 ? 600 : indice == 1 ? 600 : 120;
        valoresInteiros[indice] = Math.Max(minimo,
            Math.Min(maximo, valoresInteiros[indice] + delta));

        string nome = indice switch
        {
            0 => "Tempo da votação",
            1 => "Intervalo entre rodadas",
            _ => "Preparação do chat"
        };

        SetWindowTextW(textosInteiros[indice],
            nome + ": " + valoresInteiros[indice] + " s");
    }

    private static IntPtr CriarControle(
        string classe, string texto, uint estilo,
        int x, int y, int largura, int altura,
        IntPtr pai, IntPtr id, IntPtr instancia)
    {
        return CreateWindowExW(0, classe, texto, estilo,
            x, y, largura, altura, pai, id, instancia, IntPtr.Zero);
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

    private delegate IntPtr WndProcDelegate(
        IntPtr hwnd, uint mensagem, IntPtr wParam, IntPtr lParam);

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

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW", SetLastError = true)]
    private static extern IntPtr CreateFontW(
        int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight,
        bool bItalic, bool bUnderline, bool bStrikeOut, uint iCharSet,
        uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily,
        string pszFaceName);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessageW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetMessageW", SetLastError = true)]
    private static extern int GetMessageW(
        out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll", EntryPoint = "PostQuitMessage")]
    private static extern void PostQuitMessage(int nExitCode);
}

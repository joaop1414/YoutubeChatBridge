using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Configurador externo de funções. A janela é Win32, como os painéis F6/F7,
/// e não é desenhada dentro da janela do Among Us.
/// </summary>
internal static class ConfiguradorFuncoesMenu
{
    private const string CLASSE_JANELA = "YouTubeVoteBridge_RoleConfigWindow";
    private const uint WM_APP_TOGGLE = 0x8008;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_SETFONT = 0x0030;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint CBS_DROPDOWNLIST = 0x0003;
    private const uint CB_ADDSTRING = 0x0143;
    private const uint CB_SETCURSEL = 0x014E;
    private const uint CB_GETCURSEL = 0x0147;
    private const uint CB_RESETCONTENT = 0x014B;
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const int ID_JOGADOR = 1001;
    private const int ID_FUNCAO = 1002;
    private const int ID_APLICAR = 1003;
    private const int ID_AUTOMATICO = 1004;
    private const int ID_LIMPAR = 1005;
    private const int ID_ATUALIZAR = 1006;
    private const int ID_FECHAR = 1007;

    private static readonly object trava = new();
    private static readonly Dictionary<byte, RoleTypes> atribuicoes = new();
    private static readonly RoleTypes[] funcoes = Enum.GetValues(typeof(RoleTypes))
        .Cast<RoleTypes>()
        .Where(x => !string.Equals(x.ToString(), "None", StringComparison.OrdinalIgnoreCase))
        .Distinct()
        .OrderBy(x => x.ToString())
        .ToArray();
    private static List<(byte Id, string Nome)> jogadores = new();
    private static readonly WndProcDelegate wndProc = ProcedimentoJanela;
    private static Thread threadJanela;
    private static IntPtr janela;
    private static IntPtr comboJogador;
    private static IntPtr comboFuncao;
    private static IntPtr status;
    private static IntPtr fonte;
    private static bool inicializado;

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
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

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

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    public static void Inicializar()
    {
        if (inicializado) return;
        inicializado = true;
        threadJanela = new Thread(CriarJanela)
        {
            IsBackground = true,
            Name = "YouTubeChatBridge configurador de funções"
        };
        threadJanela.Start();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Configurador de funções] Janela externa iniciando. Pressione F8 no lobby.");
    }

    public static void Abrir()
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost || ShipStatus.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Configurador de funções] Disponível somente para o host no lobby, antes da partida.");
            return;
        }

        AtualizarListaJogadores();
        IntPtr hwnd = janela;
        if (hwnd == IntPtr.Zero)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Configurador de funções] A janela externa ainda não está pronta.");
            return;
        }
        PostMessageW(hwnd, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
    }

    internal static Dictionary<byte, RoleTypes> ConsumirConfiguracoes()
    {
        lock (trava)
        {
            Dictionary<byte, RoleTypes> copia = new(atribuicoes);
            atribuicoes.Clear();
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Configurador de funções] {copia.Count} configuração(ões) entregue(s) à seleção inicial.");
            return copia;
        }
    }

    private static void AtualizarListaJogadores()
    {
        List<(byte Id, string Nome)> lista = new();
        try
        {
            if (PlayerControl.AllPlayerControls != null)
            {
                foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
                {
                    if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                        continue;
                    lista.Add((jogador.PlayerId, jogador.Data.PlayerName ?? "Jogador"));
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Configurador de funções] Falha ao atualizar jogadores: " + ex.Message);
        }

        lock (trava) jogadores = lista;
        if (janela != IntPtr.Zero)
            PostMessageW(janela, 0x8009, IntPtr.Zero, IntPtr.Zero);
    }

    private static void CriarJanela()
    {
        try
        {
            IntPtr instancia = GetModuleHandleW(null);
            WNDCLASSEX wc = new()
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = wndProc,
                hInstance = instancia,
                hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                hbrBackground = new IntPtr(6),
                lpszClassName = CLASSE_JANELA
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException("RegisterClassExW falhou: " + Marshal.GetLastWin32Error());

            janela = CreateWindowExW(0, CLASSE_JANELA,
                "YouTubeChatBridge — Configurador de funções (F8)",
                WS_OVERLAPPEDWINDOW, unchecked((int)0x80000000), unchecked((int)0x80000000),
                570, 360, IntPtr.Zero, IntPtr.Zero, instancia, IntPtr.Zero);
            if (janela == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowExW falhou: " + Marshal.GetLastWin32Error());

            ShowWindow(janela, SW_HIDE);
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
                "[Configurador de funções] Erro na janela externa: " + ex);
        }
    }

    private static IntPtr ProcedimentoJanela(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case 0x0001: // WM_CREATE
                CriarControles(hwnd);
                AtualizarCombos();
                return IntPtr.Zero;

            case WM_APP_TOGGLE:
                if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_HIDE);
                else
                {
                    AtualizarCombos();
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                return IntPtr.Zero;

            case 0x8009: // WM_APP_REFRESH
                AtualizarCombos();
                return IntPtr.Zero;

            case WM_COMMAND:
                int id = wParam.ToInt32() & 0xFFFF;
                if (id == ID_APLICAR || id == ID_AUTOMATICO)
                {
                    int indiceJogador = SendMessageW(comboJogador, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    if (indiceJogador < 0)
                    {
                        DefinirStatus("Selecione um jogador.");
                        return IntPtr.Zero;
                    }

                    (byte Id, string Nome) alvo;
                    lock (trava)
                    {
                        if (indiceJogador >= jogadores.Count)
                        {
                            DefinirStatus("Lista de jogadores desatualizada. Atualize.");
                            return IntPtr.Zero;
                        }
                        alvo = jogadores[indiceJogador];
                    }

                    if (id == ID_AUTOMATICO)
                    {
                        lock (trava) atribuicoes.Remove(alvo.Id);
                        DefinirStatus(alvo.Nome + " ficará em Automático.");
                    }
                    else
                    {
                        int indiceFuncao = SendMessageW(comboFuncao, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32();
                        if (indiceFuncao < 0 || indiceFuncao >= funcoes.Length)
                        {
                            DefinirStatus("Selecione uma função.");
                            return IntPtr.Zero;
                        }
                        RoleTypes papel = funcoes[indiceFuncao];
                        lock (trava) atribuicoes[alvo.Id] = papel;
                        DefinirStatus(alvo.Nome + " [ID " + alvo.Id + "] -> " + papel);
                    }
                    return IntPtr.Zero;
                }

                if (id == ID_LIMPAR)
                {
                    lock (trava) atribuicoes.Clear();
                    DefinirStatus("Todas as configurações manuais foram limpas.");
                    return IntPtr.Zero;
                }
                if (id == ID_ATUALIZAR)
                {
                    AtualizarCombos();
                    DefinirStatus("Lista de jogadores atualizada.");
                    return IntPtr.Zero;
                }
                if (id == ID_FECHAR)
                {
                    ShowWindow(hwnd, SW_HIDE);
                    return IntPtr.Zero;
                }
                return IntPtr.Zero;

            case WM_CLOSE:
                ShowWindow(hwnd, SW_HIDE);
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private static void CriarControles(IntPtr hwnd)
    {
        IntPtr instancia = GetModuleHandleW(null);
        fonte = CreateFontW(18, 0, 0, 0, 400, false, false, false, 1, 0, 0, 5, 0, "Segoe UI");

        CriarControle("STATIC", "Defina funções manualmente; os demais jogadores ficam no sorteio vanilla.",
            WS_CHILD | WS_VISIBLE, 20, 18, 520, 28, hwnd, IntPtr.Zero, instancia);
        CriarControle("STATIC", "JOGADOR", WS_CHILD | WS_VISIBLE, 22, 62, 220, 22, hwnd, IntPtr.Zero, instancia);
        comboJogador = CriarControle("COMBOBOX", "",
            WS_CHILD | WS_VISIBLE | WS_BORDER | CBS_DROPDOWNLIST,
            22, 88, 510, 260, hwnd, new IntPtr(ID_JOGADOR), instancia);

        CriarControle("STATIC", "FUNÇÃO", WS_CHILD | WS_VISIBLE, 22, 132, 220, 22, hwnd, IntPtr.Zero, instancia);
        comboFuncao = CriarControle("COMBOBOX", "",
            WS_CHILD | WS_VISIBLE | WS_BORDER | CBS_DROPDOWNLIST,
            22, 158, 510, 260, hwnd, new IntPtr(ID_FUNCAO), instancia);

        CriarControle("BUTTON", "Aplicar função", WS_CHILD | WS_VISIBLE,
            22, 210, 160, 34, hwnd, new IntPtr(ID_APLICAR), instancia);
        CriarControle("BUTTON", "Automático", WS_CHILD | WS_VISIBLE,
            192, 210, 130, 34, hwnd, new IntPtr(ID_AUTOMATICO), instancia);
        CriarControle("BUTTON", "Limpar todas", WS_CHILD | WS_VISIBLE,
            332, 210, 120, 34, hwnd, new IntPtr(ID_LIMPAR), instancia);
        CriarControle("BUTTON", "Atualizar jogadores", WS_CHILD | WS_VISIBLE,
            22, 254, 245, 32, hwnd, new IntPtr(ID_ATUALIZAR), instancia);
        CriarControle("BUTTON", "Fechar (F8)", WS_CHILD | WS_VISIBLE,
            282, 254, 250, 32, hwnd, new IntPtr(ID_FECHAR), instancia);
        status = CriarControle("STATIC", "STATUS: aguardando configuração",
            WS_CHILD | WS_VISIBLE, 22, 300, 510, 28, hwnd, IntPtr.Zero, instancia);
        foreach (IntPtr c in new[] { comboJogador, comboFuncao, status })
            if (c != IntPtr.Zero && fonte != IntPtr.Zero)
                SendMessageW(c, WM_SETFONT, fonte, new IntPtr(1));
    }

    private static void AtualizarCombos()
    {
        if (comboJogador == IntPtr.Zero || comboFuncao == IntPtr.Zero) return;

        SendMessageW(comboJogador, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        List<(byte Id, string Nome)> copia;
        Dictionary<byte, RoleTypes> configs;
        lock (trava)
        {
            copia = new(jogadores);
            configs = new(atribuicoes);
        }

        for (int i = 0; i < copia.Count; i++)
        {
            var p = copia[i];
            string funcao = configs.TryGetValue(p.Id, out RoleTypes role) ? role.ToString() : "Automático";
            AdicionarItem(comboJogador, p.Nome + " [ID " + p.Id + "] — " + funcao);
        }
        if (copia.Count > 0)
            SendMessageW(comboJogador, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);

        SendMessageW(comboFuncao, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < funcoes.Length; i++)
            AdicionarItem(comboFuncao, funcoes[i].ToString());
        if (funcoes.Length > 0)
            SendMessageW(comboFuncao, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);
    }

    private static void AdicionarItem(IntPtr combo, string texto)
    {
        IntPtr ptr = Marshal.StringToHGlobalUni(texto);
        try { SendMessageW(combo, CB_ADDSTRING, IntPtr.Zero, ptr); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    private static void DefinirStatus(string texto)
    {
        if (status != IntPtr.Zero)
            SetWindowTextW(status, "STATUS: " + texto);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Configurador de funções] " + texto);
    }

    private static IntPtr CriarControle(string classe, string texto, uint estilo,
        int x, int y, int largura, int altura, IntPtr pai, IntPtr id, IntPtr instancia)
        => CreateWindowExW(0, classe, texto, estilo, x, y, largura, altura,
            pai, id, instancia, IntPtr.Zero);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
    private static extern IntPtr GetModuleHandleW(string nome);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint ex, string classe, string titulo, uint estilo,
        int x, int y, int largura, int altura, IntPtr pai, IntPtr menu, IntPtr instancia, IntPtr parametro);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")]
    private static extern IntPtr LoadCursorW(IntPtr instancia, IntPtr cursor);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    private static extern bool ShowWindow(IntPtr hwnd, int comando);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SetWindowTextW")]
    private static extern bool SetWindowTextW(IntPtr hwnd, string texto);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateFontW")]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int peso,
        bool italico, bool sublinhado, bool riscado, uint charset, uint precisaoSaida,
        uint precisaoClip, uint qualidade, uint pitch, string fonte);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll", EntryPoint = "TranslateMessage")]
    private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);
}


/// <summary>
/// Aplica somente as substituições escolhidas no configurador depois que o
/// RoleManager termina a seleção vanilla. Assim o jogo calcula normalmente
/// a quantidade de impostores e as demais funções; o menu sobrescreve apenas
/// os jogadores configurados manualmente.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class ConfiguradorFuncoesSelecaoInicialPatch
{
    private static void Postfix()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        Dictionary<byte, RoleTypes> configuracoes =
            ConfiguradorFuncoesMenu.ConsumirConfiguracoes();

        if (configuracoes.Count == 0)
            return;

        List<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            if (configuracoes.TryGetValue(jogador.PlayerId, out RoleTypes funcao))
                atribuicoes.Add(new KeyValuePair<PlayerControl, RoleTypes>(jogador, funcao));
        }

        if (atribuicoes.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Configurador de funções] Nenhum jogador configurado participou da seleção inicial.");
            return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Configurador de funções] Aplicando " + atribuicoes.Count +
            " substituição(ões) após a seleção vanilla, sem interceptar a distribuição inicial.");

        if (!RoleSwitchAPI.TrocarFuncoes(atribuicoes))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurador de funções] A seleção vanilla ocorreu, mas não foi possível aplicar as funções manuais.");
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Painel administrativo externo do F8. A janela Win32 fica fora do jogo;
/// todas as operações que acessam Unity/Among Us são executadas na thread principal.
/// </summary>
internal static class ControladorPartidaMenu
{
    private const string CLASSE_JANELA = "YouTubeVoteBridge_MatchController";
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_APP_TOGGLE = 0x8001;
    private const uint WM_APP_STATUS = 0x8002;
    private const uint WM_APP_REFRESH = 0x8003;
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_CHILD = 0x40000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_BORDER = 0x00800000;
    private const uint CBS_DROPDOWNLIST = 0x0003;
    private const uint CB_ADDSTRING = 0x0143;
    private const uint CB_SETCURSEL = 0x014E;
    private const uint CB_GETCURSEL = 0x0147;
    private const uint CB_RESETCONTENT = 0x014B;
    private const uint WM_SETFONT = 0x0030;
    private const int SW_HIDE = 0;
    private const int SW_RESTORE = 9;

    private const int ID_JOGADOR = 1001;
    private const int ID_FUNCAO = 1002;
    private const int ID_ATUALIZAR = 1003;
    private const int ID_APLICAR_FUNCAO = 1004;
    private const int ID_TIME_TRIPULACAO = 1005;
    private const int ID_TIME_IMPOSTOR = 1006;
    private const int ID_VITORIA_TRIPULACAO = 1007;
    private const int ID_VITORIA_IMPOSTOR = 1008;
    private const int ID_ENCERRAR = 1009;
    private const int ID_PULAR_VOTACAO = 1010;
    private const int ID_FANTASMA_TRIPULANTE = 1011;
    private const int ID_FANTASMA_IMPOSTOR = 1012;
    private const int ID_FECHAR = 1013;
    private const int ID_ZOOM = 1014;
    private const int ID_CHAMAR_REUNIAO = 1015;
    private const int ID_ALTERNAR_CAMINHO_NPC = 1016;

    private enum TipoAcao
    {
        AplicarFuncao,
        TransformarTripulacao,
        TransformarImpostores,
        VitoriaTripulacao,
        VitoriaImpostores,
        Encerrar,
        PularVotacao,
        FantasmaTripulante,
        FantasmaImpostor,
        AtualizarJogadores,
        AlternarZoom,
        ChamarReuniao,
        AlternarCaminhoNpc
    }

    private sealed class Acao
    {
        public TipoAcao Tipo;
        public byte PlayerId;
        public RoleTypes Funcao;

        public Acao(TipoAcao tipo, byte playerId = 0, RoleTypes funcao = RoleTypes.Crewmate)
        {
            Tipo = tipo;
            PlayerId = playerId;
            Funcao = funcao;
        }
    }

    private sealed class JogadorItem
    {
        public byte Id;
        public string Nome;

        public JogadorItem(byte id, string nome)
        {
            Id = id;
            Nome = nome;
        }
    }

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

    private static readonly object travaJogadores = new();
    private static readonly ConcurrentQueue<Acao> fila = new();
    private static readonly WndProcDelegate procedimentoJanela = ProcedimentoJanela;
    private static readonly RoleTypes[] funcoes = Enum.GetValues(typeof(RoleTypes))
        .Cast<RoleTypes>()
        .Where(x => !string.Equals(x.ToString(), "None", StringComparison.OrdinalIgnoreCase))
        .Distinct()
        .OrderBy(x => x.ToString())
        .ToArray();

    private static List<JogadorItem> jogadores = new();
    private static Thread threadJanela;
    private static IntPtr janela;
    private static IntPtr comboJogador;
    private static IntPtr comboFuncao;
    private static IntPtr textoStatus;
    private static IntPtr fonteNormal;
    private static IntPtr fonteTitulo;
    private static bool inicializado;
    private static string mensagem = "Aguardando o host abrir o controlador (F8).";
    private static float encerrarEm = -1f;
    private static string motivoEncerramento = "CrewmatesByVote";

    public static void Inicializar()
    {
        if (inicializado) return;
        inicializado = true;

        threadJanela = new Thread(CriarJanela)
        {
            IsBackground = true,
            Name = "YouTubeChatBridge controlador F8"
        };
        threadJanela.Start();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Controlador F8] Janela externa iniciada. Disponível para o host durante a partida.");
    }

    public static void ResetarEstadoPartida(string motivo)
    {
        encerrarEm = -1f;
        motivoEncerramento = "CrewmatesByVote";
        int descartadas = 0;
        while (fila.TryDequeue(out _)) descartadas++;

        ZoomCamera.Desativar();
        DefinirStatus("Estado do controlador limpo (" + motivo + ").");
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Controlador F8] Ações pendentes descartadas=" + descartadas + ".");
    }

    public static void VerificarTecla()
    {
        if (!Input.GetKeyDown(KeyCode.F8))
            return;

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Controlador F8] F8 disponível somente para o host.");
            return;
        }

        // Antes de ShipStatus existir, F8 abre o antigo configurador de funções
        // do lobby. Depois que o mapa é carregado, abre o controlador da partida.
        if (ShipStatus.Instance == null)
        {
            ConfiguradorFuncoesMenu.Abrir();
            return;
        }

        AtualizarJogadores();
        if (janela == IntPtr.Zero)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Controlador F8] A janela externa ainda não está pronta.");
            return;
        }

        PostMessageW(janela, WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Executado no Update do jogo. Nenhuma chamada Unity é feita pela thread da janela.
    /// </summary>
    public static void ProcessarFilaExecucao()
    {
        if (encerrarEm >= 0f && Time.realtimeSinceStartup >= encerrarEm)
        {
            encerrarEm = -1f;
            EncerrarPartida(motivoEncerramento);
            motivoEncerramento = "CrewmatesByVote";
        }

        while (fila.TryDequeue(out Acao acao))
        {
            try
            {
                if (acao.Tipo == TipoAcao.AtualizarJogadores)
                {
                    AtualizarJogadores();
                    DefinirStatus("Lista de jogadores atualizada.");
                    continue;
                }

                if (!ValidarHostEPartida())
                    continue;

                switch (acao.Tipo)
                {
                    case TipoAcao.AplicarFuncao:
                        AplicarFuncaoIndividual(acao.PlayerId, acao.Funcao);
                        break;

                    case TipoAcao.TransformarTripulacao:
                        TransformarTodos(RoleTypes.Crewmate, "Tripulantes");
                        break;

                    case TipoAcao.TransformarImpostores:
                        TransformarTodos(RoleTypes.Impostor, "Impostores");
                        break;

                    case TipoAcao.VitoriaTripulacao:
                        DefinirVitoria("CrewmatesByVote", "Vitória da tripulação solicitada, sem alterar funções.");
                        break;

                    case TipoAcao.VitoriaImpostores:
                        DefinirVitoria("ImpostorsByKill", "Vitória dos impostores solicitada, sem alterar funções.");
                        break;

                    case TipoAcao.Encerrar:
                        EncerrarPartida("CrewmatesByVote");
                        break;

                    case TipoAcao.PularVotacao:
                        PularVotacao();
                        break;

                    case TipoAcao.FantasmaTripulante:
                        TransformarEmFantasmaEAgendarFim(RoleTypes.CrewmateGhost, "CrewmatesByVote");
                        break;

                    case TipoAcao.FantasmaImpostor:
                        TransformarEmFantasmaEAgendarFim(RoleTypes.ImpostorGhost, "ImpostorsByKill");
                        break;

                    case TipoAcao.AlternarZoom:
                        bool zoomAtivo = ZoomCamera.Alternar();
                        DefinirStatus(zoomAtivo
                            ? "Zoom ativado. Use a roda do mouse para aproximar/afastar."
                            : "Zoom desativado; câmera restaurada.");
                        break;

                    case TipoAcao.ChamarReuniao:
                        ChamarReuniaoComAutoReport();
                        break;

                    case TipoAcao.AlternarCaminhoNpc:
                        bool caminhoVisivel = EventoSeguidorMortal.AlternarVisualizacaoCaminho();
                        DefinirStatus(caminhoVisivel
                            ? "Caminho do NPC visível no mapa."
                            : "Visualização do caminho do NPC desativada.");
                        break;
                }
            }
            catch (Exception ex)
            {
                DefinirStatus("Erro: " + ex.Message);
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[Controlador F8] Falha ao processar ação: " + ex);
            }
        }
    }

    private static void ChamarReuniaoComAutoReport()
    {
        if (!ValidarHostEPartida()) return;
        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.IsDead && !jogador.Data.Disconnected &&
                !jogador.notRealPlayer)
                vivos.Add(jogador);
        if (vivos.Count == 0)
        {
            DefinirStatus("Não há jogadores vivos para realizar o auto-report.");
            return;
        }

        PlayerControl reporter = vivos[UnityEngine.Random.Range(0, vivos.Count)];
        try
        {
            object meetingManager = ObterInstanciaTipo("MeetingRoomManager");
            if (meetingManager != null)
                InvocarMetodoCompativel(meetingManager, "AssignSelf", reporter, reporter.Data);
            if (HudManager.Instance != null)
                InvocarMetodoCompativel(HudManager.Instance, "OpenMeetingRoom", reporter);
            if (!InvocarMetodoCompativel(reporter, "RpcStartMeeting", reporter.Data))
                throw new MissingMethodException("RpcStartMeeting(NetworkedPlayerInfo) não foi encontrado.");

            YouTubeVoteBridgePlugin.EnviarMensagemAnfitriao(
                "<color=blue>ESSA REUNIÃO FOI CHAMADA PELO ANFITRIÃO</color>",
                reporter);
            DefinirStatus("Reunião iniciada por auto-report de " +
                (reporter.Data.PlayerName ?? ("ID " + reporter.PlayerId)) + ".");
        }
        catch (Exception ex)
        {
            DefinirStatus("Falha ao chamar reunião: " + ex.Message);
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Controlador F8] Falha no auto-report: " + ex);
        }
    }

    private static object ObterInstanciaTipo(string nomeTipo)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type tipo = assembly.GetType(nomeTipo);
            if (tipo == null) continue;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo propriedade = tipo.GetProperty("Instance", flags);
            if (propriedade != null) return propriedade.GetValue(null);
            FieldInfo campo = tipo.GetField("Instance", flags);
            if (campo != null) return campo.GetValue(null);
        }
        return null;
    }

    private static bool InvocarMetodoCompativel(object instancia, string nome, params object[] argumentos)
    {
        if (instancia == null) return false;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo metodo in instancia.GetType().GetMethods(flags))
        {
            if (!string.Equals(metodo.Name, nome, StringComparison.Ordinal)) continue;
            ParameterInfo[] parametros = metodo.GetParameters();
            if (parametros.Length != argumentos.Length) continue;
            bool compativel = true;
            for (int i = 0; i < parametros.Length; i++)
                if (argumentos[i] != null && !parametros[i].ParameterType.IsInstanceOfType(argumentos[i]))
                {
                    compativel = false;
                    break;
                }
            if (!compativel) continue;
            metodo.Invoke(instancia, argumentos);
            return true;
        }
        return false;
    }

    private static bool ValidarHostEPartida()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null || ShipStatus.Instance == null)
        {
            DefinirStatus("Ação disponível somente para o host durante a partida.");
            return false;
        }
        return true;
    }

    private static void AplicarFuncaoIndividual(byte playerId, RoleTypes funcao)
    {
        PlayerControl jogador = EncontrarJogador(playerId);
        if (jogador == null)
        {
            DefinirStatus("Jogador não encontrado ou desconectado.");
            return;
        }

        bool sucesso = RoleSwitchAPI.TrocarFuncao(jogador, funcao);
        DefinirStatus(sucesso
            ? jogador.Data.PlayerName + " -> " + funcao + ". Jogadores vivos serão preservados pela rede."
            : "Falha ao trocar a função. Consulte o log RoleSwitchAPI.");
    }

    private static void TransformarTodos(RoleTypes funcao, string nomeTime)
    {
        List<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            atribuicoes.Add(new KeyValuePair<PlayerControl, RoleTypes>(jogador, funcao));
        }

        if (atribuicoes.Count == 0)
        {
            DefinirStatus("Nenhum jogador conectado para transformar.");
            return;
        }

        bool sucesso = RoleSwitchAPI.TrocarFuncoes(atribuicoes);
        DefinirStatus(sucesso
            ? atribuicoes.Count + " jogador(es) transformados em " + nomeTime +
              ". Quem estava vivo antes será restaurado pela rede."
            : "Falha ao transformar os jogadores. Consulte o log RoleSwitchAPI.");
    }

    private static void TransformarEmFantasmaEAgendarFim(RoleTypes funcaoFantasma, string motivo)
    {
        List<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            atribuicoes.Add(new KeyValuePair<PlayerControl, RoleTypes>(jogador, funcaoFantasma));
        }

        if (atribuicoes.Count == 0 || !RoleSwitchAPI.TrocarFuncoes(atribuicoes))
        {
            DefinirStatus("Não foi possível aplicar as funções fantasma.");
            return;
        }

        motivoEncerramento = motivo;
        encerrarEm = Time.realtimeSinceStartup + 1.5f;
        DefinirStatus("Funções fantasma aplicadas. Encerrando a partida em 1,5 segundo...");
    }

    private static void DefinirVitoria(string motivo, string texto)
    {
        DefinirStatus(texto);
        EncerrarPartida(motivo);
    }

    private static void PularVotacao()
    {
        MeetingHud reuniao = MeetingHud.Instance;
        if (reuniao == null || reuniao.playerStates == null ||
            reuniao.state == MeetingHud.MeetingStates.Results ||
            reuniao.state == MeetingHud.MeetingStates.Proceeding)
        {
            DefinirStatus("Não existe uma fase de votação ativa no momento.");
            return;
        }

        YouTubeVotePularVotacaoJuizHelper.TentarEncerrar(reuniao, true);
        DefinirStatus("Votação encerrada. Quem não votou permaneceu sem escolher jogador ou skip.");
    }

    private static void EncerrarPartida(string nomeMotivo)
    {
        if (!ValidarHostEPartida()) return;

        bool enviado = false;
        try
        {
            GameManager gerente = GameManager.Instance;
            if (gerente != null)
            {
                foreach (MethodInfo metodo in typeof(GameManager).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (metodo.Name != "RpcEndGame") continue;

                    ParameterInfo[] parametros = metodo.GetParameters();
                    if (parametros.Length != 2 ||
                        !parametros[0].ParameterType.IsEnum ||
                        parametros[1].ParameterType != typeof(bool))
                        continue;

                    object motivo = ObterMotivoFimDeJogo(parametros[0].ParameterType, nomeMotivo);
                    YouTubeVoteBridgePlugin.ExecutarFimDeJogoAutorizado(
                        () => metodo.Invoke(gerente, new object[] { motivo, false }));
                    enviado = true;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Controlador F8] Falha ao chamar RpcEndGame: " + ex);
        }

        DefinirStatus(enviado
            ? "RPC de encerramento enviado: " + nomeMotivo + "."
            : "Não foi possível encontrar/chamar RpcEndGame.");

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Controlador F8] Fim de partida: motivo=" + nomeMotivo + ", enviado=" + enviado + ".");
    }

    private static object ObterMotivoFimDeJogo(Type tipoEnum, string solicitado)
    {
        string[] nomes = Enum.GetNames(tipoEnum);
        string encontrado = nomes.FirstOrDefault(n =>
            string.Equals(n, solicitado, StringComparison.OrdinalIgnoreCase));

        if (encontrado == null)
        {
            bool impostor = !string.IsNullOrEmpty(solicitado) &&
                solicitado.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0;
            string[] preferidos = impostor
                ? new[] { "ImpostorsByKill", "ImpostorsByVote", "ImpostorsBySabotage" }
                : new[] { "CrewmatesByVote", "CrewmatesByTask" };

            foreach (string nome in preferidos)
            {
                encontrado = nomes.FirstOrDefault(n =>
                    string.Equals(n, nome, StringComparison.OrdinalIgnoreCase));
                if (encontrado != null) break;
            }
        }

        if (encontrado == null)
            encontrado = nomes.FirstOrDefault(n =>
                n.IndexOf(solicitado.Contains("Impostor") ? "Impostor" : "Human",
                    StringComparison.OrdinalIgnoreCase) >= 0);

        if (encontrado == null)
            throw new InvalidOperationException(
                "Motivo de fim de jogo não encontrado: " + solicitado +
                ". Valores disponíveis: " + string.Join(", ", nomes));

        return Enum.Parse(tipoEnum, encontrado, true);
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        if (PlayerControl.AllPlayerControls == null) return null;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.PlayerId == playerId &&
                jogador.Data != null && !jogador.Data.Disconnected)
                return jogador;
        }
        return null;
    }

    private static void AtualizarJogadores()
    {
        List<JogadorItem> lista = new();
        try
        {
            if (PlayerControl.AllPlayerControls != null)
            {
                foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
                {
                    if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                        continue;

                    string estado = jogador.Data.IsDead ? "MORTO" : "VIVO";
                    lista.Add(new JogadorItem(jogador.PlayerId,
                        jogador.Data.PlayerName + " [ID " + jogador.PlayerId + "] — " + estado));
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Controlador F8] Falha ao atualizar jogadores: " + ex.Message);
        }

        lock (travaJogadores) jogadores = lista;
        if (janela != IntPtr.Zero)
            PostMessageW(janela, WM_APP_REFRESH, IntPtr.Zero, IntPtr.Zero);
    }

    private static void CriarJanela()
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
                throw new InvalidOperationException("RegisterClassExW falhou: " + Marshal.GetLastWin32Error());

            janela = CreateWindowExW(0, CLASSE_JANELA,
                "YouTubeChatBridge — Controlador da partida (F8)",
                WS_OVERLAPPEDWINDOW,
                unchecked((int)0x80000000), unchecked((int)0x80000000),
                760, 740, IntPtr.Zero, IntPtr.Zero, instancia, IntPtr.Zero);

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
                "[Controlador F8] Erro ao criar janela externa: " + ex);
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
                if (IsWindowVisible(hwnd))
                    ShowWindow(hwnd, SW_HIDE);
                else
                {
                    AtualizarCombos();
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                }
                return IntPtr.Zero;

            case WM_APP_REFRESH:
                AtualizarCombos();
                return IntPtr.Zero;

            case WM_APP_STATUS:
                if (textoStatus != IntPtr.Zero)
                    SetWindowTextW(textoStatus, "STATUS: " + mensagem);
                return IntPtr.Zero;

            case WM_COMMAND:
                int id = wParam.ToInt32() & 0xFFFF;
                if (id == ID_FECHAR)
                {
                    ShowWindow(hwnd, SW_HIDE);
                    return IntPtr.Zero;
                }

                if (id == ID_ATUALIZAR)
                {
                    fila.Enqueue(new Acao(TipoAcao.AtualizarJogadores));
                    DefinirStatus("Atualizando jogadores...");
                    return IntPtr.Zero;
                }

                if (id == ID_APLICAR_FUNCAO)
                {
                    int jogadorIndice = SendMessageW(comboJogador, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32();
                    int funcaoIndice = SendMessageW(comboFuncao, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero).ToInt32();

                    JogadorItem selecionado = null;
                    lock (travaJogadores)
                    {
                        if (jogadorIndice >= 0 && jogadorIndice < jogadores.Count)
                            selecionado = jogadores[jogadorIndice];
                    }

                    if (selecionado == null || funcaoIndice < 0 || funcaoIndice >= funcoes.Length)
                    {
                        DefinirStatus("Selecione um jogador e uma função.");
                        return IntPtr.Zero;
                    }

                    fila.Enqueue(new Acao(TipoAcao.AplicarFuncao, selecionado.Id, funcoes[funcaoIndice]));
                    DefinirStatus("Solicitação de troca enfileirada...");
                    return IntPtr.Zero;
                }

                TipoAcao? acao = id switch
                {
                    ID_TIME_TRIPULACAO => TipoAcao.TransformarTripulacao,
                    ID_TIME_IMPOSTOR => TipoAcao.TransformarImpostores,
                    ID_VITORIA_TRIPULACAO => TipoAcao.VitoriaTripulacao,
                    ID_VITORIA_IMPOSTOR => TipoAcao.VitoriaImpostores,
                    ID_ENCERRAR => TipoAcao.Encerrar,
                    ID_PULAR_VOTACAO => TipoAcao.PularVotacao,
                    ID_FANTASMA_TRIPULANTE => TipoAcao.FantasmaTripulante,
                    ID_FANTASMA_IMPOSTOR => TipoAcao.FantasmaImpostor,
                    ID_ZOOM => TipoAcao.AlternarZoom,
                    ID_CHAMAR_REUNIAO => TipoAcao.ChamarReuniao,
                    ID_ALTERNAR_CAMINHO_NPC => TipoAcao.AlternarCaminhoNpc,
                    _ => null
                };

                if (acao.HasValue)
                {
                    fila.Enqueue(new Acao(acao.Value));
                    DefinirStatus("Solicitação enfileirada...");
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
        fonteNormal = CreateFontW(18, 0, 0, 0, 400, false, false, false, 1, 0, 0, 5, 0, "Segoe UI");
        fonteTitulo = CreateFontW(24, 0, 0, 0, 700, false, false, false, 1, 0, 0, 5, 0, "Segoe UI Semibold");

        IntPtr titulo = CriarControle("STATIC", "CONTROLADOR DA PARTIDA",
            WS_CHILD | WS_VISIBLE, 22, 14, 700, 30, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(titulo, fonteTitulo);

        IntPtr subtitulo = CriarControle("STATIC",
            "Somente host. As ações são executadas no jogo; esta janela fica fora do Among Us.",
            WS_CHILD | WS_VISIBLE, 24, 48, 700, 24, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(subtitulo, fonteNormal);

        CriarControle("STATIC", "JOGADOR", WS_CHILD | WS_VISIBLE, 24, 88, 320, 22, hwnd, IntPtr.Zero, instancia);
        comboJogador = CriarControle("COMBOBOX", "",
            WS_CHILD | WS_VISIBLE | WS_BORDER | CBS_DROPDOWNLIST,
            24, 112, 390, 260, hwnd, new IntPtr(ID_JOGADOR), instancia);

        CriarControle("STATIC", "FUNÇÃO", WS_CHILD | WS_VISIBLE, 430, 88, 290, 22, hwnd, IntPtr.Zero, instancia);
        comboFuncao = CriarControle("COMBOBOX", "",
            WS_CHILD | WS_VISIBLE | WS_BORDER | CBS_DROPDOWNLIST,
            430, 112, 290, 260, hwnd, new IntPtr(ID_FUNCAO), instancia);

        CriarBotao(hwnd, instancia, "Atualizar lista", 24, 154, 170, 32, ID_ATUALIZAR);
        CriarBotao(hwnd, instancia, "Aplicar função ao jogador", 205, 154, 515, 32, ID_APLICAR_FUNCAO);

        CriarSeparador(hwnd, instancia, 202);
        CriarRotulo(hwnd, instancia, "TRANSFORMAR TODOS OS JOGADORES", 24, 212);
        CriarBotao(hwnd, instancia, "Todos -> Tripulantes", 24, 240, 340, 36, ID_TIME_TRIPULACAO);
        CriarBotao(hwnd, instancia, "Todos -> Impostores", 380, 240, 340, 36, ID_TIME_IMPOSTOR);

        CriarSeparador(hwnd, instancia, 292);
        CriarRotulo(hwnd, instancia, "DAR VITÓRIA SEM ALTERAR AS FUNÇÕES", 24, 302);
        CriarBotao(hwnd, instancia, "Vitória da tripulação", 24, 330, 340, 36, ID_VITORIA_TRIPULACAO);
        CriarBotao(hwnd, instancia, "Vitória dos impostores", 380, 330, 340, 36, ID_VITORIA_IMPOSTOR);

        CriarSeparador(hwnd, instancia, 382);
        CriarRotulo(hwnd, instancia, "OUTROS CONTROLES", 24, 392);
        CriarBotao(hwnd, instancia, "Encerrar partida", 24, 420, 220, 34, ID_ENCERRAR);
        CriarBotao(hwnd, instancia, "Pular votação", 260, 420, 220, 34, ID_PULAR_VOTACAO);
        CriarBotao(hwnd, instancia, "Tripulante fantasma + vitória", 496, 420, 224, 34, ID_FANTASMA_TRIPULANTE);
        CriarBotao(hwnd, instancia, "Impostor fantasma + vitória", 24, 462, 340, 34, ID_FANTASMA_IMPOSTOR);
        CriarBotao(hwnd, instancia, "Fechar (F8)", 380, 462, 340, 34, ID_FECHAR);
        CriarBotao(hwnd, instancia, "Alternar zoom da câmera", 24, 505, 696, 34, ID_ZOOM);
        CriarBotao(hwnd, instancia, "Chamar reunião (auto-report aleatório)", 24, 548, 696, 34, ID_CHAMAR_REUNIAO);
        CriarBotao(hwnd, instancia, "Mostrar / ocultar caminho do NPC", 24, 590, 696, 34, ID_ALTERNAR_CAMINHO_NPC);

        textoStatus = CriarControle("STATIC", "STATUS: " + mensagem,
            WS_CHILD | WS_VISIBLE, 24, 638, 700, 32, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(textoStatus, fonteNormal);
    }

    private static void AtualizarCombos()
    {
        if (comboJogador == IntPtr.Zero || comboFuncao == IntPtr.Zero) return;

        SendMessageW(comboJogador, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
        List<JogadorItem> copia;
        lock (travaJogadores) copia = new List<JogadorItem>(jogadores);

        for (int i = 0; i < copia.Count; i++)
            AdicionarItem(comboJogador, copia[i].Nome);
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

    private static void CriarSeparador(IntPtr hwnd, IntPtr instancia, int y)
        => CriarControle("STATIC", "", WS_CHILD | WS_VISIBLE | 0x10,
            20, y, 710, 2, hwnd, IntPtr.Zero, instancia);

    private static void CriarRotulo(IntPtr hwnd, IntPtr instancia, string texto, int x, int y)
    {
        IntPtr c = CriarControle("STATIC", texto, WS_CHILD | WS_VISIBLE,
            x, y, 690, 22, hwnd, IntPtr.Zero, instancia);
        AplicarFonte(c, fonteNormal);
    }

    private static void CriarBotao(IntPtr hwnd, IntPtr instancia, string texto,
        int x, int y, int largura, int altura, int id)
    {
        IntPtr c = CriarControle("BUTTON", texto, WS_CHILD | WS_VISIBLE,
            x, y, largura, altura, hwnd, new IntPtr(id), instancia);
        AplicarFonte(c, fonteNormal);
    }

    private static IntPtr CriarControle(string classe, string texto, uint estilo,
        int x, int y, int largura, int altura, IntPtr pai, IntPtr id, IntPtr instancia)
        => CreateWindowExW(0, classe, texto, estilo, x, y, largura, altura,
            pai, id, instancia, IntPtr.Zero);

    private static void AplicarFonte(IntPtr controle, IntPtr fonte)
    {
        if (controle != IntPtr.Zero && fonte != IntPtr.Zero)
            SendMessageW(controle, WM_SETFONT, fonte, new IntPtr(1));
    }

    private static void DefinirStatus(string texto)
    {
        mensagem = texto ?? "";
        if (janela != IntPtr.Zero)
            PostMessageW(janela, WM_APP_STATUS, IntPtr.Zero, IntPtr.Zero);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Controlador F8] " + mensagem);
    }

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
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowTextW(IntPtr hwnd, string texto);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
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

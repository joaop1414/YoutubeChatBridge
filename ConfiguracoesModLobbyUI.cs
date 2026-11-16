using System;
using BepInEx.Configuration;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Botão e painel IMGUI de configurações dentro do lobby do Among Us.
/// Só aparece para o host e não depende de uma janela Win32 externa.
/// </summary>
internal sealed class ConfiguracoesModLobbyUI : MonoBehaviour
{
    private static bool tipoRegistrado;
    private static bool painelAberto;
    private static Rect janela = new Rect(0f, 0f, 520f, 590f);
    private static Vector2 rolagem;
    private static string liveIdEditando = string.Empty;

    public ConfiguracoesModLobbyUI(IntPtr ptr) : base(ptr) { }

    internal static void Inicializar()
    {
        if (!tipoRegistrado)
        {
            ClassInjector.RegisterTypeInIl2Cpp<ConfiguracoesModLobbyUI>();
            tipoRegistrado = true;
        }

        if (UnityEngine.Object.FindObjectOfType<ConfiguracoesModLobbyUI>() != null)
            return;

        GameObject objeto = new GameObject("YouTubeVoteBridge_ConfiguracoesLobby");
        UnityEngine.Object.DontDestroyOnLoad(objeto);
        objeto.AddComponent<ConfiguracoesModLobbyUI>();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Configurações] Botão de configurações do mod inicializado para o lobby.");
    }

    private static bool HostNoLobby()
    {
        return AmongUsClient.Instance != null &&
            AmongUsClient.Instance.AmHost &&
            ShipStatus.Instance == null;
    }

    private void OnGUI()
    {
        if (!HostNoLobby())
        {
            painelAberto = false;
            return;
        }

        float larguraBotao = Mathf.Min(250f, Screen.width - 24f);
        Rect botao = new Rect(14f, Screen.height - 58f, larguraBotao, 42f);

        if (GUI.Button(botao, "⚙  Configurações do mod"))
        {
            painelAberto = !painelAberto;
            if (painelAberto)
                liveIdEditando = YouTubeVoteBridgePlugin.LiveId?.Value ?? string.Empty;
        }

        if (!painelAberto)
            return;

        float largura = Mathf.Min(540f, Screen.width - 24f);
        float altura = Mathf.Min(610f, Screen.height - 24f);
        janela.width = largura;
        janela.height = altura;
        janela.x = Mathf.Clamp(janela.x, 12f, Mathf.Max(12f, Screen.width - largura - 12f));
        janela.y = Mathf.Clamp(janela.y, 12f, Mathf.Max(12f, Screen.height - altura - 12f));

        // O wrapper IL2CPP do Unity pode não resolver diretamente o method group
        // para GUI.WindowFunction. A lambda cria o delegate com a assinatura esperada.
        janela = GUI.Window(
            73421,
            janela,
            id => DesenharJanela(id),
            "YouTubeVoteBridge - Configurações");
    }

    private void DesenharJanela(int id)
    {
        bool precisaSalvar = false;

        GUILayout.BeginVertical();
        GUILayout.Label("Configurações do mod (somente host)");
        GUILayout.Space(6f);

        rolagem = GUILayout.BeginScrollView(rolagem, GUILayout.Height(janela.height - 105f));

        DesenharInteiro("Tempo da votação", YouTubeVoteBridgePlugin.TempoVotacao, 5, 600, 5, ref precisaSalvar);
        DesenharInteiro("Intervalo entre rodadas", YouTubeVoteBridgePlugin.IntervaloRodadas, 0, 600, 5, ref precisaSalvar);
        DesenharInteiro("Preparação do chat", YouTubeVoteBridgePlugin.PreparacaoChat, 0, 120, 5, ref precisaSalvar);

        GUILayout.Space(8f);
        DesenharBooleano("Sem fim de jogo", YouTubeVoteBridgePlugin.SemFimDeJogo, ref precisaSalvar);
        DesenharBooleano("Iniciar partida com Enter", YouTubeVoteBridgePlugin.EnterKeyToStartGame, ref precisaSalvar);
        DesenharBooleano("Pular fase de votos quando o Juiz usa sua habilidade", YouTubeVoteBridgePlugin.PularFaseVotosJuiz, ref precisaSalvar);
        DesenharBooleano("Multitransmissão", YouTubeVoteBridgePlugin.MultiTransmissaoAtiva, ref precisaSalvar);
        DesenharBooleano("Permitir IDs de outros jogadores", YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores, ref precisaSalvar);
        DesenharBooleano("Escolher mapa aleatório ao iniciar", YouTubeVoteBridgePlugin.TrocarMapaAoIniciar, ref precisaSalvar);
        DesenharBooleano("Permitir que o NPC Assassino use ventilações", YouTubeVoteBridgePlugin.PermitirVentilacaoNPC, ref precisaSalvar);

        GUILayout.Space(10f);
        GUILayout.Label("ID da live principal");
        liveIdEditando = GUILayout.TextField(liveIdEditando ?? string.Empty, 100);
        if (GUILayout.Button("Salvar ID da live"))
        {
            if (YouTubeVoteBridgePlugin.LiveId != null)
            {
                YouTubeVoteBridgePlugin.LiveId.Value = (liveIdEditando ?? string.Empty).Trim();
                precisaSalvar = true;
            }
        }

        GUILayout.Space(6f);
        GUILayout.Label("Estado da live: " +
            (YouTubeVoteBridgePlugin.LiveConectada != null && YouTubeVoteBridgePlugin.LiveConectada.Value
                ? "Conectada" : "Desconectada"));
        GUILayout.Label("Votação ativa: " +
            (YouTubeVoteBridgePlugin.VotacaoAtiva != null && YouTubeVoteBridgePlugin.VotacaoAtiva.Value
                ? "Sim" : "Não"));

        GUILayout.EndScrollView();

        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Fechar", GUILayout.Width(110f), GUILayout.Height(30f)))
            painelAberto = false;
        GUILayout.EndHorizontal();
        GUILayout.EndVertical();

        if (precisaSalvar)
            SalvarConfiguracoes();

        GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
    }

    private static void DesenharBooleano(
        string titulo, ConfigEntry<bool> entrada, ref bool precisaSalvar)
    {
        if (entrada == null)
            return;

        bool novoValor = GUILayout.Toggle(entrada.Value, titulo);
        if (novoValor == entrada.Value)
            return;

        entrada.Value = novoValor;
        precisaSalvar = true;
    }

    private static void DesenharInteiro(
        string titulo,
        ConfigEntry<int> entrada,
        int minimo,
        int maximo,
        int passo,
        ref bool precisaSalvar)
    {
        if (entrada == null)
            return;

        GUILayout.BeginHorizontal();
        GUILayout.Label(titulo + ": " + entrada.Value + " s");
        if (GUILayout.Button("-", GUILayout.Width(34f)))
        {
            entrada.Value = Mathf.Clamp(entrada.Value - passo, minimo, maximo);
            precisaSalvar = true;
        }
        if (GUILayout.Button("+", GUILayout.Width(34f)))
        {
            entrada.Value = Mathf.Clamp(entrada.Value + passo, minimo, maximo);
            precisaSalvar = true;
        }
        GUILayout.EndHorizontal();
    }

    private static void SalvarConfiguracoes()
    {
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
}

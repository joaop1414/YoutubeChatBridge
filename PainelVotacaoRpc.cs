using System;
using System.Linq;
using System.Text;
using Hazel;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Painel desenhado com IMGUI (GUI.Box/GUI.Label), seguindo a abordagem usada
// em overlays de outros mods, em vez de depender de texto no mundo do jogo.
internal static class PainelVotacaoRpc
{
    private const byte RpcPainelVotacao = 250;
    private static string[] eventos = Array.Empty<string>();
    private static int[] votos = new int[4];
    private static int rodada;
    private static int segundos;
    private static bool ativo;
    private static bool votacaoLobby;
    private static int tempoConfig = 40;
    private static int intervaloConfig = 15;
    private static int preparacaoConfig = 10;
    private static bool configuracaoRecebida;
    private static float proximoEnvio;
    private static float proximaSincronizacaoConfiguracoes;
    private static bool overlayCriado;

    internal static bool Ativo => ativo;
    internal static bool VotacaoLobby => votacaoLobby;
    internal static int Rodada => rodada;
    internal static int Segundos => segundos;
    internal static string[] Eventos => eventos;
    internal static int[] Votos => votos;

    public static void Inicializar()
    {
        try
        {
            if (YouTubeVoteBridgePlugin.Instance == null)
                throw new InvalidOperationException("Instância do plugin ainda não está disponível.");

            if (!overlayCriado)
            {
                // BasePlugin.AddComponent registra o tipo IL2CPP quando necessário
                // e adiciona o componente ao objeto persistente gerenciado pelo BepInEx.
                YouTubeVoteBridgePlugin.Instance.AddComponent<PainelVotacaoOverlay>();
                overlayCriado = true;
            }

            YouTubeVoteBridgePlugin.Instance.Log.LogInfo(
                "Componente do painel IMGUI anexado ao gerenciador persistente do BepInEx.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao inicializar painel IMGUI: " + ex);
        }
    }

    public static void Publicar(int numeroRodada, bool rodadaAtiva, int tempo,
        string[] nomesEventos, int[] contagem, bool modoLobby = false)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null)
            return;

        rodada = numeroRodada;
        ativo = rodadaAtiva;
        votacaoLobby = modoLobby;
        segundos = Math.Max(0, tempo);
        eventos = nomesEventos ?? Array.Empty<string>();
        votos = contagem ?? new int[4];

        if (Time.realtimeSinceStartup < proximoEnvio) return;
        proximoEnvio = Time.realtimeSinceStartup + 1f;
        tempoConfig = YouTubeVoteBridgePlugin.TempoVotacao?.Value ?? 40;
        intervaloConfig = YouTubeVoteBridgePlugin.IntervaloRodadas?.Value ?? 15;
        preparacaoConfig = YouTubeVoteBridgePlugin.PreparacaoChat?.Value ?? 10;
        configuracaoRecebida = true;
        EnviarEstadoParaClientes();
    }

    public static void SincronizarConfiguracoes()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            Time.realtimeSinceStartup < proximaSincronizacaoConfiguracoes)
            return;

        proximaSincronizacaoConfiguracoes = Time.realtimeSinceStartup + 2f;
        tempoConfig = YouTubeVoteBridgePlugin.TempoVotacao?.Value ?? 40;
        intervaloConfig = YouTubeVoteBridgePlugin.IntervaloRodadas?.Value ?? 15;
        preparacaoConfig = YouTubeVoteBridgePlugin.PreparacaoChat?.Value ?? 10;
        configuracaoRecebida = true;
        EnviarEstadoParaClientes();
    }

    private static void EnviarEstadoParaClientes()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null || PlayerControl.AllPlayerControls == null)
            return;

        // O painel usa RPC personalizado; envie-o individualmente apenas aos clientes
        // que confirmaram ter o mod. Assim, jogadores vanilla não recebem o RPC 250.
        try
        {
            string listaEventos = string.Join(",", eventos.Select(x =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes(x ?? ""))));
            string listaVotos = string.Join(",", votos.Select(x => Math.Max(0, x).ToString()));
            string payload = string.Join("|", new[]
            {
                rodada.ToString(), ativo ? "1" : "0", segundos.ToString(),
                votacaoLobby ? "1" : "0", listaEventos, listaVotos,
                tempoConfig.ToString(), intervaloConfig.ToString(), preparacaoConfig.ToString()
            });

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected ||
                    jogador.OwnerId < 0 ||
                    jogador.OwnerId == AmongUsClient.Instance.ClientId ||
                    !YouTubeVoteBridgePlugin.ClienteTemMod(jogador.OwnerId))
                    continue;

                MessageWriter writer = null;
                try
                {
                    writer = MessageWriter.Get(SendOption.Reliable);
                    writer.StartMessage(6); // GameDataTo
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(jogador.OwnerId);

                    writer.StartMessage(2); // RPC direcionado ao PlayerControl do host
                    writer.WritePacked(PlayerControl.LocalPlayer.NetId);
                    writer.Write(RpcPainelVotacao);
                    writer.Write(payload);
                    writer.EndMessage();

                    writer.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                }
                finally
                {
                    if (writer != null)
                    {
                        try { writer.Recycle(); } catch { }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao sincronizar painel com clientes modificados: " + ex.Message);
        }
    }

    public static string ObterTextoConfiguracoes()
    {
        // A consulta deve ler a configuração ativa do host diretamente.
        // Antes, dependia de configuracaoRecebida, que só era marcada após
        // Publicar/uma sincronização do painel; fora de uma votação, podia
        // responder que ainda não recebeu as configurações mesmo no próprio host.
        if (YouTubeVoteBridgePlugin.EhHost())
        {
            tempoConfig = YouTubeVoteBridgePlugin.TempoVotacao?.Value ?? 40;
            intervaloConfig = YouTubeVoteBridgePlugin.IntervaloRodadas?.Value ?? 15;
            preparacaoConfig = YouTubeVoteBridgePlugin.PreparacaoChat?.Value ?? 10;
            configuracaoRecebida = true;
        }

        if (!configuracaoRecebida)
            return "Não consegui obter as configurações do host. Peça ao host para usar /ytconfig.";

        return "Configurações do host: tempo de votação=" + tempoConfig + "s | intervalo entre rodadas=" +
            intervaloConfig + "s | preparação do chat=" + preparacaoConfig + "s.";
    }

    public static void Receber(string payload)
    {
        try
        {
            string[] p = payload.Split('|');
            if (p.Length != 9) return;
            rodada = int.Parse(p[0]);
            ativo = p[1] == "1";
            segundos = Math.Max(0, int.Parse(p[2]));
            votacaoLobby = p[3] == "1";
            eventos = p[4].Length == 0 ? Array.Empty<string>() :
                p[4].Split(',').Select(x =>
                    Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
            votos = p[5].Length == 0 ? new int[4] : p[5].Split(',').Select(int.Parse).ToArray();
            tempoConfig = int.Parse(p[6]);
            intervaloConfig = int.Parse(p[7]);
            preparacaoConfig = int.Parse(p[8]);
            configuracaoRecebida = true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Pacote do painel inválido: " + ex.Message);
        }
    }
}

internal sealed class PainelVotacaoOverlay : MonoBehaviour
{
    public PainelVotacaoOverlay(IntPtr ptr) : base(ptr) { }

    private bool diagnosticoDesenhoRegistrado;
    private bool posicaoManual;
    private bool arrastando;
    private Vector2 posicaoPainel;
    private Vector2 offsetArraste;
    private static Texture2D texturaPainel;
    private static Texture2D texturaLinha;
    private static Texture2D texturaBarra;
    private static GUIStyle estiloPainel;
    private static GUIStyle estiloTitulo;
    private static GUIStyle estiloSubtitulo;
    private static GUIStyle estiloOpcao;
    private static GUIStyle estiloContagem;
    private static GUIStyle estiloTempo;
    private static GUIStyle estiloPequeno;
    private static GUIStyle estiloBarraFundo;
    private static GUIStyle estiloBarra;
    private static GUIStyle estiloSeparador;
    private static bool estilosCriados;
    private static readonly Color[] Cores =
    {
        new Color(0.20f, 0.78f, 1.00f),
        new Color(0.70f, 0.55f, 1.00f),
        new Color(0.28f, 0.90f, 0.62f),
        new Color(1.00f, 0.68f, 0.30f)
    };

    private static Texture2D CriarTextura(Color cor)
    {
        var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        tex.SetPixel(0, 0, cor);
        tex.Apply();
        return tex;
    }

    private static GUIStyle EstiloComFundo(Texture2D fundo)
    {
        var s = new GUIStyle(GUI.skin.box);
        s.normal.background = fundo;
        s.border = new RectOffset();
        s.border.left = 8;
        s.border.right = 8;
        s.border.top = 8;
        s.border.bottom = 8;
        return s;
    }

    private static void CriarEstilos()
    {
        if (estilosCriados || Event.current == null) return;

        texturaPainel = CriarTextura(new Color(0.035f, 0.055f, 0.09f, 0.96f));
        texturaLinha = CriarTextura(new Color(0.20f, 0.29f, 0.40f, 1f));
        texturaBarra = CriarTextura(new Color(0.14f, 0.20f, 0.29f, 1f));

        estiloPainel = EstiloComFundo(texturaPainel);
        estiloBarraFundo = EstiloComFundo(texturaBarra);
        estiloBarra = EstiloComFundo(CriarTextura(Color.white));
        estiloSeparador = EstiloComFundo(texturaLinha);

        estiloTitulo = new GUIStyle(GUI.skin.label)
        {
            fontSize = 21, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
            normal = { textColor = Color.white }
        };
        estiloSubtitulo = new GUIStyle(GUI.skin.label)
        {
            fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
            normal = { textColor = new Color(0.60f, 0.70f, 0.82f) }
        };
        estiloOpcao = new GUIStyle(GUI.skin.label)
        {
            fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Overflow, wordWrap = true,
            normal = { textColor = Color.white }
        };
        estiloContagem = new GUIStyle(GUI.skin.label)
        {
            fontSize = 13, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleRight,
            normal = { textColor = Color.white }
        };
        estiloTempo = new GUIStyle(estiloContagem);
        estiloPequeno = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11, alignment = TextAnchor.MiddleLeft,
            normal = { textColor = new Color(0.65f, 0.73f, 0.83f) }
        };
        estilosCriados = true;
    }

    private void OnGUI()
    {
        if (AmongUsClient.Instance == null)
            return;

        if (!diagnosticoDesenhoRegistrado)
        {
            diagnosticoDesenhoRegistrado = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Overlay de votação recebeu OnGUI; resolução=" + Screen.width + "x" + Screen.height);
        }

        CriarEstilos();
        if (!estilosCriados) return;

        // 1280x720 é a escala de referência. Em janelas menores, reduzimos
        // proporcionalmente o painel e as fontes para caber nas três instâncias.
        float escala = Mathf.Clamp(
            Mathf.Min(Screen.width / 1280f, Screen.height / 720f), 0.40f, 1.0f);
        // Um pouco maior na resolução de referência, com espaço para nomes longos.
        float largura = 420f * escala;
        float altura = (PainelVotacaoRpc.Ativo && PainelVotacaoRpc.Eventos.Length > 0
            ? 180f + PainelVotacaoRpc.Eventos.Length * 58f
            : 160f) * escala;

        // GUIStyle guarda tamanhos absolutos; acompanhe a mesma escala do painel.
        estiloTitulo.fontSize = Mathf.Max(9, Mathf.RoundToInt(21f * escala));
        estiloSubtitulo.fontSize = Mathf.Max(7, Mathf.RoundToInt(12f * escala));
        estiloOpcao.fontSize = Mathf.Max(8, Mathf.RoundToInt(14f * escala));
        estiloContagem.fontSize = Mathf.Max(7, Mathf.RoundToInt(13f * escala));
        estiloTempo.fontSize = estiloContagem.fontSize;
        estiloPequeno.fontSize = Mathf.Max(6, Mathf.RoundToInt(11f * escala));
        // IMGUI usa coordenadas a partir do canto superior esquerdo. Deixe uma
        // margem fixa e limite a posição para o painel nunca sair da tela.
        float margemTela = 48f * escala;
        float x = Mathf.Max(8f * escala, Screen.width - largura - 22f * escala);
        float y = Mathf.Clamp(margemTela, 8f * escala,
            Mathf.Max(8f * escala, Screen.height - altura - 8f * escala));

        if (posicaoManual)
        {
            x = posicaoPainel.x;
            y = posicaoPainel.y;
        }

        // Mantém o painel inteiro visível mesmo após redimensionar a janela do jogo.
        x = Mathf.Clamp(x, 0f, Mathf.Max(0f, Screen.width - largura));
        y = Mathf.Clamp(y, 0f, Mathf.Max(0f, Screen.height - altura));
        posicaoPainel = new Vector2(x, y);

        // Arraste pela faixa do título "YOUTUBE VOTE".
        Event eventoGUI = Event.current;
        Rect areaArraste = new Rect(x, y, largura, 38f * escala);
        if (eventoGUI != null)
        {
            if (eventoGUI.type == EventType.MouseDown && eventoGUI.button == 0 &&
                areaArraste.Contains(eventoGUI.mousePosition))
            {
                arrastando = true;
                posicaoManual = true;
                offsetArraste = eventoGUI.mousePosition - posicaoPainel;
                eventoGUI.Use();
            }
            else if (eventoGUI.type == EventType.MouseDrag && arrastando)
            {
                posicaoPainel = eventoGUI.mousePosition - offsetArraste;
                posicaoPainel.x = Mathf.Clamp(posicaoPainel.x, 0f, Mathf.Max(0f, Screen.width - largura));
                posicaoPainel.y = Mathf.Clamp(posicaoPainel.y, 0f, Mathf.Max(0f, Screen.height - altura));
                eventoGUI.Use();
            }
            else if (eventoGUI.type == EventType.MouseUp && arrastando)
            {
                arrastando = false;
                eventoGUI.Use();
            }
        }

        x = posicaoPainel.x;
        y = posicaoPainel.y;
        var painel = new Rect(x, y, largura, altura);

        // Use os fundos nativos da skin do Unity e apenas tinja a caixa.
        // A atribuição de Texture2D a GUIStyleState.background não é confiável
        // em algumas combinações de Unity/IL2CPP do Among Us.
        GUI.color = new Color(0.12f, 0.16f, 0.23f, 0.98f);
        GUI.Box(painel, GUIContent.none, GUI.skin.box);
        GUI.color = Color.white;

        float margem = 16f * escala;
        float dentroX = x + margem;
        float larguraInterna = largura - margem * 2f;
        float atualY = y + 12f * escala;

        GUI.Label(new Rect(dentroX, atualY, larguraInterna, 30f * escala),
            "YOUTUBE VOTE", estiloTitulo);
        atualY += 30f * escala;

        if (!PainelVotacaoRpc.Ativo || PainelVotacaoRpc.Eventos.Length == 0)
        {
            GUI.Label(new Rect(dentroX, atualY, larguraInterna, 22f * escala),
                "PAINEL DE VOTAÇÃO", estiloSubtitulo);
            atualY += 26f * escala;
            GUI.Label(new Rect(dentroX, atualY, larguraInterna, 34f * escala),
                "Aguardando a próxima votação...", estiloOpcao);
            GUI.Label(new Rect(dentroX, atualY + 36f * escala, larguraInterna, 24f * escala),
                "A votação começa automaticamente quando a live estiver ativa.", estiloPequeno);
            return;
        }

        GUI.Label(new Rect(dentroX, atualY, larguraInterna, 20f * escala),
            PainelVotacaoRpc.VotacaoLobby
                ? $"F7 • VOTAÇÃO NO LOBBY  #{PainelVotacaoRpc.Rodada}"
                : $"F6 • VOTAÇÃO NA PARTIDA  #{PainelVotacaoRpc.Rodada}",
            estiloSubtitulo);
        atualY += 25f * escala;
        GUI.color = new Color(0.20f, 0.29f, 0.40f, 1f);
        GUI.Box(new Rect(dentroX, atualY, larguraInterna, 2f * escala),
            GUIContent.none, GUI.skin.box);
        GUI.color = Color.white;
        atualY += 10f * escala;

        var eventos = PainelVotacaoRpc.Eventos;
        var votos = PainelVotacaoRpc.Votos;
        int maior = 1;
        int total = 0;
        for (int i = 0; i < eventos.Length; i++)
        {
            int n = i < votos.Length ? Math.Max(0, votos[i]) : 0;
            maior = Math.Max(maior, n);
            total += n;
        }

        for (int i = 0; i < eventos.Length; i++)
        {
            int n = i < votos.Length ? Math.Max(0, votos[i]) : 0;
            float linhaAltura = 54f * escala;

            GUI.color = Cores[i % Cores.Length];
            GUI.Box(new Rect(dentroX, atualY + 3f * escala, 25f * escala, 25f * escala),
                (i + 1).ToString(), GUI.skin.box);
            GUI.color = Color.white;

            GUI.Label(new Rect(dentroX + 33f * escala, atualY, larguraInterna - 105f * escala,
                38f * escala), eventos[i], estiloOpcao);
            GUI.Label(new Rect(x + largura - margem - 76f * escala, atualY, 76f * escala,
                28f * escala), n + (n == 1 ? " voto" : " votos"), estiloContagem);

            float barraX = dentroX + 33f * escala;
            float barraY = atualY + 40f * escala;
            float barraLargura = larguraInterna - 33f * escala;
            GUI.color = new Color(0.14f, 0.20f, 0.29f, 1f);
            GUI.Box(new Rect(barraX, barraY, barraLargura, 7f * escala),
                GUIContent.none, GUI.skin.box);
            GUI.color = Color.white;
            if (n > 0)
            {
                GUI.color = Cores[i % Cores.Length];
                GUI.Box(new Rect(barraX, barraY,
                    barraLargura * n / maior, 7f * escala),
                    GUIContent.none, GUI.skin.box);
                GUI.color = Color.white;
            }

            atualY += linhaAltura;
        }

        atualY += 2f * escala;
        GUI.Box(new Rect(dentroX, atualY, larguraInterna, 2f * escala),
            GUIContent.none, estiloSeparador);
        atualY += 9f * escala;

        GUI.Label(new Rect(dentroX, atualY, larguraInterna * 0.58f, 22f * escala),
            $"TOTAL DE VOTOS: {total}", estiloSubtitulo);
        estiloTempo.normal.textColor = PainelVotacaoRpc.Segundos <= 5 ? new Color(1f, 0.35f, 0.35f) :
            PainelVotacaoRpc.Segundos <= 10 ? new Color(1f, 0.82f, 0.35f) :
            new Color(0.39f, 0.90f, 0.65f);
        GUI.Label(new Rect(dentroX + larguraInterna * 0.58f, atualY, larguraInterna * 0.42f,
            22f * escala), $"TEMPO: {PainelVotacaoRpc.Segundos}s", estiloTempo);
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class PainelVotacaoRpcPatch
{
    private const byte RpcPainelVotacao = 250;

    public static bool Prefix(PlayerControl __instance, byte callId, MessageReader reader)
    {
        if (callId == 248)
        {
            if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost &&
                __instance != null)
                YouTubeVoteBridgePlugin.RegistrarClienteComMod(__instance.OwnerId);
            return false;
        }

        if (callId != RpcPainelVotacao) return true;
        try
        {
            PainelVotacaoRpc.Receber(reader.ReadString());
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Erro ao receber RPC do painel: " + ex.Message);
        }
        return false;
    }
}

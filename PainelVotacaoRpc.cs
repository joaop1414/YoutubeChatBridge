using System;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime.Injection;
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
    private static int tempoConfig = 40;
    private static int intervaloConfig = 15;
    private static int preparacaoConfig = 10;
    private static bool configuracaoRecebida;
    private static float proximoEnvio;
    private static float proximaSincronizacaoConfiguracoes;
    private static bool tipoRegistrado;
    private static bool overlayCriado;

    internal static bool Ativo => ativo;
    internal static int Rodada => rodada;
    internal static int Segundos => segundos;
    internal static string[] Eventos => eventos;
    internal static int[] Votos => votos;

    public static void Inicializar()
    {
        try
        {
            if (!tipoRegistrado)
            {
                ClassInjector.RegisterTypeInIl2Cpp<PainelVotacaoOverlay>();
                tipoRegistrado = true;
            }

            if (!overlayCriado)
            {
                var objeto = new GameObject("YouTubeVoteBridge_VoteOverlay");
                UnityEngine.Object.DontDestroyOnLoad(objeto);
                objeto.AddComponent<PainelVotacaoOverlay>();
                overlayCriado = true;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Painel de votação IMGUI inicializado.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao inicializar painel IMGUI: " + ex);
        }
    }

    public static void Publicar(int numeroRodada, bool rodadaAtiva, int tempo,
        string[] nomesEventos, int[] contagem)
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null)
            return;

        rodada = numeroRodada;
        ativo = rodadaAtiva;
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
            PlayerControl.LocalPlayer == null)
            return;

        try
        {
            string listaEventos = string.Join(",", eventos.Select(x =>
                Convert.ToBase64String(Encoding.UTF8.GetBytes(x ?? ""))));
            string listaVotos = string.Join(",", votos.Select(x => Math.Max(0, x).ToString()));
            string payload = string.Join("|", new[]
            {
                rodada.ToString(), ativo ? "1" : "0", segundos.ToString(),
                listaEventos, listaVotos, tempoConfig.ToString(),
                intervaloConfig.ToString(), preparacaoConfig.ToString()
            });

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId, RpcPainelVotacao,
                SendOption.Reliable, -1);
            writer.Write(payload);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao sincronizar painel com clientes: " + ex.Message);
        }
    }

    public static string ObterTextoConfiguracoes()
    {
        if (!configuracaoRecebida)
            return "Ainda não recebi as configurações do host. Aguarde alguns segundos e tente /youtube config novamente.";

        return "Configurações atuais: tempo=" + tempoConfig + "s | intervalo=" +
            intervaloConfig + "s | preparação=" + preparacaoConfig + "s.";
    }

    public static void Receber(string payload)
    {
        try
        {
            string[] p = payload.Split('|');
            if (p.Length != 8) return;
            rodada = int.Parse(p[0]);
            ativo = p[1] == "1";
            segundos = Math.Max(0, int.Parse(p[2]));
            eventos = p[3].Length == 0 ? Array.Empty<string>() :
                p[3].Split(',').Select(x =>
                    Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
            votos = p[4].Length == 0 ? new int[4] : p[4].Split(',').Select(int.Parse).ToArray();
            tempoConfig = int.Parse(p[5]);
            intervaloConfig = int.Parse(p[6]);
            preparacaoConfig = int.Parse(p[7]);
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
        s.border = new RectOffset(8, 8, 8, 8);
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
            clipping = TextClipping.Clip, normal = { textColor = Color.white }
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
        if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null)
            return;

        CriarEstilos();
        if (!estilosCriados) return;

        float escala = Mathf.Clamp(Screen.width / 1600f, 0.72f, 1.15f);
        float largura = 340f * escala;
        float altura = (PainelVotacaoRpc.Ativo && PainelVotacaoRpc.Eventos.Length > 0
            ? 245f + PainelVotacaoRpc.Eventos.Length * 48f
            : 142f) * escala;
        float x = Screen.width - largura - 22f * escala;
        float y = Mathf.Max(18f, Screen.height * 0.18f);
        var painel = new Rect(x, y, largura, altura);

        GUI.Box(painel, GUIContent.none, estiloPainel);

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
            $"RODADA #{PainelVotacaoRpc.Rodada}     ● AO VIVO", estiloSubtitulo);
        atualY += 25f * escala;
        GUI.Box(new Rect(dentroX, atualY, larguraInterna, 2f * escala), GUIContent.none,
            estiloSeparador);
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
            float linhaAltura = 43f * escala;

            GUI.color = Cores[i % Cores.Length];
            GUI.Box(new Rect(dentroX, atualY + 3f * escala, 25f * escala, 25f * escala),
                (i + 1).ToString(), estiloBarra);
            GUI.color = Color.white;

            GUI.Label(new Rect(dentroX + 33f * escala, atualY, larguraInterna - 105f * escala,
                28f * escala), eventos[i], estiloOpcao);
            GUI.Label(new Rect(x + largura - margem - 76f * escala, atualY, 76f * escala,
                28f * escala), n + (n == 1 ? " voto" : " votos"), estiloContagem);

            float barraX = dentroX + 33f * escala;
            float barraY = atualY + 29f * escala;
            float barraLargura = larguraInterna - 33f * escala;
            GUI.Box(new Rect(barraX, barraY, barraLargura, 7f * escala),
                GUIContent.none, estiloBarraFundo);
            if (n > 0)
            {
                GUI.color = Cores[i % Cores.Length];
                GUI.Box(new Rect(barraX, barraY, barraLargura * n / maior, 7f * escala),
                    GUIContent.none, estiloBarra);
                GUI.color = Color.white;
            }

            atualY += linhaAltura;
        }

        atualY += 2f * escala;
        GUI.Box(new Rect(dentroX, atualY, larguraInterna, 2f * escala), GUIContent.none,
            EstiloComFundo(texturaLinha));
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

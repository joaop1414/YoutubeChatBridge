using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Hazel;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

internal static class PainelVotacaoRpc
{
    private const byte RpcPainelVotacao = 250;
    private static TextMeshPro textoPainel;
    private static GameObject fundoPainel;
    private static Sprite spriteFundo;
    private static Texture2D texturaFundo;
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
    private static float proximaTentativaCriarTexto;

    // Dimensões em unidades do mundo do HUD.
    private const float LARGURA_PAINEL = 6.2f;
    private const float ALTURA_FUNDO = 4.5f;
    private static readonly Vector3 POSICAO_PAINEL = new(3.0f, 0.0f, 0f);

    public static void Inicializar()
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Painel de votação: inicialização concluída; aguardando HudManager.");
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
        AtualizarTexto();
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

    private static void AtualizarTexto()
    {
        if (!textoPainel) return;

        if (!ativo || eventos.Length == 0)
        {
            textoPainel.text =
                "<b><color=#39BFFF>YouTubeVoteBridge</color></b>\n" +
                "<color=#FFD966>PAINEL DE VOTAÇÃO</color>\n" +
                "<color=#FFFFFF>Estado:</color> aguardando votação\n" +
                "<size=70%>Inicie a live e a votação com /youtube.</size>";
            return;
        }

        StringBuilder sb = new();
        sb.AppendLine("<b><color=#39BFFF>YouTubeVoteBridge</color></b>");
        sb.AppendLine("<color=#7CFF8A>VOTAÇÃO ATIVA</color>  |  Rodada #" + rodada);

        for (int i = 0; i < eventos.Length; i++)
        {
            int quantidade = i < votos.Length ? votos[i] : 0;
            sb.Append("<color=#FFD966>");
            sb.Append(i + 1);
            sb.Append(".</color> ");
            sb.Append(eventos[i]);
            sb.Append("  <color=#7CFF8A>(");
            sb.Append(quantidade);
            sb.AppendLine(" votos)</color>");
        }

        sb.AppendLine();
        sb.Append("<color=#FFFFFF>Tempo restante: <b>");
        sb.Append(segundos);
        sb.AppendLine("s</b></color>");
        textoPainel.text = sb.ToString();
    }

    private static void CriarFundo(Transform pai)
    {
        // Fundo preto semitransparente atrás do título, dos eventos e dos votos.
        // Isso evita depender de um Canvas adicional dentro do HUD do jogo.
        texturaFundo = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texturaFundo.name = "YouTubeVoteBridge_FundoTexture";
        texturaFundo.SetPixel(0, 0, Color.white);
        texturaFundo.Apply();

        spriteFundo = Sprite.Create(
            texturaFundo, new Rect(0, 0, 1, 1), new Vector2(0.5f, 0.5f), 1f);
        spriteFundo.name = "YouTubeVoteBridge_FundoSprite";

        fundoPainel = new GameObject("YouTubeVoteBridge_VotePanelBackground");
        fundoPainel.transform.SetParent(pai, false);
        fundoPainel.transform.localPosition =
            POSICAO_PAINEL + new Vector3(0.04f, -0.12f, 0.12f);
        fundoPainel.transform.localScale =
            new Vector3(LARGURA_PAINEL, ALTURA_FUNDO, 1f);

        SpriteRenderer renderer = fundoPainel.AddComponent<SpriteRenderer>();
        renderer.sprite = spriteFundo;
        renderer.color = new Color(0f, 0f, 0f, 0.78f);
        renderer.sortingOrder = -10;
    }

    public static void GarantirInterface(HudManager hud)
    {
        if (!hud || !hud.KillButton || !hud.KillButton.cooldownTimerText)
            return;

        if (!textoPainel)
        {
            if (Time.realtimeSinceStartup < proximaTentativaCriarTexto) return;
            proximaTentativaCriarTexto = Time.realtimeSinceStartup + 2f;

            try
            {
                CriarFundo(hud.transform);

                textoPainel = UnityEngine.Object.Instantiate(
                    hud.KillButton.cooldownTimerText,
                    hud.transform,
                    true
                );

                textoPainel.gameObject.name = "YouTubeVoteBridge_VotePanel";
                textoPainel.alignment = TextAlignmentOptions.TopLeft;
                textoPainel.verticalAlignment = VerticalAlignmentOptions.Top;
                textoPainel.transform.localPosition = POSICAO_PAINEL;
                textoPainel.transform.localScale = Vector3.one;
                textoPainel.rectTransform.sizeDelta = new Vector2(LARGURA_PAINEL, ALTURA_FUNDO);
                textoPainel.fontSize = 1.25f;
                textoPainel.fontSizeMin = 1.25f;
                textoPainel.fontSizeMax = 1.25f;
                textoPainel.enableWordWrapping = true;
                textoPainel.overflowMode = TextOverflowModes.Overflow;
                textoPainel.color = Color.white;
                textoPainel.enabled = true;

                if (textoPainel.GetComponent<MeshRenderer>())
                    textoPainel.GetComponent<MeshRenderer>().sortingOrder = 0;

                AtualizarTexto();
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Painel de eventos criado com fundo preto semitransparente (alpha 0.78).");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Não foi possível criar o painel em HudManager: " + ex);
            }
        }
        else
        {
            if (textoPainel.transform.parent != hud.transform)
                textoPainel.transform.SetParent(hud.transform, false);

            if (fundoPainel && fundoPainel.transform.parent != hud.transform)
                fundoPainel.transform.SetParent(hud.transform, false);

            textoPainel.transform.localPosition = POSICAO_PAINEL;
            textoPainel.rectTransform.sizeDelta = new Vector2(LARGURA_PAINEL, ALTURA_FUNDO);
            if (fundoPainel)
            {
                fundoPainel.transform.localPosition =
                    POSICAO_PAINEL + new Vector3(0.04f, -0.12f, 0.12f);
                fundoPainel.transform.localScale =
                    new Vector3(LARGURA_PAINEL, ALTURA_FUNDO, 1f);
            }

            textoPainel.enabled = true;
            AtualizarTexto();
        }
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
            AtualizarTexto();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Pacote do painel inválido: " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class PainelVotacaoHudPatch
{
    public static void Postfix(HudManager __instance)
    {
        try
        {
            if (AmongUsClient.Instance == null || PlayerControl.LocalPlayer == null)
                return;
            if (AmongUsClient.Instance.AmHost)
                PainelVotacaoRpc.SincronizarConfiguracoes();
            PainelVotacaoRpc.GarantirInterface(__instance);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Erro ao atualizar a interface do painel: " + ex.Message);
        }
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

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
    // Estado local do painel. Por enquanto, a interface é exibida no host;
    // sincronização seletiva com clientes modificados será adicionada depois.
    private const byte RpcPainelVotacao = 250;
    private static TextMeshPro textoPainel;
    private static string[] eventos = Array.Empty<string>();
    private static int[] votos = new int[4];
    private static int rodada;
    private static int segundos;
    private static bool ativo;
    private static float proximoEnvio;
    private static float proximaTentativaCriarTexto;

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

        if (Time.realtimeSinceStartup < proximoEnvio && rodadaAtiva) return;
        proximoEnvio = Time.realtimeSinceStartup + 1f;

        // Não transmitir o RPC customizado a todos os jogadores ainda:
        // clientes sem o mod não conhecem o identificador 250.
        AtualizarTexto();
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
                textoPainel = UnityEngine.Object.Instantiate(
                    hud.KillButton.cooldownTimerText,
                    hud.transform,
                    true
                );

                textoPainel.gameObject.name = "YouTubeVoteBridge_VotePanel";
                textoPainel.alignment = TextAlignmentOptions.TopLeft;
                textoPainel.verticalAlignment = VerticalAlignmentOptions.Top;
                textoPainel.transform.localPosition = new Vector3(3.0f, 2.25f, 0f);
                textoPainel.transform.localScale = Vector3.one;
                textoPainel.fontSize = 1.25f;
                textoPainel.fontSizeMin = 1.25f;
                textoPainel.fontSizeMax = 1.25f;
                textoPainel.enableWordWrapping = true;
                textoPainel.overflowMode = TextOverflowModes.Overflow;
                textoPainel.color = Color.white;
                textoPainel.enabled = true;

                AtualizarTexto();
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Painel de votação criado na interface HudManager.");
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

            textoPainel.enabled = true;
            AtualizarTexto();
        }
    }

    public static void Receber(string payload)
    {
        // Reservado para a futura sincronização entre clientes modificados.
        try
        {
            string[] p = payload.Split('|');
            if (p.Length != 5) return;
            rodada = int.Parse(p[0]);
            ativo = p[1] == "1";
            segundos = Math.Max(0, int.Parse(p[2]));
            eventos = p[3].Length == 0 ? Array.Empty<string>() :
                p[3].Split(',').Select(x =>
                    Encoding.UTF8.GetString(Convert.FromBase64String(x))).ToArray();
            votos = p[4].Split(',').Select(int.Parse).ToArray();
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
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
                return;

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

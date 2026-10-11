using System;
using System.Linq;
using System.Text;
using HarmonyLib;
using Hazel;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace YouTubeVoteBridge;

internal static class PainelVotacaoRpc
{
    // ID customizado deste mod. Todos os clientes com o mod precisam usar
    // uma versão compatível para interpretar este pacote.
    private const byte RpcPainelVotacao = 250;
    private static GameObject objetoPainel;
    private static bool inicializado;
    private static string[] eventos = Array.Empty<string>();
    private static int[] votos = new int[4];
    private static int rodada;
    private static int segundos;
    private static bool ativo;
    private static float proximoEnvio;

    public static void Inicializar()
    {
        if (inicializado) return;
        inicializado = true;
        ClassInjector.RegisterTypeInIl2Cpp<PainelVotacaoBehaviour>();
        objetoPainel = new GameObject("YouTubeVoteBridge_PainelVotacao");
        UnityEngine.Object.DontDestroyOnLoad(objetoPainel);
        objetoPainel.AddComponent<PainelVotacaoBehaviour>();
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

        // Estado completo enviado a cada segundo durante a votação.
        if (Time.realtimeSinceStartup < proximoEnvio && rodadaAtiva) return;
        proximoEnvio = Time.realtimeSinceStartup + 1f;

        try
        {
            string payload = Codificar(rodada, ativo, segundos, eventos, votos);
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                RpcPainelVotacao,
                SendOption.Reliable,
                -1
            );
            writer.Write(payload);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            Aplicar(payload);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao sincronizar painel por RPC: " + ex);
        }
    }

    private static string Codificar(int n, bool ligado, int tempo,
        string[] nomes, int[] quantidades)
    {
        string listaEventos = string.Join(",", (nomes ?? Array.Empty<string>())
            .Select(x => Convert.ToBase64String(Encoding.UTF8.GetBytes(x ?? ""))));
        string listaVotos = string.Join(",", Enumerable.Range(0, 4)
            .Select(i => i < (quantidades?.Length ?? 0) ? quantidades[i] : 0));
        return n + "|" + (ligado ? "1" : "0") + "|" + tempo + "|" +
            listaEventos + "|" + listaVotos;
    }

    private static void Aplicar(string payload)
    {
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
            votos = p[4].Split(',').Select(x => int.Parse(x)).ToArray();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Pacote RPC do painel inválido: " + ex.Message);
        }
    }

    public static void Receber(string payload) => Aplicar(payload);

    public static void Desenhar()
    {
        if (!ativo || eventos.Length == 0) return;

        const float largura = 330f;
        float altura = 92f + eventos.Length * 43f;
        float x = Screen.width - largura - 18f;
        float y = Mathf.Max(18f, Screen.height * 0.18f);

        GUI.depth = -100;
        GUI.Box(new Rect(x, y, largura, altura), "");
        GUI.Label(new Rect(x + 12, y + 8, largura - 24, 24),
            "VOTAÇÃO DO YOUTUBE  |  Rodada #" + rodada);
        for (int i = 0; i < eventos.Length; i++)
        {
            int quantidade = i < votos.Length ? votos[i] : 0;
            GUI.Label(new Rect(x + 12, y + 36 + i * 43, largura - 24, 40),
                (i + 1) + ". " + eventos[i] + "\n   Votos: " + quantidade);
        }
        GUI.Label(new Rect(x + 12, y + altura - 28, largura - 24, 22),
            "Tempo restante: " + segundos + "s");
    }
}

public sealed class PainelVotacaoBehaviour : MonoBehaviour
{
    public PainelVotacaoBehaviour(IntPtr ptr) : base(ptr) { }
    private void OnGUI() => PainelVotacaoRpc.Desenhar();
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

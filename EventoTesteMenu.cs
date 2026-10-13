using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTesteMenu : MonoBehaviour
{
    private static bool registrado;
    private static bool aberto;
    private static Vector2 scroll;
    private static string mensagem = "Pressione F6 para abrir/fechar.";
    private static float mensagemAte;

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
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Menu de teste] Inicializado. Tecla de abertura: F6.");
    }

    public static void VerificarTecla()
    {
        if (!Input.GetKeyDown(KeyCode.F6)) return;

        aberto = !aberto;
        mensagem = aberto
            ? "Selecione um evento para executá-lo imediatamente."
            : "";
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Menu de teste] F6 detectado. Menu " + (aberto ? "aberto." : "fechado."));
    }

    public void Update()
    {
        if (mensagemAte > 0f && Time.unscaledTime > mensagemAte)
        {
            mensagem = "";
            mensagemAte = 0f;
        }
    }

    public void OnGUI()
    {
        // O menu também pode ser aberto no lobby; só a execução dos eventos
        // exige que a partida já tenha jogadores carregados.
        if (!aberto)
            return;

        float largura = Mathf.Min(620f, Screen.width - 30f);
        float altura = Mathf.Min(620f, Screen.height - 30f);
        Rect janela = new Rect(
            (Screen.width - largura) / 2f,
            (Screen.height - altura) / 2f,
            largura,
            altura
        );

        GUI.Box(janela, "YouTubeVoteBridge — Teste de eventos (F6 fecha)");
        GUILayout.BeginArea(new Rect(janela.x + 14f, janela.y + 34f, janela.width - 28f, janela.height - 48f));
        GUILayout.Label(YouTubeVoteBridgePlugin.EhHost()
            ? "Host detectado: os eventos serão executados localmente pelo host."
            : "Você não é o host. Os eventos que exigem autoridade não serão executados.");
        GUILayout.Space(6f);

        scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
        IReadOnlyList<EventoVotacao> eventos = RegistroEventos.ObterTodos();
        for (int i = 0; i < eventos.Count; i++)
        {
            EventoVotacao evento = eventos[i];
            if (evento == null) continue;

            GUILayout.BeginHorizontal("box");
            GUILayout.Label(evento.Nome, GUILayout.ExpandWidth(true));
            if (GUILayout.Button("Executar", GUILayout.Width(105f)))
                ExecutarEvento(evento);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        if (!string.IsNullOrEmpty(mensagem))
        {
            GUILayout.Space(5f);
            GUILayout.Label(mensagem);
        }

        GUILayout.Space(5f);
        if (GUILayout.Button("Fechar menu (F6)", GUILayout.Height(30f)))
            aberto = false;

        GUILayout.EndArea();
    }

    private static void ExecutarEvento(EventoVotacao evento)
    {
        if (evento == null) return;

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
                null,
                Type.EmptyTypes,
                null
            );

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
                null,
                Type.EmptyTypes,
                null
            );

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
        mensagem = texto;
        mensagemAte = Time.unscaledTime + 5f;
    }
}

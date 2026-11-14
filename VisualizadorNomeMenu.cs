using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Configurador de funções por jogador no lobby. As escolhas são aplicadas
/// somente na seleção inicial; jogadores em Automático continuam sendo
/// selecionados pelo RoleManager vanilla.
/// </summary>
internal sealed class ConfiguradorFuncoesMenu : MonoBehaviour
{
    public ConfiguradorFuncoesMenu(IntPtr ptr) : base(ptr) { }

    private static bool componenteCriado;
    private static bool painelAberto;
    private static byte? jogadorSelecionado;
    private static Vector2 scrollJogadores;
    private static Vector2 scrollFuncoes;
    private static readonly Dictionary<byte, RoleTypes> atribuicoes = new();
    private static readonly RoleTypes[] funcoesDisponiveis = Enum.GetValues(typeof(RoleTypes))
        .Cast<RoleTypes>()
        .Where(x => !string.Equals(x.ToString(), "None", StringComparison.OrdinalIgnoreCase))
        .Distinct()
        .OrderBy(x => x.ToString())
        .ToArray();

    public static void Inicializar()
    {
        try
        {
            if (componenteCriado)
                return;

            if (YouTubeVoteBridgePlugin.Instance == null)
                throw new InvalidOperationException("Instância do plugin ainda não está disponível.");

            // Usa o mesmo mecanismo do painel de votação, que já recebe OnGUI
            // corretamente no objeto persistente gerenciado pelo BepInEx.
            YouTubeVoteBridgePlugin.Instance.AddComponent<ConfiguradorFuncoesMenu>();
            componenteCriado = true;

            YouTubeVoteBridgePlugin.Instance.Log.LogInfo(
                "[Configurador de funções] Componente IMGUI anexado ao gerenciador persistente; pressione F8 no lobby.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurador de funções] Falha na inicialização: " + ex);
        }
    }

    /// <summary>
    /// Entrega as escolhas ao RoleSwitchAPI uma única vez, no começo da partida.
    /// </summary>
    internal static Dictionary<byte, RoleTypes> ConsumirConfiguracoes()
    {
        Dictionary<byte, RoleTypes> copia = new(atribuicoes);
        atribuicoes.Clear();
        jogadorSelecionado = null;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Configurador de funções] {copia.Count} configuração(ões) entregue(s) à seleção inicial.");
        return copia;
    }

    public static void VerificarTecla()
    {
        if (!Input.GetKeyDown(KeyCode.F8))
            return;

        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost ||
            string.Equals(cliente.GameState.ToString(), "Started", StringComparison.OrdinalIgnoreCase))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Configurador de funções] F8 disponível somente para o host no lobby.");
            return;
        }

        painelAberto = !painelAberto;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            painelAberto
                ? "[Configurador de funções] Painel aberto."
                : "[Configurador de funções] Painel fechado.");
    }

    private void OnGUI()
    {
        if (!painelAberto)
            return;

        try
        {
            float largura = Mathf.Min(760f, Screen.width - 30f);
            float altura = Mathf.Min(650f, Screen.height - 30f);
            Rect janela = new Rect(
                (Screen.width - largura) / 2f,
                (Screen.height - altura) / 2f,
                largura,
                altura);

            GUI.depth = -200;
            GUI.Box(janela, "YouTubeChatBridge — Funções por jogador (F8)");
            GUILayout.BeginArea(new Rect(
                janela.x + 14f, janela.y + 32f,
                janela.width - 28f, janela.height - 46f));

            GUILayout.Label("Escolha um jogador e defina sua função. Quem ficar em Automático será sorteado pelo jogo.");
            GUILayout.Label("As escolhas são aplicadas pelo host durante a seleção inicial, antes da revelação.");

            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(largura * 0.43f));
            GUILayout.Label("JOGADORES DA SALA");
            scrollJogadores = GUILayout.BeginScrollView(scrollJogadores, GUILayout.Height(altura - 170f));

            int encontrados = 0;
            try
            {
                if (PlayerControl.AllPlayerControls != null)
                {
                    foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
                    {
                        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                            continue;

                        encontrados++;
                        byte id = jogador.PlayerId;
                        string nome = jogador.Data.PlayerName ?? "Jogador";
                        string funcao = atribuicoes.TryGetValue(id, out RoleTypes definida)
                            ? definida.ToString()
                            : "Automático";
                        bool selecionado = jogadorSelecionado.HasValue && jogadorSelecionado.Value == id;

                        GUILayout.BeginHorizontal();
                        string rotulo = $"{nome}  [ID {id}]\n{funcao}";
                        if (GUILayout.Button(rotulo, GUILayout.Height(48f)))
                            jogadorSelecionado = id;
                        if (selecionado)
                            GUILayout.Label("◀", GUILayout.Width(24f));
                        GUILayout.EndHorizontal();
                    }
                }
            }
            catch (Exception ex)
            {
                GUILayout.Label("Não foi possível listar jogadores: " + ex.Message);
            }

            if (encontrados == 0)
                GUILayout.Label("Nenhum jogador encontrado. Abra o painel dentro do lobby.");

            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.Space(12f);
            GUILayout.BeginVertical();
            string nomeSelecionado = "Nenhum jogador selecionado";
            if (jogadorSelecionado.HasValue)
            {
                PlayerControl alvo = EncontrarJogador(jogadorSelecionado.Value);
                nomeSelecionado = alvo != null && alvo.Data != null
                    ? alvo.Data.PlayerName
                    : "PlayerId " + jogadorSelecionado.Value;
            }

            GUILayout.Label("FUNÇÃO PARA: " + nomeSelecionado);
            if (!jogadorSelecionado.HasValue)
            {
                GUILayout.Label("Selecione um jogador na lista ao lado.");
            }
            else
            {
                if (GUILayout.Button("Automático — deixar o jogo escolher", GUILayout.Height(34f)))
                {
                    atribuicoes.Remove(jogadorSelecionado.Value);
                }

                scrollFuncoes = GUILayout.BeginScrollView(scrollFuncoes, GUILayout.Height(altura - 220f));
                int colunas = 2;
                for (int i = 0; i < funcoesDisponiveis.Length; i += colunas)
                {
                    GUILayout.BeginHorizontal();
                    for (int c = 0; c < colunas && i + c < funcoesDisponiveis.Length; c++)
                    {
                        RoleTypes funcao = funcoesDisponiveis[i + c];
                        bool atual = atribuicoes.TryGetValue(jogadorSelecionado.Value, out RoleTypes definida)
                            && definida == funcao;
                        string texto = atual ? "✓ " + funcao : funcao.ToString();
                        if (GUILayout.Button(texto, GUILayout.Height(32f)))
                            atribuicoes[jogadorSelecionado.Value] = funcao;
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
            }
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(8f);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Configurações manuais: {atribuicoes.Count}", GUILayout.Width(210f));
            if (GUILayout.Button("Limpar todas", GUILayout.Height(30f)))
            {
                atribuicoes.Clear();
                jogadorSelecionado = null;
            }
            if (GUILayout.Button("Fechar (F8)", GUILayout.Height(30f)))
                painelAberto = false;
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurador de funções] Erro ao desenhar painel: " + ex);
        }
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        try
        {
            if (PlayerControl.AllPlayerControls == null)
                return null;

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador != null && jogador.PlayerId == playerId)
                    return jogador;
            }
        }
        catch { }
        return null;
    }
}

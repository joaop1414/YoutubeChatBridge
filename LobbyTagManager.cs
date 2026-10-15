using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BepInEx;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

public static class LobbyTagManager
{
    private const string UrlTagsRemotas =
        "https://raw.githubusercontent.com/joaop1414/YoutubeChatBridge/refs/heads/main/tags.json";
    private const string CorTagDono = "gold";
    private static readonly HttpClient ClienteHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static Task<string> DownloadTagsPendente;
    private static DateTime ProximaBuscaTagsUtc = DateTime.MinValue;
    private static bool AvisouFalhaTagsRemotas;

    private static readonly Dictionary<string, string> Tags =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<byte, string> NomesOriginais =
        new();
    private static readonly HashSet<byte> TagsAplicadas = new();
    private static string CaminhoTags;
    private static string CaminhoJogadores;
    private static readonly Dictionary<string, JogadorRegistrado> JogadoresRegistrados =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool AvisouEstadoDesconhecido;

    public sealed class JogadorRegistrado
    {
        public JogadorRegistrado() { }

        public string FriendCode { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string PrimeiroRegistroUtc { get; set; } = string.Empty;
        public string UltimoRegistroUtc { get; set; } = string.Empty;
    }

    public static void Initialize()
    {
        try
        {
            string pasta = Path.Combine(Paths.ConfigPath, "YouTubeVoteBridge");
            Directory.CreateDirectory(pasta);
            CaminhoTags = Path.Combine(pasta, "tags.json");
            CaminhoJogadores = Path.Combine(pasta, "players.json");

            if (File.Exists(CaminhoJogadores))
            {
                try
                {
                    var salvos = JsonSerializer.Deserialize<Dictionary<string, JogadorRegistrado>>(
                        File.ReadAllText(CaminhoJogadores));
                    if (salvos != null)
                    {
                        foreach (var item in salvos)
                            JogadoresRegistrados[NormalizarFriendCode(item.Key)] = item.Value;
                    }
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Não foi possível ler players.json: " + ex.Message);
                }
            }

            if (File.Exists(CaminhoTags))
            {
                try
                {
                    var salvas = JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(CaminhoTags));
                    if (salvas != null)
                    {
                        foreach (var item in salvas)
                            Tags[NormalizarFriendCode(item.Key)] = item.Value;
                    }
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Não foi possível ler tags.json; será usado o padrão: " + ex.Message);
                }
            }

            // O arquivo remoto é a fonte principal. O arquivo local serve como fallback
            // se não houver conexão com o GitHub.
            IniciarDownloadTagsRemotas();

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Tags locais carregadas; buscando tags remotas no GitHub.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao inicializar tags do lobby: " + ex);
        }

        ForcarDisableServerAuthority();
    }

    private static string NormalizarFriendCode(string friendCode)
    {
        return (friendCode ?? string.Empty)
            .Trim()
            .Replace(':', '#')
            .ToLowerInvariant();
    }

    private static void RegistrarJogador(PlayerControl jogador, string friendCode)
    {
        if (jogador == null || string.IsNullOrWhiteSpace(friendCode))
            return;

        string chave = NormalizarFriendCode(friendCode);
        string nome = jogador.Data?.PlayerName ?? string.Empty;
        string agora = DateTime.UtcNow.ToString("O");
        bool precisaSalvar = false;

        if (!JogadoresRegistrados.TryGetValue(chave, out JogadorRegistrado registro))
        {
            registro = new JogadorRegistrado
            {
                FriendCode = friendCode.Replace(':', '#'),
                Nome = nome,
                PrimeiroRegistroUtc = agora,
                UltimoRegistroUtc = agora
            };
            JogadoresRegistrados[chave] = registro;
            precisaSalvar = true;
        }
        else if (!string.Equals(registro.Nome, nome, StringComparison.Ordinal))
        {
            registro.Nome = nome;
            registro.UltimoRegistroUtc = agora;
            precisaSalvar = true;
        }

        if (precisaSalvar)
            SalvarJogadores();
    }

    private static void SalvarJogadores()
    {
        try
        {
            if (string.IsNullOrEmpty(CaminhoJogadores))
                return;

            File.WriteAllText(CaminhoJogadores,
                JsonSerializer.Serialize(JogadoresRegistrados, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível salvar players.json: " + ex.Message);
        }
    }

    private static void SalvarTags()
    {
        try
        {
            if (string.IsNullOrEmpty(CaminhoTags))
                return;

            var exportar = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Tags)
                exportar[item.Key] = item.Value;

            File.WriteAllText(CaminhoTags,
                JsonSerializer.Serialize(exportar, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível salvar tags.json: " + ex.Message);
        }
    }

    public static bool PartidaIniciada()
    {
        try
        {
            if (AmongUsClient.Instance == null)
                return false;

            Type tipo = AmongUsClient.Instance.GetType();
            PropertyInfo propriedade = tipo.GetProperty(
                "GameState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object estado = propriedade?.GetValue(AmongUsClient.Instance);

            if (estado == null)
            {
                FieldInfo campo = tipo.GetField(
                    "GameState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                estado = campo?.GetValue(AmongUsClient.Instance);
            }

            string nomeEstado = estado?.ToString() ?? string.Empty;
            return string.Equals(nomeEstado, "Started", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool EstaNoLobby()
    {
        try
        {
            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
                PlayerControl.LocalPlayer == null)
                return false;

            Type tipo = AmongUsClient.Instance.GetType();
            PropertyInfo propriedade = tipo.GetProperty(
                "GameState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object estado = propriedade?.GetValue(AmongUsClient.Instance);

            if (estado == null)
            {
                FieldInfo campo = tipo.GetField(
                    "GameState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                estado = campo?.GetValue(AmongUsClient.Instance);
            }

            string nomeEstado = estado?.ToString() ?? string.Empty;
            if (string.Equals(nomeEstado, "Joined", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nomeEstado, "Lobby", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!AvisouEstadoDesconhecido && !string.IsNullOrEmpty(nomeEstado))
            {
                AvisouEstadoDesconhecido = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Estado de rede detectado para tags do lobby: " + nomeEstado);
            }
        }
        catch { }

        return false;
    }

    private static void IniciarDownloadTagsRemotas()
    {
        if (DownloadTagsPendente != null || DateTime.UtcNow < ProximaBuscaTagsUtc)
            return;

        ProximaBuscaTagsUtc = DateTime.UtcNow.AddSeconds(60);
        try
        {
            DownloadTagsPendente = Task.Run(async () =>
                await ClienteHttp.GetStringAsync(UrlTagsRemotas).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            DownloadTagsPendente = null;
            if (!AvisouFalhaTagsRemotas)
            {
                AvisouFalhaTagsRemotas = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível iniciar o download de tags.json: " + ex.Message);
            }
        }
    }

    private static void ProcessarDownloadTagsRemotas()
    {
        if (DownloadTagsPendente == null)
        {
            IniciarDownloadTagsRemotas();
            return;
        }

        if (!DownloadTagsPendente.IsCompleted)
            return;

        Task<string> concluida = DownloadTagsPendente;
        DownloadTagsPendente = null;

        try
        {
            if (concluida.IsFaulted || concluida.IsCanceled)
                throw concluida.Exception ?? new Exception("Download remoto cancelado.");

            string json = concluida.Result;
            var remotas = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (remotas == null)
                throw new InvalidDataException("O JSON remoto não contém um objeto de tags válido.");

            Tags.Clear();
            foreach (var item in remotas)
            {
                string friendCode = NormalizarFriendCode(item.Key);
                string tag = item.Value?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(friendCode) && !string.IsNullOrWhiteSpace(tag))
                    Tags[friendCode] = tag;
            }

            AvisouFalhaTagsRemotas = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Tags remotas carregadas do GitHub: {Tags.Count} registro(s).");
            SalvarTags();
        }
        catch (Exception ex)
        {
            if (!AvisouFalhaTagsRemotas)
            {
                AvisouFalhaTagsRemotas = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Falha ao carregar tags remotas; mantendo tags locais: " + ex.Message);
            }
        }

        IniciarDownloadTagsRemotas();
    }

    public static void Update()
    {
        ProcessarDownloadTagsRemotas();

        if (!EstaNoLobby())
        {
            if (PartidaIniciada())
                RestaurarTodos();
            return;
        }

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null)
                continue;

            string friendCode = NormalizarFriendCode(jogador.Data.FriendCode);
            if (!TagsAplicadas.Contains(jogador.PlayerId))
                RegistrarJogador(jogador, friendCode);

            if (!Tags.TryGetValue(friendCode, out string tag) ||
                string.IsNullOrWhiteSpace(tag) ||
                TagsAplicadas.Contains(jogador.PlayerId))
                continue;

            string nomeOriginal = jogador.Data.PlayerName ?? string.Empty;
            // Evita aplicar a mesma tag em cima dela se o jogo já refletiu o RPC.
            if (!NomesOriginais.ContainsKey(jogador.PlayerId))
                NomesOriginais[jogador.PlayerId] = nomeOriginal;

            string nomeComTag =
                $"<size=50%><color={CorTagDono}>{tag}</color></size>\n{NomesOriginais[jogador.PlayerId]}";

            if (EnviarSetName(jogador, nomeComTag))
            {
                TagsAplicadas.Add(jogador.PlayerId);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Tag '{tag}' aplicada no lobby ao Friend Code {friendCode}.");
            }
        }
    }

    private static bool EnviarSetName(PlayerControl jogador, string nome)
    {
        if (jogador == null || jogador.Data == null || AmongUsClient.Instance == null)
            return false;

        MessageWriter writer = null;
        try
        {
            // SetName vanilla, enviado como GameData para todos os clientes.
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);

            writer.StartMessage(2);
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;

            // Mantém também o nome local atualizado imediatamente.
            jogador.SetName(nome);
            return true;
        }
        catch (Exception ex)
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao aplicar tag de lobby: " + ex.Message);
            return false;
        }
    }

    public static void RestaurarTodos()
    {
        if (NomesOriginais.Count == 0)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                !NomesOriginais.TryGetValue(jogador.PlayerId, out string original))
                continue;

            EnviarSetName(jogador, original);
        }

        NomesOriginais.Clear();
        TagsAplicadas.Clear();
    }

    private static void ForcarDisableServerAuthority()
    {
        try
        {
            // Reactor mantém ReactorConfig internal; usamos reflexão para não depender
            // de uma API pública que não existe nessa versão.
            Type tipoConfig = typeof(Reactor.ReactorPlugin).Assembly.GetType("Reactor.ReactorConfig");
            PropertyInfo propriedade = tipoConfig?.GetProperty(
                "ForceDisableServerAuthority",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            object entrada = propriedade?.GetValue(null);
            PropertyInfo valor = entrada?.GetType().GetProperty(
                "Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (entrada == null || valor == null)
                throw new InvalidOperationException(
                    "Não foi possível localizar ReactorConfig.ForceDisableServerAuthority.");

            valor.SetValue(entrada, true);

            // Persiste a opção no arquivo do Reactor para as próximas inicializações.
            object arquivoConfig = entrada.GetType().GetProperty(
                "ConfigFile", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(entrada);
            arquivoConfig?.GetType().GetMethod(
                "Save", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.Invoke(arquivoConfig, null);

            string caminho = Path.Combine(Paths.ConfigPath, "gg.reactor.api.cfg");
            if (File.Exists(caminho))
            {
                string texto = File.ReadAllText(caminho);
                string atualizado = Regex.Replace(
                    texto,
                    @"(?m)^\s*ForceDisableServerAuthority\s*=\s*(true|false)\s*$",
                    "ForceDisableServerAuthority = true");

                if (!string.Equals(texto, atualizado, StringComparison.Ordinal))
                    File.WriteAllText(caminho, atualizado);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Reactor: ForceDisableServerAuthority definido como true automaticamente.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível forçar ForceDisableServerAuthority automaticamente: " + ex.Message);
        }
    }
}

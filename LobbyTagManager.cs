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
    private static readonly HttpClient ClienteHttp = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static Task<string> DownloadTagsPendente;
    private static DateTime ProximaBuscaTagsUtc = DateTime.MinValue;
    private static bool AvisouFalhaTagsRemotas;

    private static readonly Dictionary<string, TagConfig> Tags =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed class TagConfig
    {
        // A tag pode conter a própria marcação Rich Text, por exemplo:
        // <color=#e3c420>DONO</color>
        public string Tag { get; set; } = string.Empty;
    }
    private static readonly Dictionary<byte, string> NomesOriginais =
        new();
    private static readonly HashSet<byte> TagsAplicadas = new();
    private static readonly HashSet<byte> JogadoresDiagnosticados = new();
    private static string UltimoGameId = string.Empty;
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
                    CarregarTagsJson(File.ReadAllText(CaminhoTags));
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

            File.WriteAllText(CaminhoTags,
                JsonSerializer.Serialize(Tags, new JsonSerializerOptions
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

    private static void CarregarTagsJson(string json)
    {
        foreach (var item in LerTagsJson(json))
            Tags[item.Key] = item.Value;
    }

    private static Dictionary<string, TagConfig> LerTagsJson(string json)
    {
        var resultado = new Dictionary<string, TagConfig>(StringComparer.OrdinalIgnoreCase);
        using JsonDocument documento = JsonDocument.Parse(json);

        if (documento.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("O JSON de tags precisa ser um objeto.");

        foreach (JsonProperty item in documento.RootElement.EnumerateObject())
        {
            // Formato separado:
            // "DONO": { "cor": "#e3c420", "jogadores": ["punhazy#1673"] }
            if (item.Value.ValueKind == JsonValueKind.Object &&
                item.Value.TryGetProperty("jogadores", out JsonElement jogadoresElement) &&
                jogadoresElement.ValueKind == JsonValueKind.Array)
            {
                string textoTagSeparado = item.Name.Trim();
                string corSeparada = string.Empty;

                if (item.Value.TryGetProperty("cor", out JsonElement corSeparadaElement) &&
                    corSeparadaElement.ValueKind == JsonValueKind.String)
                    corSeparada = corSeparadaElement.GetString()?.Trim() ?? string.Empty;

                bool corSeparadaValida = Regex.IsMatch(
                    corSeparada, @"^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?|[a-zA-Z]+)$");

                if (string.IsNullOrWhiteSpace(textoTagSeparado))
                    continue;

                // O TextMeshPro do jogo interpreta a cor com alfa explícito de forma
                // mais consistente. Converte #RRGGBB em #RRGGBBFF sem alterar cores nomeadas.
                string corSeparadaRichText = Regex.IsMatch(corSeparada, @"^#[0-9a-fA-F]{6}$")
                    ? corSeparada + "FF"
                    : corSeparada;

                string tagSeparadaFormatada = corSeparadaValida
                    ? $"<color={corSeparadaRichText}>{textoTagSeparado}</color>"
                    : textoTagSeparado;

                foreach (JsonElement pessoa in jogadoresElement.EnumerateArray())
                {
                    if (pessoa.ValueKind != JsonValueKind.String)
                        continue;

                    string friendCode = NormalizarFriendCode(pessoa.GetString());
                    if (!string.IsNullOrWhiteSpace(friendCode))
                    {
                        resultado[friendCode] = new TagConfig
                        {
                            Tag = tagSeparadaFormatada
                        };
                    }
                }

                continue;
            }

            // Formato anterior:

            // "<color=#e3c420>DONO</color>": ["punhazy#1673", "outro#1234"]
            if (item.Value.ValueKind == JsonValueKind.Array)
            {
                string tagFormatada = item.Name.Trim();
                if (string.IsNullOrWhiteSpace(tagFormatada))
                    continue;

                foreach (JsonElement pessoa in item.Value.EnumerateArray())
                {
                    if (pessoa.ValueKind != JsonValueKind.String)
                        continue;

                    string friendCode = NormalizarFriendCode(pessoa.GetString());
                    if (!string.IsNullOrWhiteSpace(friendCode))
                    {
                        resultado[friendCode] = new TagConfig
                        {
                            Tag = tagFormatada
                        };
                    }
                }

                continue;
            }

            // Compatibilidade com o formato antigo:
            // "punhazy#1673": { "tag": "DONO", "color": "#e3c420" }
            string codigoAntigo = NormalizarFriendCode(item.Name);
            string textoTag = string.Empty;
            string corTag = string.Empty;

            if (item.Value.ValueKind == JsonValueKind.String)
            {
                textoTag = item.Value.GetString()?.Trim() ?? string.Empty;
            }
            else if (item.Value.ValueKind == JsonValueKind.Object)
            {
                if (item.Value.TryGetProperty("tag", out JsonElement tagElement) &&
                    tagElement.ValueKind == JsonValueKind.String)
                    textoTag = tagElement.GetString()?.Trim() ?? string.Empty;

                if (item.Value.TryGetProperty("color", out JsonElement corElement) &&
                    corElement.ValueKind == JsonValueKind.String)
                    corTag = corElement.GetString()?.Trim() ?? string.Empty;
            }

            bool corValida = Regex.IsMatch(
                corTag, @"^(#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?|[a-zA-Z]+)$");

            if (!string.IsNullOrWhiteSpace(codigoAntigo) &&
                !string.IsNullOrWhiteSpace(textoTag))
            {
                string tagFormatada = corValida
                    ? $"<color={corTag}>{textoTag}</color>"
                    : textoTag;

                resultado[codigoAntigo] = new TagConfig
                {
                    Tag = tagFormatada
                };
            }
        }

        return resultado;
    }

    // Retorna true apenas para Friend Codes presentes no tags.json remoto/local.
    public static bool TemTag(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return false;

        string friendCode = NormalizarFriendCode(jogador.Data.FriendCode);
        return !string.IsNullOrWhiteSpace(friendCode) && Tags.ContainsKey(friendCode);
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
            var remotas = LerTagsJson(json);
            if (remotas.Count == 0)
                throw new InvalidDataException("O JSON remoto não contém tags válidas.");

            Tags.Clear();
            foreach (var item in remotas)
                Tags[item.Key] = item.Value;

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

        // GameId muda quando uma nova sala é criada/entramos em outra sala.
        // Limpa os IDs de jogadores da sala anterior para permitir reaplicar as tags.
        string gameIdAtual = AmongUsClient.Instance?.GameId.ToString() ?? string.Empty;
        if (!string.Equals(gameIdAtual, UltimoGameId, StringComparison.Ordinal))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Tags] Nova sessão detectada. GameId anterior='{UltimoGameId}', atual='{gameIdAtual}'. Limpando estado das tags.");
            RestaurarTodos();
            TagsAplicadas.Clear();
            JogadoresDiagnosticados.Clear();
            NomesOriginais.Clear();
            UltimoGameId = gameIdAtual;
        }

        if (!EstaNoLobby())
        {
            // Também limpa ao voltar ao menu/fechar uma sala, não apenas ao iniciar a partida.
            // Isso evita que TagsAplicadas da sala anterior bloqueie a próxima sala.
            RestaurarTodos();
            TagsAplicadas.Clear();
            JogadoresDiagnosticados.Clear();
            NomesOriginais.Clear();
            return;
        }

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null)
                continue;

            string friendCode = NormalizarFriendCode(jogador.Data.FriendCode);
            if (!TagsAplicadas.Contains(jogador.PlayerId))
                RegistrarJogador(jogador, friendCode);

            if (JogadoresDiagnosticados.Add(jogador.PlayerId))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[Tags][PLAYER] Nome='{jogador.Data.PlayerName}', PlayerId={jogador.PlayerId}, FriendCode='{friendCode}', TemTag={Tags.ContainsKey(friendCode)}");
                if (Tags.TryGetValue(friendCode, out TagConfig tagDiagnostico))
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        $"[Tags][CONFIG] Player='{jogador.Data.PlayerName}', tag armazenada='{tagDiagnostico.Tag}'");
            }

            if (!Tags.TryGetValue(friendCode, out TagConfig configuracao) ||
                string.IsNullOrWhiteSpace(configuracao.Tag) ||
                TagsAplicadas.Contains(jogador.PlayerId))
                continue;

            string nomeOriginal = jogador.Data.PlayerName ?? string.Empty;
            // Evita aplicar a mesma tag em cima dela se o jogo já refletiu o RPC.
            if (!NomesOriginais.ContainsKey(jogador.PlayerId))
                NomesOriginais[jogador.PlayerId] = nomeOriginal;

            // Deixa a tag menor e mantém o nome original na linha seguinte.
            // A cor configurada em tags.json fica dentro do <size>.
            // Deixa a cor por fora do tamanho: alguns renderizadores lidam melhor
            // com <color><size>...</size></color> do que com tags aninhadas ao contrário.
            string tagRenderizada = configuracao.Tag;
            Match corMatch = Regex.Match(
                configuracao.Tag,
                @"^<color=([^>]+)>(.*?)</color>$",
                RegexOptions.Singleline);

            if (corMatch.Success)
                tagRenderizada = "<color=" + corMatch.Groups[1].Value +
                    "><size=50%>" + corMatch.Groups[2].Value + "</size></color>";
            else
                tagRenderizada = "<size=50%>" + configuracao.Tag + "</size>";

            string nomeComTag = tagRenderizada + "\n" +
                NomesOriginais[jogador.PlayerId];

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Tags][APLICAR] Nome original='{NomesOriginais[jogador.PlayerId]}'; nome enviado='{nomeComTag}'; cor configurada em tags.json. PlayerId={jogador.PlayerId}, FriendCode='{friendCode}'");

            if (EnviarSetName(jogador, nomeComTag))
            {
                TagsAplicadas.Add(jogador.PlayerId);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Tag '{configuracao.Tag}' aplicada no lobby ao Friend Code {friendCode}.");
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
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Tags][SETNAME] SetName local executado. Nome atual='{jogador.Data.PlayerName}'");
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

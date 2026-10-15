using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

using HarmonyLib;
using Reactor;
using UnityEngine;
using Hazel;
using InnerNet;
using Il2CppInterop.Runtime.Injection;

namespace YouTubeVoteBridge;

[BepInAutoPlugin]
[BepInProcess("Among Us.exe")]
[BepInDependency(ReactorPlugin.Id)]
public partial class YouTubeVoteBridgePlugin : BasePlugin
{
    public static YouTubeVoteBridgePlugin Instance { get; private set; }

    public Harmony Harmony { get; } = new(Id);

    public static ConfigEntry<string> LiveId;
    public static ConfigEntry<bool> LiveConectada;
    public static ConfigEntry<bool> VotacaoAtiva;
    public static ConfigEntry<int> TempoVotacao;
    public static ConfigEntry<int> IntervaloRodadas;
    public static ConfigEntry<int> PreparacaoChat;

    private static bool LeitorConectado;
    private static bool PreparacaoIniciada;
    private static bool PreparacaoConcluida;
    private static float FimPreparacaoChat;
    private static float ProximaRodadaPermitida;

    private const string PREFIXO = "[YouTubeVoteBridge] ";
    private const string TITULO_SISTEMA =
        "<color=blue>YouTubeVoteBridge</color>";

    private static readonly Dictionary<int, float> AvisosPendentes = new();
    private static readonly HashSet<int> AvisosEnviados = new();
    private sealed class VotoRecebido { public string Autor; public int Opcao; public VotoRecebido(string a, int o) { Autor = a; Opcao = o; } }
    private sealed class Evento { public string Nome; public Evento(string n) { Nome = n; } }
    private static readonly ConcurrentQueue<VotoRecebido> FilaVotos = new();
    private static readonly Dictionary<string, int> Votos = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Evento> Opcoes = new();
    private static readonly System.Random Sorteador = new();
    private static readonly List<Evento> Catalogo = RegistroEventos.ObterTodos()
        .Select(evento => new Evento(evento.Nome))
        .ToList();
    private static bool RodadaAtiva;
    private static float FimRodada;
    private static int NumeroRodada;

    private static float ProximaAtualizacaoPainel;
    private static float ProximoLogDiagnostico;
    private static int UltimoTotalVotos = -1;
    private static readonly Dictionary<byte, string> NomesOriginaisPainel = new();
    private static string NomeHostOriginalPlacar;
    private static readonly Dictionary<byte, string> NomesOriginaisPlacarLocal = new();
    private static readonly HashSet<int> ClientesComMod = new();
    private static bool SinalModEnviado;

    // Usado por eventos que precisam alterar o nick somente na tela do cliente dono,
    // inclusive em clientes vanilla, sem replicar a mudança para a sala inteira.
    public static bool ClienteTemMod(int clientId) => ClientesComMod.Contains(clientId);

    private static CancellationTokenSource leitorToken;
    private static Task tarefaLeitor;

    private static readonly HttpClient ClienteHttp = CriarClienteHttp();

    private static HttpClient CriarClienteHttp()
    {
        var cliente = new HttpClient();
        cliente.Timeout = TimeSpan.FromSeconds(30);

        cliente.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/130.0.0.0 Safari/537.36"
        );

        return cliente;
    }

    public static bool ChatEmLeitura =>
        tarefaLeitor != null && !tarefaLeitor.IsCompleted;

    public override void Load()
    {
        Instance = this;

        LiveId = Config.Bind(
            "YouTube", "LiveId", "",
            "ID da live do YouTube."
        );

        LiveConectada = Config.Bind(
            "YouTube", "LiveConectada", false,
            "Indica se a leitura do chat está ativa."
        );

        VotacaoAtiva = Config.Bind(
            "Votacao", "VotacaoAtiva", false,
            "Indica se a votação está ativa."
        );

        TempoVotacao = Config.Bind(
            "Configurações do Mod", "TempoVotacao", 40,
            "Duração de cada votação, em segundos."
        );

        IntervaloRodadas = Config.Bind(
            "Configurações do Mod", "IntervaloRodadas", 15,
            "Intervalo entre o fim de uma votação e o começo da próxima, em segundos."
        );

        PreparacaoChat = Config.Bind(
            "Configurações do Mod", "PreparacaoChat", 10,
            "Tempo de preparação do chat ao conectar. Votos antigos recebidos durante esse período são descartados."
        );

        LobbyTagManager.Initialize();

        Harmony.PatchAll();
        PainelVotacaoRpc.Inicializar();
        EventoTesteMenu.Inicializar();

        Log.LogInfo("YouTubeVoteBridge carregado!");
    }

    public static bool EhHost()
    {
        return AmongUsClient.Instance != null &&
               AmongUsClient.Instance.AmHost;
    }

    // ========================================================
    // RESPOSTAS NO CHAT DO JOGO
    // ========================================================

    public static void Responder(ChatController chat, string mensagem)
    {
        if (chat == null || PlayerControl.LocalPlayer == null)
            return;

        chat.AddChat(PlayerControl.LocalPlayer, PREFIXO + mensagem, false);
    }

    // Escolhe um remetente que ainda esteja vivo. Se o host morreu,
    // um jogador vivo será usado como remetente visual das mensagens privadas.
    private static PlayerControl ObterRemetenteMensagens()
    {
        if (PlayerControl.LocalPlayer == null)
            return null;

        PlayerControl host = PlayerControl.LocalPlayer;
        if (host.Data != null && !host.Data.IsDead && !host.Data.Disconnected)
            return host;

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return host;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            return jogador;
        }

        // Se todos estiverem mortos, mantém o host como remetente de reserva.
        return host;
    }

    // ========================================================
    // MENSAGENS PRIVADAS
    // ========================================================

    public static void EnviarMensagemIndividual(
        PlayerControl destinatario,
        string mensagem)
    {
        if (destinatario == null ||
            destinatario.Data == null ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null ||
            !EhHost() ||
            string.IsNullOrWhiteSpace(mensagem))
            return;

        // Mensagem privada no padrão do EHR:
        // GameDataTo (tipo 6) + OwnerId do destinatário.
        // Diferente do tipo 5 (GameData), este pacote não é transmitido a todos.
        int targetClientId = destinatario.OwnerId;
        if (targetClientId < 0)
        {
            Instance?.Log.LogWarning(
                $"Mensagem individual cancelada: OwnerId inválido para PlayerId={destinatario.PlayerId}.");
            return;
        }

        PlayerControl remetente = ObterRemetenteMensagens();
        if (remetente == null || remetente.Data == null)
            return;

        string nomeOriginal = remetente.Data.PlayerName;
        MessageWriter writer = null;

        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo: somente o ClientId abaixo
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(targetClientId);

            writer.StartMessage(2); // Rpc SetName: título temporário, só no destinatário
            writer.WritePacked(remetente.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(remetente.Data.NetId);
            writer.Write(TITULO_SISTEMA);
            writer.EndMessage();

            writer.StartMessage(2); // Rpc SendChat: texto, só no destinatário
            writer.WritePacked(remetente.NetId);
            writer.Write((byte)RpcCalls.SendChat);
            writer.Write(mensagem);
            writer.EndMessage();

            writer.StartMessage(2); // Rpc SetName: restaura o nome, só no destinatário
            writer.WritePacked(remetente.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(remetente.Data.NetId);
            writer.Write(nomeOriginal);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;

            // Um envio direcionado ao próprio host pode não retornar pela rede.
            // Nesse caso, desenha a mensagem apenas na interface local.
            if (destinatario.AmOwner &&
                HudManager.InstanceExists &&
                HudManager.Instance.Chat != null)
            {
                remetente.SetName(TITULO_SISTEMA);
                HudManager.Instance.Chat.AddChat(remetente, mensagem, false);
                remetente.SetName(nomeOriginal);
            }

            Instance?.Log.LogInfo(
                $"Mensagem individual enviada para PlayerId={destinatario.PlayerId}, ClientId={targetClientId} (GameDataTo).");
        }
        catch (Exception ex)
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }

            Instance?.Log.LogError("Erro ao enviar mensagem individual via GameDataTo: " + ex);
        }
    }

    // Clientes com o mod enviam um sinal ao host. Os clientes que não
    // responderem são tratados como vanilla e recebem a alteração local do nick.
    public static void EnviarSinalModAoHost()
    {
        if (AmongUsClient.Instance == null ||
            AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null ||
            SinalModEnviado)
            return;

        try
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                248,
                SendOption.Reliable,
                AmongUsClient.Instance.HostId
            );
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            SinalModEnviado = true;
        }
        catch (Exception ex)
        {
            Instance?.Log.LogWarning("Falha ao sinalizar cliente com mod: " + ex.Message);
        }
    }

    public static void RegistrarClienteComMod(int clientId)
    {
        if (!EhHost() || clientId < 0)
            return;

        ClientesComMod.Add(clientId);
        Instance?.Log.LogInfo("Cliente com YouTubeVoteBridge detectado: " + clientId);
    }

    private static string CriarNomeComPlacar(string nomeOriginal)
    {
        int[] contagem = ContarVotos();
        int segundosRestantes = Math.Max(
            0,
            (int)Math.Ceiling(FimRodada - Time.realtimeSinceStartup)
        );

        StringBuilder nome = new();
        nome.Append(nomeOriginal);
        for (int i = 0; i < 4; i++)
        {
            string nomeOpcao = i < Opcoes.Count ? Opcoes[i].Nome : "—";
            nome.Append("\n");
            nome.Append(nomeOpcao);
            nome.Append(" (");
            nome.Append(contagem[i]);
            nome.Append(" votos)");
        }
        nome.Append("\ntempo: ");
        nome.Append(segundosRestantes);
        return nome.ToString();
    }

    // Cada jogador sem o mod recebe o placar somente no próprio nick,
    // enviado apenas ao cliente que controla aquele personagem.
    private static void AtualizarPlacarNoNick()
    {
        if (!EhHost() || AmongUsClient.Instance == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.OwnerId < 0 ||
                jogador.OwnerId == AmongUsClient.Instance.ClientId ||
                ClientesComMod.Contains(jogador.OwnerId))
                continue;

            if (!NomesOriginaisPlacarLocal.ContainsKey(jogador.PlayerId))
                NomesOriginaisPlacarLocal[jogador.PlayerId] = jogador.Data.PlayerName;

            try
            {
                DefinirNomeLocal(
                    jogador,
                    CriarNomeComPlacar(NomesOriginaisPlacarLocal[jogador.PlayerId])
                );
            }
            catch (Exception ex)
            {
                Instance?.Log.LogWarning(
                    $"Não foi possível atualizar o nick local do jogador {jogador.PlayerId}: {ex.Message}");
            }
        }
    }

    private static void RestaurarPlacarNoNick()
    {
        if (NomesOriginaisPlacarLocal.Count == 0)
            return;

        if (EhHost() && AmongUsClient.Instance != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    !NomesOriginaisPlacarLocal.TryGetValue(
                        jogador.PlayerId, out string nomeOriginal))
                    continue;

                if (jogador.OwnerId < 0 ||
                    jogador.OwnerId == AmongUsClient.Instance.ClientId ||
                    ClientesComMod.Contains(jogador.OwnerId))
                    continue;

                try
                {
                    DefinirNomeLocal(jogador, nomeOriginal);
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogWarning(
                        $"Não foi possível restaurar o nick local do jogador {jogador.PlayerId}: {ex.Message}");
                }
            }
        }

        NomesOriginaisPlacarLocal.Clear();
    }

    // ========================================================
    // EXTRAÇÃO DE JSON
    // ========================================================

    private static string ExtrairObjetoJson(string texto, int inicioBusca)
    {
        if (string.IsNullOrEmpty(texto) ||
            inicioBusca < 0 ||
            inicioBusca >= texto.Length)
            return "";

        int inicio = texto.IndexOf('{', inicioBusca);

        if (inicio < 0)
            return "";

        int profundidade = 0;
        bool dentroString = false;
        bool escape = false;

        for (int i = inicio; i < texto.Length; i++)
        {
            char c = texto[i];

            if (dentroString)
            {
                if (escape)
                    escape = false;
                else if (c == '\\')
                    escape = true;
                else if (c == '"')
                    dentroString = false;

                continue;
            }

            if (c == '"')
                dentroString = true;
            else if (c == '{')
                profundidade++;
            else if (c == '}')
            {
                profundidade--;

                if (profundidade == 0)
                    return texto.Substring(inicio, i - inicio + 1);
            }
        }

        return "";
    }

    private static string ExtrairConfiguracaoYouTube(string html)
    {
        const string marcador = "ytcfg.set(";

        int posicao = html.IndexOf(
            marcador,
            StringComparison.Ordinal
        );

        while (posicao >= 0)
        {
            int inicio = posicao + marcador.Length;

            while (inicio < html.Length &&
                   char.IsWhiteSpace(html[inicio]))
                inicio++;

            if (inicio < html.Length && html[inicio] == '{')
            {
                string json = ExtrairObjetoJson(html, inicio);

                if (!string.IsNullOrWhiteSpace(json))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(json);

                        if (doc.RootElement.ValueKind ==
                                JsonValueKind.Object &&
                            doc.RootElement.TryGetProperty(
                                "INNERTUBE_API_KEY", out _))
                        {
                            return json;
                        }
                    }
                    catch (JsonException)
                    {
                        // Tenta a próxima ocorrência.
                    }
                }
            }

            posicao = html.IndexOf(
                marcador,
                posicao + marcador.Length,
                StringComparison.Ordinal
            );
        }

        return "";
    }

    private static string LerString(JsonElement objeto, string nome)
    {
        if (objeto.ValueKind == JsonValueKind.Object &&
            objeto.TryGetProperty(nome, out JsonElement valor) &&
            valor.ValueKind == JsonValueKind.String)
            return valor.GetString() ?? "";

        return "";
    }

    private static JsonElement EncontrarPropriedade(
        JsonElement elemento,
        string nome)
    {
        if (elemento.ValueKind == JsonValueKind.Object)
        {
            if (elemento.TryGetProperty(nome, out JsonElement encontrado))
                return encontrado;

            foreach (JsonProperty propriedade in elemento.EnumerateObject())
            {
                JsonElement resultado = EncontrarPropriedade(
                    propriedade.Value, nome
                );

                if (resultado.ValueKind != JsonValueKind.Undefined)
                    return resultado;
            }
        }
        else if (elemento.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement filho in elemento.EnumerateArray())
            {
                JsonElement resultado = EncontrarPropriedade(filho, nome);

                if (resultado.ValueKind != JsonValueKind.Undefined)
                    return resultado;
            }
        }

        return default;
    }

    private static string EncontrarContinuacao(JsonElement elemento)
    {
        JsonElement continuacoes = EncontrarPropriedade(
            elemento, "continuations"
        );

        if (continuacoes.ValueKind != JsonValueKind.Array)
            return "";

        foreach (JsonElement continuacao in continuacoes.EnumerateArray())
        {
            foreach (string tipo in new[]
            {
                "invalidationContinuationData",
                "timedContinuationData",
                "reloadContinuationData"
            })
            {
                if (continuacao.TryGetProperty(tipo, out JsonElement dados) &&
                    dados.TryGetProperty(
                        "continuation", out JsonElement token) &&
                    token.ValueKind == JsonValueKind.String)
                    return token.GetString() ?? "";
            }
        }

        return "";
    }

    private static JsonElement EncontrarItemMensagem(JsonElement acao)
    {
        foreach (string nome in new[]
        {
            "liveChatTextMessageRenderer",
            "liveChatPaidMessageRenderer",
            "liveChatMembershipItemRenderer",
            "liveChatPaidStickerRenderer"
        })
        {
            JsonElement renderer = EncontrarPropriedade(acao, nome);

            if (renderer.ValueKind == JsonValueKind.Object)
                return renderer;
        }

        return default;
    }

    private static string ObterTexto(JsonElement elemento, string nome)
    {
        JsonElement valor = EncontrarPropriedade(elemento, nome);

        if (valor.ValueKind == JsonValueKind.String)
            return valor.GetString() ?? "";

        if (valor.ValueKind != JsonValueKind.Object)
            return "";

        if (valor.TryGetProperty("simpleText", out JsonElement simples) &&
            simples.ValueKind == JsonValueKind.String)
            return simples.GetString() ?? "";

        if (valor.TryGetProperty("runs", out JsonElement runs) &&
            runs.ValueKind == JsonValueKind.Array)
        {
            var texto = new StringBuilder();

            foreach (JsonElement run in runs.EnumerateArray())
            {
                if (run.TryGetProperty("text", out JsonElement parte) &&
                    parte.ValueKind == JsonValueKind.String)
                {
                    texto.Append(parte.GetString());
                    continue;
                }

                if (run.TryGetProperty("emoji", out JsonElement emoji))
                {
                    string atalho = LerString(emoji, "shortcuts");

                    if (string.IsNullOrWhiteSpace(atalho))
                        atalho = LerString(emoji, "searchTerms");

                    if (!string.IsNullOrWhiteSpace(atalho))
                        texto.Append(atalho);
                }
            }

            return texto.ToString();
        }

        return "";
    }

    // ========================================================
    // LEITOR HTTP DO YOUTUBE
    // ========================================================

    public static void IniciarLeituraChat()
    {
        if (!EhHost())
        {
            Instance.Log.LogWarning(
                "Somente o host pode iniciar o leitor do YouTube."
            );
            return;
        }

        if (ChatEmLeitura)
        {
            Instance.Log.LogInfo("O leitor já está funcionando.");
            return;
        }

        string id = LiveId.Value?.Trim() ?? "";

        if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$"))
        {
            Instance.Log.LogError("ID da live inválido.");
            return;
        }

        leitorToken?.Dispose();
        leitorToken = new CancellationTokenSource();

        CancellationToken token = leitorToken.Token;

        LiveConectada.Value = true;
        LeitorConectado = false;
        PreparacaoIniciada = false;
        PreparacaoConcluida = false;
        FimPreparacaoChat = 0f;
        ProximaRodadaPermitida = 0f;
        Instance.Config.Save();

        tarefaLeitor = Task.Run(() => LerChatAsync(id, token), token);

        Instance.Log.LogInfo("Iniciando leitor HTTP. ID: " + id);
    }

    public static void PararLeituraChat()
    {
        LiveConectada.Value = false;
        LeitorConectado = false;
        PreparacaoIniciada = false;
        PreparacaoConcluida = false;
        leitorToken?.Cancel();
        Instance.Config.Save();

        Instance.Log.LogInfo(
            "Solicitado encerramento da leitura do YouTube."
        );
    }

    // O YouTube pode responder 429 temporariamente. Respeita Retry-After
    // e usa espera progressiva para evitar bombardear o endpoint durante o limite.
    private static async Task<HttpResponseMessage> GetComRetry429Async(
        string url,
        CancellationToken token)
    {
        int tentativa = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();
            HttpResponseMessage resposta = await ClienteHttp.GetAsync(url, token);

            if ((int)resposta.StatusCode != 429)
                return resposta;

            TimeSpan espera = CalcularEspera429(resposta, tentativa);
            resposta.Dispose();
            tentativa++;

            Instance?.Log.LogWarning(
                $"YouTube limitou o GET (HTTP 429). Nova tentativa em " +
                $"{espera.TotalSeconds:0}s (tentativa {tentativa}).");

            await Task.Delay(espera, token);
        }
    }

    private static async Task<HttpResponseMessage> PostComRetry429Async(
        string url,
        string json,
        CancellationToken token)
    {
        int tentativa = 0;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            using var conteudo = new StringContent(
                json, Encoding.UTF8, "application/json");

            HttpResponseMessage resposta =
                await ClienteHttp.PostAsync(url, conteudo, token);

            if ((int)resposta.StatusCode != 429)
                return resposta;

            TimeSpan espera = CalcularEspera429(resposta, tentativa);
            resposta.Dispose();
            tentativa++;

            Instance?.Log.LogWarning(
                $"YouTube limitou o POST do chat (HTTP 429). Nova tentativa em " +
                $"{espera.TotalSeconds:0}s (tentativa {tentativa}).");

            await Task.Delay(espera, token);
        }
    }

    private static TimeSpan CalcularEspera429(
        HttpResponseMessage resposta,
        int tentativa)
    {
        TimeSpan espera;
        var retryAfter = resposta.Headers.RetryAfter;

        if (retryAfter?.Delta != null)
        {
            espera = retryAfter.Delta.Value;
        }
        else if (retryAfter?.Date != null)
        {
            espera = retryAfter.Date.Value - DateTimeOffset.UtcNow;
        }
        else
        {
            // 5, 10, 20, 40 e no máximo 60 segundos.
            int segundos = tentativa >= 4
                ? 60
                : Math.Min(60, 5 * (1 << Math.Min(tentativa, 4)));
            espera = TimeSpan.FromSeconds(segundos);
        }

        // Evita espera negativa ou exagerada caso o servidor envie um cabeçalho ruim.
        if (espera < TimeSpan.FromSeconds(2))
            espera = TimeSpan.FromSeconds(2);
        if (espera > TimeSpan.FromMinutes(5))
            espera = TimeSpan.FromMinutes(5);

        return espera;
    }

    private static async Task LerChatAsync(
        string liveId,
        CancellationToken token)
    {
        try
        {
            Instance.Log.LogInfo("Conectando ao YouTube por HTTP...");

            string urlLive =
                "https://www.youtube.com/watch?v=" +
                Uri.EscapeDataString(liveId);

            using HttpResponseMessage respostaPagina =
                await GetComRetry429Async(urlLive, token);

            respostaPagina.EnsureSuccessStatusCode();

            string html = await respostaPagina.Content.ReadAsStringAsync();

            string jsonConfig = ExtrairConfiguracaoYouTube(html);

            if (string.IsNullOrWhiteSpace(jsonConfig))
            {
                Instance.Log.LogError(
                    "Não foi possível encontrar a configuração JSON do YouTube. " +
                    "A página pode exigir consentimento, login ou ter mudado."
                );
                return;
            }

            using JsonDocument configDoc = JsonDocument.Parse(jsonConfig);
            JsonElement config = configDoc.RootElement;

            string apiKey = LerString(config, "INNERTUBE_API_KEY");
            JsonElement contexto = default;

            if (config.TryGetProperty(
                "INNERTUBE_CONTEXT",
                out JsonElement contextoEncontrado))
                contexto = contextoEncontrado.Clone();

            if (string.IsNullOrWhiteSpace(apiKey) ||
                contexto.ValueKind != JsonValueKind.Object)
            {
                Instance.Log.LogError(
                    "A configuração não contém a chave ou o contexto."
                );
                return;
            }

            string continuacao = "";
            int posDados = html.IndexOf(
                "ytInitialData",
                StringComparison.Ordinal
            );

            if (posDados >= 0)
            {
                string jsonDados = ExtrairObjetoJson(html, posDados);

                if (!string.IsNullOrWhiteSpace(jsonDados))
                {
                    using JsonDocument dadosDoc =
                        JsonDocument.Parse(jsonDados);

                    JsonElement renderer = EncontrarPropriedade(
                        dadosDoc.RootElement, "liveChatRenderer"
                    );

                    if (renderer.ValueKind != JsonValueKind.Undefined)
                        continuacao = EncontrarContinuacao(renderer);
                }
            }

            if (string.IsNullOrWhiteSpace(continuacao))
            {
                Match matchContinuacao = Regex.Match(
                    html,
                    @"""continuation"":\s*""([^""]+)""",
                    RegexOptions.Singleline
                );

                if (matchContinuacao.Success)
                    continuacao = matchContinuacao.Groups[1].Value;
            }

            if (string.IsNullOrWhiteSpace(continuacao))
            {
                throw new Exception(
                    "Não encontrei o token de continuação do chat. " +
                    "Confirme se a transmissão está ao vivo e tem chat habilitado."
                );
            }

            string endpoint =
                "https://www.youtube.com/youtubei/v1/live_chat/get_live_chat" +
                "?key=" + Uri.EscapeDataString(apiKey) +
                "&prettyPrint=false";

            Instance.Log.LogInfo(
                "Conexão HTTP preparada. Limpando mensagens antigas antes de votar..."
            );

            // A primeira resposta pode conter mensagens antigas do histórico.
            // Processamos a continuação, mas ignoramos votos dessa primeira resposta.
            bool ignorarPrimeiraResposta = true;
            LeitorConectado = true;
            var mensagensVistas = new HashSet<string>();

            while (!token.IsCancellationRequested)
            {
                var payload = new
                {
                    context = contexto,
                    continuation = continuacao
                };

                string json = JsonSerializer.Serialize(payload);

                using HttpResponseMessage resposta =
                    await PostComRetry429Async(endpoint, json, token);

                resposta.EnsureSuccessStatusCode();

                string corpo = await resposta.Content.ReadAsStringAsync();

                using JsonDocument documento = JsonDocument.Parse(corpo);

                JsonElement continuacaoChat = EncontrarPropriedade(
                    documento.RootElement, "liveChatContinuation"
                );

                if (continuacaoChat.ValueKind == JsonValueKind.Undefined)
                {
                    throw new Exception(
                        "O YouTube não retornou liveChatContinuation. Resposta: " +
                        corpo.Substring(0, Math.Min(corpo.Length, 500))
                    );
                }

                if (continuacaoChat.TryGetProperty(
                    "actions", out JsonElement actions) &&
                    actions.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement acao in actions.EnumerateArray())
                    {
                        if (token.IsCancellationRequested)
                            break;

                        JsonElement item = EncontrarItemMensagem(acao);

                        if (item.ValueKind == JsonValueKind.Undefined)
                            continue;

                        string autor = ObterTexto(item, "authorName");
                        string mensagem = ObterTexto(item, "message");

                        if (string.IsNullOrWhiteSpace(mensagem))
                            continue;

                        if (string.IsNullOrWhiteSpace(autor))
                            autor = "Desconhecido";

                        string idMensagem = LerString(item, "id");
                        string chave = !string.IsNullOrWhiteSpace(idMensagem)
                            ? idMensagem
                            : autor + "\n" + mensagem;

                        if (!mensagensVistas.Add(chave))
                            continue;

                        Instance.Log.LogInfo(
                            "[YouTube Chat] " + autor + ": " + mensagem
                        );
                        if (!ignorarPrimeiraResposta &&
                            VotacaoAtiva != null && VotacaoAtiva.Value &&
                            Regex.IsMatch(mensagem.Trim(), @"^[1-4]$"))
                            FilaVotos.Enqueue(new VotoRecebido(autor, int.Parse(mensagem.Trim())));

                        if (mensagensVistas.Count > 5000)
                            mensagensVistas.Clear();
                    }
                }

                ignorarPrimeiraResposta = false;
                continuacao = EncontrarContinuacao(continuacaoChat);

                if (string.IsNullOrWhiteSpace(continuacao))
                {
                    throw new Exception(
                        "O YouTube não forneceu um novo token de continuação."
                    );
                }

                int esperaMs = 1000;

                if (continuacaoChat.TryGetProperty(
                    "continuations", out JsonElement continuacoes) &&
                    continuacoes.ValueKind == JsonValueKind.Array &&
                    continuacoes.GetArrayLength() > 0)
                {
                    JsonElement primeira = continuacoes[0];
                    JsonElement espera = EncontrarPropriedade(
                        primeira, "timeoutMs"
                    );

                    if (espera.ValueKind == JsonValueKind.Number &&
                        espera.TryGetInt32(out int valorEspera))
                    {
                        // Não consultar antes do intervalo solicitado pelo YouTube.
                        // O limite anterior de 15 s podia acelerar a consulta se o
                        // servidor pedisse uma espera maior.
                        esperaMs = Math.Clamp(valorEspera, 1000, 120000);
                    }
                }

                await Task.Delay(esperaMs, token);
            }

            Instance.Log.LogInfo("Leitura HTTP do chat encerrada.");
        }
        catch (OperationCanceledException)
        {
            if (Instance != null)
                Instance.Log.LogInfo("Leitura do chat cancelada.");
        }
        catch (Exception ex)
        {
            if (Instance != null)
                Instance.Log.LogError("Erro no leitor HTTP do YouTube: " + ex);
        }
        finally
        {
            if (LiveConectada != null)
                LiveConectada.Value = false;

            if (Instance != null)
                Instance.Config.Save();
        }
    }

    // ========================================================
    // MOTOR DE VOTAÇÃO
    // ========================================================
    internal static void CancelarReuniaoSolicitada()
    {
        if (!EhHost()) return;
        RestaurarNomesPainel();
        RestaurarPlacarNoNick();
        EventoCongelarJogador.PausarDuranteReuniao();
        Instance?.Log.LogDebug("Solicitação de reunião detectada; nomes restaurados antes de permitir a chamada.");
    }

    internal static void Anunciar(string texto)
    {
        if (PlayerControl.LocalPlayer != null && HudManager.InstanceExists &&
            HudManager.Instance.Chat != null)
            HudManager.Instance.Chat.AddChat(PlayerControl.LocalPlayer, PREFIXO + texto, false);
    }

    private static int[] ContarVotos()
    {
        int[] contagem = new int[4];
        foreach (int voto in Votos.Values)
            if (voto >= 1 && voto <= 4) contagem[voto - 1]++;
        return contagem;
    }

    // Altera o nome apenas na tela do cliente dono daquele jogador.
    // Segue o padrão de RPC SetName direcionado usado pelo EHR.
    public static void DefinirNomeLocal(PlayerControl jogador, string nome)
    {
        if (jogador == null)
        {
            Instance?.Log.LogWarning("DIAG Painel: DefinirNomeLocal recebeu jogador nulo.");
            return;
        }

        if (jogador.Data == null)
        {
            Instance?.Log.LogWarning($"DIAG Painel: jogador.Data nulo (PlayerId={jogador.PlayerId}).");
            return;
        }

        if (AmongUsClient.Instance == null)
        {
            Instance?.Log.LogWarning("DIAG Painel: AmongUsClient.Instance nulo.");
            return;
        }

        try
        {
            if (jogador.AmOwner)
            {
                Instance.Log.LogInfo($"SetName local direto");
                jogador.SetName(nome);
                return;
            }

            if (!EhHost())
            {
                Instance?.Log.LogWarning($"DIAG Painel: sem host; ignorando PlayerId={jogador.PlayerId}.");
                return;
            }

            if (jogador.OwnerId < 0)
            {
                Instance?.Log.LogWarning($"DIAG Painel: OwnerId inválido ({jogador.OwnerId}) para PlayerId={jogador.PlayerId}.");
                return;
            }

            // Não enviar SetName via GameDataTo para clientes remotos.
            // Em salas vanilla/não registradas, o servidor oficial pode expulsar
            // o host por esse tipo de pacote. O placar por nick fica desativado
            // para jogadores remotos; o painel normal do mod continua disponível.
            return;
        }
        catch (Exception ex)
        {
            Instance.Log.LogError("Falha ao atualizar nome local do painel: " + ex);
        }
    }

    private static void RestaurarNomesPainel()
    {
        foreach (var item in NomesOriginaisPainel.ToArray())
        {
            PlayerControl jogador = null;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            {
                if (p != null && p.PlayerId == item.Key) { jogador = p; break; }
            }
            if (jogador != null && jogador.Data != null)
                DefinirNomeLocal(jogador, item.Value);
        }
        NomesOriginaisPainel.Clear();
    }

    private static void AtualizarPainelVotacao()
    {
        if (!RodadaAtiva) return;
        if (Time.realtimeSinceStartup < ProximaAtualizacaoPainel) return;

        ProximaAtualizacaoPainel = Time.realtimeSinceStartup + 1f;
        PainelVotacaoRpc.Publicar(
            NumeroRodada,
            true,
            Math.Max(0, (int)Math.Ceiling(FimRodada - Time.realtimeSinceStartup)),
            Opcoes.Select(o => o.Nome).ToArray(),
            ContarVotos()
        );
        AtualizarPlacarNoNick();
    }

    private static void EncerrarRodada()
    {
        RestaurarPlacarNoNick();
        RestaurarNomesPainel();
        RodadaAtiva = false;
        PainelVotacaoRpc.Publicar(NumeroRodada, false, 0, Array.Empty<string>(), new int[4]);
        Votos.Clear();
        Opcoes.Clear();
        UltimoTotalVotos = -1;
        while (FilaVotos.TryDequeue(out _)) { }
    }

    private static void IniciarRodada()
    {
        if (Catalogo.Count < 4) return;
        EncerrarRodada();
        while (FilaVotos.TryDequeue(out _)) { }
        Opcoes.AddRange(Catalogo.OrderBy(_ => Sorteador.Next()).Take(4));
        NumeroRodada++;
        FimRodada = Time.realtimeSinceStartup + Math.Max(5, TempoVotacao.Value);
        ProximaAtualizacaoPainel = 0f;
        RodadaAtiva = true;
        
        
    }

    public static void AtualizarVotacao()
    {
        if (!EhHost())
            return;

        // A votação do YouTube só pode iniciar eventos durante uma partida.
        // Em lobby, descarta votos acumulados sem interromper os leitores da live.
        if (!LobbyTagManager.PartidaIniciada())
        {
            if (RodadaAtiva)
                EncerrarRodada();

            while (FilaVotos.TryDequeue(out _)) { }
            return;
        }

        if (MeetingHud.Instance != null)
        {
            float pausa = Math.Max(0f, Time.deltaTime);
            if (RodadaAtiva) FimRodada += pausa;
            ProximaRodadaPermitida += pausa;
            EventoCongelarJogador.PausarDuranteReuniao();
            RestaurarNomesPainel();
            RestaurarPlacarNoNick();
            return;
        }

        // Enquanto o tsunami estiver ativo, não inicia nem processa outra votação/evento.
        if (EventoTsunami.Ativo)
        {
            if (RodadaAtiva)
            {
                RestaurarNomesPainel();
                RestaurarPlacarNoNick();
                RodadaAtiva = false;
                Votos.Clear();
                Opcoes.Clear();
                while (FilaVotos.TryDequeue(out _)) { }
            }
            return;
        }

        EventoCongelarJogador.Atualizar();

        if (VotacaoAtiva == null || !VotacaoAtiva.Value || !ChatEmLeitura)
        {
            if (RodadaAtiva) EncerrarRodada();
            LeitorConectado = false;
            PreparacaoIniciada = false;
            PreparacaoConcluida = false;
            while (FilaVotos.TryDequeue(out _)) { }
            return;
        }

        // Aguarda o leitor conectar e descarta votos do buffer/histórico antigo.
        if (!PreparacaoConcluida)
        {
            while (FilaVotos.TryDequeue(out _)) { }

            if (!LeitorConectado)
                return;

            if (!PreparacaoIniciada)
            {
                PreparacaoIniciada = true;
                FimPreparacaoChat = Time.realtimeSinceStartup +
                    Math.Clamp(PreparacaoChat.Value, 0, 120);
                Instance.Log.LogInfo(
                    "Preparação do chat iniciada por " + PreparacaoChat.Value + " segundo(s)."
                );
            }

            if (Time.realtimeSinceStartup < FimPreparacaoChat)
                return;

            PreparacaoConcluida = true;
            ProximaRodadaPermitida = Time.realtimeSinceStartup;
            while (FilaVotos.TryDequeue(out _)) { }
            
        }

        while (FilaVotos.TryDequeue(out VotoRecebido voto))
        {
            if (RodadaAtiva && !string.IsNullOrWhiteSpace(voto.Autor))
                Votos[voto.Autor.Trim()] = voto.Opcao;
        }

        if (!RodadaAtiva)
        {
            if (Time.realtimeSinceStartup < ProximaRodadaPermitida)
                return;
            IniciarRodada();
            return;
        }
        AtualizarPainelVotacao();
        if (Time.realtimeSinceStartup < FimRodada) return;

        RestaurarNomesPainel();
        RestaurarPlacarNoNick();
        RodadaAtiva = false;
        PainelVotacaoRpc.Publicar(NumeroRodada, false, 0, Array.Empty<string>(), new int[4]);
        int[] contagem = ContarVotos();
        
        

        if (Votos.Count > 0)
        {
            int maior = contagem.Max();
            List<int> empatadas = Enumerable.Range(1, 4).Where(n => contagem[n - 1] == maior).ToList();
            int vencedora = empatadas[Sorteador.Next(empatadas.Count)];
            Evento eventoVencedor = Opcoes[vencedora - 1];
            
            switch (eventoVencedor.Nome)
            {
                case "Escudo temporário":
                    new EventoEscudoTemporario().Executar();
                    break;
                case "Congelar um jogador temporariamente":
                    new EventoCongelarJogador().Executar();
                    break;
                case "Supervelocidade temporária":
                    new EventoSupervelocidade().Executar();
                    break;
                case "Velocidade reduzida temporária":
                    new EventoVelocidadeReduzida().Executar();
                    break;
                case "Trocar posição de dois jogadores":
                    new EventoTrocarPosicoes().Executar();
                    break;
                case "Trocar cores de jogadores":
                    new EventoTrocarCores().Executar();
                    break;
                case "Fechar as portas":
                case "Fechar todas as portas":
                    new EventoFecharPortas().Executar();
                    break;
                case "Fechar uma porta aleatória":
                    new EventoFecharPortaAleatoria().Executar();
                    break;
                case "Criar corpo falso":
                    new EventoCriarCorpoFalso().Executar();
                    break;
                case "Tsunami":
                    new EventoTsunami().Executar();
                    break;
                case "Reportar um corpo":
                    new EventoReportarCorpo().Executar();
                    break;
                case "Inverter os controles de um jogador aleatório por 15 segundos":
                case "Inverter os controles por 15 segundos":
                    new EventoInverterControles().Executar();
                    break;
                case "Todos ficam com movimento invertido por 15 segundos":
                    new EventoInverterMovimentoTodos().Executar();
                    break;
                case "Um jogador aleatório fica com movimento invertido por 15 segundos":
                    new EventoInverterMovimentoAleatorio().Executar();
                    break;
                case "Teleporte aleatório":
                    new EventoTeleporteAleatorio().Executar();
                    break;
                case "Um jogador aleatório comete suicídio":
                    new EventoSuicidioAleatorio().Executar();
                    break;
                case "Trocar identidades de dois jogadores por 20 segundos":
                    new EventoTrocarIdentidades().Executar();
                    break;
                case "Visão reduzida para todos por 20 segundos":
                    new EventoVisaoReduzida().Executar();
                    break;
                case "Sabotagem aleatória":
                    new EventoSabotagemAleatoria().Executar();
                    break;
                case "Expulsar todos para o duto mais próximo":
                    new EventoDutoMaisProximoTodos().Executar();
                    break;
                case "Expulsar todos por um duto aleatório":
                    new EventoDutoAleatorioTodos().Executar();
                    break;
                case "Expulsar um jogador aleatório para o duto mais próximo":
                    new EventoDutoMaisProximoAleatorio().Executar();
                    break;
                case "Quem completar uma tarefa mata um jogador":
                    EventoTarefaMataJogador.Ativar();
                    break;
                case "Quem completar uma tarefa se mata":
                    EventoTarefaSuicidio.Ativar();
                    break;
                default:
                    Instance.Log.LogWarning("O evento vencedor ainda não tem efeito conectado: " + eventoVencedor.Nome);
                    break;
            }
        }
        
        Votos.Clear();
        Opcoes.Clear();
        ProximaRodadaPermitida = Time.realtimeSinceStartup +
            Math.Clamp(IntervaloRodadas.Value, 0, 600);
        Instance.Log.LogInfo(
            "Próxima votação liberada em " + IntervaloRodadas.Value + " segundo(s)."
        );
    }

    // ========================================================
    // ÁREA DE TRANSFERÊNCIA
    // ========================================================

    public static string LerAreaDeTransferencia()
    {
        try
        {
            return GUIUtility.systemCopyBuffer ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string ExtrairLiveId(string entrada)
    {
        if (string.IsNullOrWhiteSpace(entrada))
            return "";

        string texto = entrada.Trim();
        texto = texto.Trim('\"', '\'', '<', '>', '(', ')', '[', ']').Trim();
        texto = texto.Replace("\u200B", "").Replace("\u200C", "")
            .Replace("\u200D", "").Replace("\uFEFF", "").Trim();

        Match match = Regex.Match(
            texto,
            @"(?:youtube\.com/(?:watch\?(?:.*?&)?v=|live/|embed/|shorts/)|youtu\.be/)([A-Za-z0-9_-]{11})",
            RegexOptions.IgnoreCase
        );
        if (match.Success)
            return match.Groups[1].Value;

        // Um ID puro do YouTube tem 11 caracteres válidos.
        if (Regex.IsMatch(texto, @"^[A-Za-z0-9_-]{11}$"))
            return texto;

        // Também tolera texto/URL copiados junto com o ID.
        Match idIsolado = Regex.Match(texto, @"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{11}(?![A-Za-z0-9_-])");
        return idIsolado.Success ? idIsolado.Value : "";
    }
    // ========================================================
    // COMANDOS
    // ========================================================

    private static void ResponderComando(
        ChatController chat,
        PlayerControl destinatario,
        string mensagem)
    {
        if (destinatario != null && !destinatario.AmOwner)
        {
            EnviarMensagemIndividual(destinatario, mensagem);
            return;
        }

        Responder(chat, mensagem);
    }

    public static void ProcessarComando(
        ChatController chat,
        string comando,
        PlayerControl destinatario = null)
    {
        if (chat == null)
            return;

        string argumentosAntesDaPermissao = comando.Length > "/youtube".Length
            ? comando.Substring("/youtube".Length).Trim()
            : "";
        if (argumentosAntesDaPermissao.Equals("config", StringComparison.OrdinalIgnoreCase) ||
            argumentosAntesDaPermissao.Equals("configuracoes", StringComparison.OrdinalIgnoreCase) ||
            argumentosAntesDaPermissao.Equals("configurações", StringComparison.OrdinalIgnoreCase))
        {
            ResponderComando(chat, destinatario, PainelVotacaoRpc.ObterTextoConfiguracoes());
            return;
        }

        if (!EhHost())
        {
            EnviarMensagemIndividual(
                PlayerControl.LocalPlayer,
                "<color=red>Você não tem permissão para usar esse comando</color>"
            );
            return;
        }

        string argumentos = comando.Length > "/youtube".Length
            ? comando.Substring("/youtube".Length).Trim()
            : "";

        if (argumentos.Length == 0 ||
            argumentos.Equals("ajuda", StringComparison.OrdinalIgnoreCase))
        {
            ResponderComando(chat, destinatario,
                chat,
                "Comandos: /youtube live (usa ID copiado) | colar | status | iniciar | parar | tempo 40 | intervalo 15 | preparacao 10 | config"
            );
            return;
        }

        string[] partes = argumentos.Split(
            new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries
        );

        string acao = partes[0].ToLowerInvariant();

        switch (acao)
        {
            case "config":
            case "configuracoes":
            case "configurações":
                ResponderComando(chat, destinatario,
                    "Configurações: tempo=" + TempoVotacao.Value + "s | intervalo=" +
                    IntervaloRodadas.Value + "s | preparação=" + PreparacaoChat.Value +
                    "s. Use /youtube tempo 40, /youtube intervalo 15 ou /youtube preparacao 10.");
                return;

            case "intervalo":
                if (partes.Length < 2 || !int.TryParse(partes[1], out int intervalo) ||
                    intervalo < 0 || intervalo > 600)
                {
                    ResponderComando(chat, destinatario, "Use: /youtube intervalo 15 (0 a 600 segundos).");
                    return;
                }
                IntervaloRodadas.Value = intervalo;
                Instance.Config.Save();
                ResponderComando(chat, destinatario, "Intervalo entre votações definido para " + intervalo + " segundo(s).");
                return;

            case "preparacao":
            case "preparação":
                if (partes.Length < 2 || !int.TryParse(partes[1], out int preparacao) ||
                    preparacao < 0 || preparacao > 120)
                {
                    ResponderComando(chat, destinatario, "Use: /youtube preparacao 10 (0 a 120 segundos).");
                    return;
                }
                PreparacaoChat.Value = preparacao;
                Instance.Config.Save();
                ResponderComando(chat, destinatario, "Preparação do chat definida para " + preparacao + " segundo(s).");
                return;

            case "status":
            case "info":
                MostrarStatus(chat, destinatario);
                return;

            case "parar":
            case "off":
            case "desligar":
                VotacaoAtiva.Value = false;
                EncerrarRodada();
                PararLeituraChat();
                Instance.Config.Save();
                ResponderComando(chat, destinatario, "Leitura do YouTube e votações desativadas.");
                return;

            case "live":
                // Usa exatamente a mesma lógica do comando "colar":
                // lê o ID/URL da área de transferência.
                string textoLiveCopiado = LerAreaDeTransferencia();
                string liveId = ExtrairLiveId(textoLiveCopiado);

                if (!Regex.IsMatch(liveId, @"^[A-Za-z0-9_-]{11}$"))
                {
                    ResponderComando(chat, destinatario, "Copie o ID ou a URL da live e use /youtube live. Também pode usar /youtube colar.");
                    return;
                }

                PararLeituraChat();
                LiveId.Value = liveId;
                LiveConectada.Value = false;
                VotacaoAtiva.Value = true;
                Instance.Config.Save();

                IniciarLeituraChat();
                ResponderComando(chat, destinatario, "ID definido pela área de transferência: " + liveId + ". Preparando o chat...");
                return;

            case "iniciar":
            case "on":
            case "ligar":
                if (string.IsNullOrWhiteSpace(LiveId.Value))
                {
                    ResponderComando(chat, destinatario,
                        chat,
                        "Defina primeiro o ID: /youtube ID ou /youtube colar."
                    );
                    return;
                }

                VotacaoAtiva.Value = true;
                Instance.Config.Save();
                IniciarLeituraChat();
                ResponderComando(chat, destinatario, "Leitura solicitada. A votação começará quando o chat conectar.");
                return;

            case "tempo":
                if (partes.Length < 2 ||
                    !int.TryParse(partes[1], out int segundos))
                {
                    ResponderComando(chat, destinatario, "Use: /youtube tempo 40");
                    return;
                }

                if (segundos < 5 || segundos > 600)
                {
                    ResponderComando(chat, destinatario, "O tempo deve estar entre 5 e 600 segundos.");
                    return;
                }

                TempoVotacao.Value = segundos;
                Instance.Config.Save();

                ResponderComando(chat, destinatario,
                    chat,
                    "Duração da votação definida para " +
                    segundos + " segundos."
                );
                return;

            case "colar":
            case "clipboard":
            case "area":
                string copiado = LerAreaDeTransferencia();
                string idColado = ExtrairLiveId(copiado);

                if (idColado.Length == 0)
                {
                    ResponderComando(chat, destinatario,
                        chat,
                        "Não encontrei um ID válido de live na área de transferência."
                    );
                    return;
                }

                LiveId.Value = idColado;
                LiveConectada.Value = false;
                Instance.Config.Save();

                ResponderComando(chat, destinatario, "ID da live definido: " + idColado);
                return;
        }

        string id = ExtrairLiveId(argumentos);

        if (id.Length > 0)
        {
            PararLeituraChat();
            LiveId.Value = id;
            LiveConectada.Value = false;
            Instance.Config.Save();

            ResponderComando(chat, destinatario,
                chat,
                "ID da live definido: " + id +
                ". Use /youtube iniciar para ler o chat."
            );
            return;
        }

        ResponderComando(chat, destinatario, "Comando desconhecido. Use /youtube ajuda.");
    }

    public static void MostrarStatus(
        ChatController chat,
        PlayerControl destinatario = null)
    {
        if (chat == null)
            return;

        string id = string.IsNullOrWhiteSpace(LiveId.Value)
            ? "não definido"
            : LiveId.Value;

        ResponderComando(chat, destinatario,
            chat,
            "ID da live: " + id +
            " | Leitor funcionando: " + (ChatEmLeitura ? "sim" : "não") +
            " | Votação ativa: " + (VotacaoAtiva.Value ? "sim" : "não") +
            " | Tempo: " + TempoVotacao.Value + " segundos."
        );
    }
 
    // ========================================================
    // COMANDO /color (ID OU NOME)
    // ========================================================

    public static void ProcessarComandoCor(
        ChatController chat,
        PlayerControl jogador,
        string comando)
    {
        if (chat == null || jogador == null || Palette.ColorNames == null)
            return;

        string argumento = comando.Length > "/color".Length
            ? comando.Substring("/color".Length).Trim()
            : "";

        if (argumento.Length == 0)
        {
            ResponderComando(chat, jogador,
                "Use: /color (id/nome). Exemplo: /color 0 ou /color Red.");
            return;
        }

        int idCor = -1;
        if (int.TryParse(argumento, out int id))
        {
            if (id >= 0 && id < Palette.ColorNames.Length)
                idCor = id;
        }
        else
        {
            for (int i = 0; i < Palette.ColorNames.Length; i++)
            {
                string nomeCor = Palette.ColorNames[i];
                if (!string.IsNullOrWhiteSpace(nomeCor) &&
                    string.Equals(nomeCor.Trim(), argumento, StringComparison.OrdinalIgnoreCase))
                {
                    idCor = i;
                    break;
                }
            }
        }

        if (idCor < 0)
        {
            string lista = string.Join(", ",
                Enumerable.Range(0, Palette.ColorNames.Length)
                    .Select(i => i + "=" + Palette.ColorNames[i]));
            ResponderComando(chat, jogador,
                "Cor inválida. Use um ID de 0 a " + (Palette.ColorNames.Length - 1) +
                " ou um nome exato. Cores: " + lista);
            return;
        }

        try
        {
            jogador.RpcSetColor((byte)idCor);
            ResponderComando(chat, jogador,
                "Sua cor foi alterada para " + Palette.ColorNames[idCor] + " (ID " + idCor + ").");
            Instance?.Log.LogInfo(
                $"/color executado por PlayerId={jogador.PlayerId}, cor={idCor} ({Palette.ColorNames[idCor]}).");
        }
        catch (Exception ex)
        {
            Instance?.Log.LogError("Erro ao executar /color: " + ex);
            ResponderComando(chat, jogador, "Não foi possível alterar a cor: " + ex.Message);
        }
    }

    // ========================================================
    // AVISOS DE ENTRADA
    // ========================================================

    public static void PrepararAvisoEntrada(int clientId)
    {
        if (!EhHost())
            return;

        // Reinicia o estado do cliente e aguarda o PlayerControl ser criado.
        AvisosPendentes.Remove(clientId);
        AvisosEnviados.Remove(clientId);
        AvisosPendentes[clientId] = Time.realtimeSinceStartup + 2f;
        Instance?.Log.LogInfo(
            $"Cliente {clientId} entrou; aviso de sala modificada agendado para daqui a 2 segundos.");
    }

    public static void RegistrarEntrada(int clientId)
    {
        if (!EhHost() || AvisosEnviados.Contains(clientId) ||
            AvisosPendentes.ContainsKey(clientId))
            return;

        AvisosPendentes[clientId] = Time.realtimeSinceStartup + 2f;
        Instance?.Log.LogInfo(
            $"Entrada do cliente {clientId} registrada; aviso de sala modificada agendado.");
    }

    public static void AtualizarAvisos()
    {
        if (!EhHost() || AmongUsClient.Instance == null ||
            AvisosPendentes.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        foreach (var aviso in AvisosPendentes.ToArray())
        {
            if (agora < aviso.Value)
                continue;

            int clientId = aviso.Key;
            AvisosPendentes.Remove(clientId);

            if (AvisosEnviados.Contains(clientId))
                continue;

            PlayerControl jogadorAlvo = null;
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador != null && jogador.OwnerId == clientId &&
                    jogador.Data != null)
                {
                    jogadorAlvo = jogador;
                    break;
                }
            }

            if (jogadorAlvo == null)
            {
                // Aguarda a criação do jogador se o objeto ainda não estiver disponível.
                AvisosPendentes[clientId] = agora + 1f;
                Instance?.Log.LogWarning(
                    $"Jogador do cliente {clientId} ainda não está disponível; tentando novamente em 1 segundo.");
                continue;
            }

            string nomeJogador = jogadorAlvo.Data.PlayerName;
            string mensagem =
                $"Olá {nomeJogador}, esta é uma sala modificada pelo YouTubeVoteBridge. " +
                "A live do YouTube terá votações, e o chat poderá interagir com a partida por meio de eventos.";

            EnviarMensagemIndividual(jogadorAlvo, mensagem);
            AvisosEnviados.Add(clientId);
            Instance?.Log.LogInfo(
                $"Aviso de sala modificada enviado após a entrada do cliente {clientId} (jogador '{nomeJogador}').");
        }
    }
}


// ============================================================
// INTERCEPTAÇÃO DO COMANDO /youtube
// ============================================================

[HarmonyPatch(typeof(ChatController), nameof(ChatController.AddChat))]
public static class YouTubeVoteChatPatch
{
    public static bool Prefix(
        ChatController __instance,
        PlayerControl sourcePlayer,
        string chatText,
        bool censor)
    {
        if (sourcePlayer == null || string.IsNullOrWhiteSpace(chatText))
            return true;

        string mensagem = chatText.Trim();
        bool ehLocal = sourcePlayer == PlayerControl.LocalPlayer;
        bool host = YouTubeVoteBridgePlugin.EhHost();

        if (mensagem.Equals("/color", StringComparison.OrdinalIgnoreCase) ||
            mensagem.StartsWith("/color ", StringComparison.OrdinalIgnoreCase))
        {
            if (host)
            {
                YouTubeVoteBridgePlugin.ProcessarComandoCor(__instance, sourcePlayer, mensagem);
                return false;
            }
            return true;
        }

        if (mensagem.Equals("/ytconfig", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/ytconfiguracoes", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/ytconfigurações", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/youtube config", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/youtube configuracoes", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/youtube configurações", StringComparison.OrdinalIgnoreCase))
        {
            if (ehLocal)
                YouTubeVoteBridgePlugin.Responder(__instance, PainelVotacaoRpc.ObterTextoConfiguracoes());
            else if (host)
                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, PainelVotacaoRpc.ObterTextoConfiguracoes());
            return false;
        }

        if (mensagem.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/ajuda", StringComparison.OrdinalIgnoreCase))
        {
            const string ajuda = "Comandos: /color (id/nome) | /help | /youtube ajuda | /youtube config | /tag update.";
            if (ehLocal)
                YouTubeVoteBridgePlugin.Responder(__instance, ajuda);
            else if (host)
                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, ajuda);
            return false;
        }

        if (mensagem.Equals("/tag update", StringComparison.OrdinalIgnoreCase))
        {
            if (!host)
                return true;

            LobbyTagManager.AtualizarTagsRemotas();
            if (ehLocal)
                YouTubeVoteBridgePlugin.Responder(__instance, "Atualização das tags solicitada. Aguarde o download do GitHub.");
            else
                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, "Atualização das tags solicitada. Aguarde o download do GitHub.");
            return false;
        }

        if (!mensagem.StartsWith("/youtube", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!host)
        {
            string argumentosLocal = mensagem.Length > "/youtube".Length
                ? mensagem.Substring("/youtube".Length).Trim()
                : "";
            bool consultaLocal = argumentosLocal.Equals("config", StringComparison.OrdinalIgnoreCase) ||
                argumentosLocal.Equals("configuracoes", StringComparison.OrdinalIgnoreCase) ||
                argumentosLocal.Equals("configurações", StringComparison.OrdinalIgnoreCase) ||
                argumentosLocal.Equals("ajuda", StringComparison.OrdinalIgnoreCase) ||
                argumentosLocal.Equals("status", StringComparison.OrdinalIgnoreCase) ||
                argumentosLocal.Equals("info", StringComparison.OrdinalIgnoreCase);

            if (ehLocal && consultaLocal)
            {
                YouTubeVoteBridgePlugin.ProcessarComando(__instance, mensagem);
                return false;
            }

            // Cliente modificado não-host encaminha comandos ao host para execução.
            return true;
        }

        // No host, comandos enviados por clientes vanilla são tratados aqui.
        YouTubeVoteBridgePlugin.ProcessarComando(__instance, mensagem, ehLocal ? null : sourcePlayer);
        return false;
    }
}
// ============================================================
// AVISO PRIVADO QUANDO UM JOGADOR ENTRA
// ============================================================

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
public static class YouTubeVotePlayerJoinedPatch
{
    public static void Postfix([HarmonyArgument(0)] ClientData client)
    {
        if (client == null ||
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
            return;

        YouTubeVoteBridgePlugin.PrepararAvisoEntrada(client.Id);
    }
}


// ============================================================
// ATUALIZAÇÃO DOS AVISOS
// ============================================================

// Não interceptamos ReportDeadBody nem o RPC recebido de reporte.
// Bloquear esses métodos impedia o jogo de abrir a reunião após o reporte.

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
public static class YouTubeVoteCancelMeetingPatch
{
    public static bool Prefix()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return true;
        EventoTsunami.PrepararAntesDaReuniao();
        YouTubeVoteBridgePlugin.CancelarReuniaoSolicitada();
        EventoTarefaMataJogador.Desativar();
        EventoTarefaSuicidio.Desativar();
        // Deixa a chamada original continuar depois de restaurar os nomes.
        // Retornar false aqui cancelava a reunião e não havia um segundo RPC.
        return true;
    }
}
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcStartMeeting))]
public static class YouTubeVoteCancelRpcStartMeetingPatch
{
    public static bool Prefix()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return true;
        YouTubeVoteBridgePlugin.CancelarReuniaoSolicitada();
        // Não bloquear o RPC: ele é a chamada que efetivamente abre a reunião.
        return true;
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
public static class YouTubeVoteUpdatePatch
{
    public static void Postfix()
    {
        LobbyTagManager.Update();

        DiagnosticoPlayerReal.Atualizar();
        JogadorRealExperimental.Atualizar();
        EventoTesteMenu.VerificarTecla();
        // O menu pode ser aberto no lobby, mas os eventos de teste só executam em partida.
        if (LobbyTagManager.PartidaIniciada())
            EventoTesteMenu.ProcessarFilaExecucao();
        // Não enviar RPC customizado 248 para hosts vanilla: eles não conhecem esse RPC.
        YouTubeVoteBridgePlugin.AtualizarAvisos();
        FakePlayerAPI.Atualizar();
        NomeNpcRpc.Atualizar();
        YouTubeVoteBridgePlugin.AtualizarVotacao();
        EventoEscudoTemporario.Atualizar();
        EventoCooldownIndividual.Atualizar();
        EventoCongelarJogador.Atualizar();
        EventoVelocidadeJogadores.Atualizar();
        EventoTsunami.Atualizar();
        EventoVisaoReduzida.Atualizar();
        EventoTrocarIdentidades.Atualizar();
    }
}


// ============================================================
// SUPORTE A CTRL+V
// ============================================================

[HarmonyPatch(typeof(TextBoxTMP), nameof(TextBoxTMP.Update))]
public static class YouTubeVotePastePatch
{
    private const int LIMITE_CARACTERES = 100;

    public static int UltimoFrameColagem = -1;

    public static void Prefix(TextBoxTMP __instance)
    {
        if (!__instance.hasFocus)
            return;

        bool ctrlPressionado =
            Input.GetKey(KeyCode.LeftControl) ||
            Input.GetKey(KeyCode.RightControl);

        if (!ctrlPressionado || !Input.GetKeyDown(KeyCode.V))
            return;

        string clipboard = GUIUtility.systemCopyBuffer ?? "";

        if (string.IsNullOrEmpty(clipboard))
            return;

        try
        {
            string textoAtual = __instance.text ?? "";

            int espacoDisponivel = LIMITE_CARACTERES - textoAtual.Length;

            if (espacoDisponivel <= 0)
            {
                UltimoFrameColagem = Time.frameCount;
                return;
            }

            if (clipboard.Length > espacoDisponivel)
                clipboard = clipboard.Substring(0, espacoDisponivel);

            __instance.SetText(textoAtual + clipboard);
            UltimoFrameColagem = Time.frameCount;
        }
        catch (Exception ex)
        {
            if (YouTubeVoteBridgePlugin.Instance != null)
            {
                YouTubeVoteBridgePlugin.Instance.Log.LogError(
                    "Erro ao colar texto: " + ex
                );
            }
        }
    }
}


[HarmonyPatch(typeof(PlayerControl), "CompleteTask")]
public static class YouTubeVoteTaskKillPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        EventoTarefaMataJogador.AoCompletarTarefa(__instance);
        EventoTarefaSuicidio.AoCompletarTarefa(__instance);
    }
}


// ============================================================
// MENSAGENS DE JOGADORES COM TAG
// ============================================================
// Modifica a mensagem antes de RpcSendChat transmiti-la pela rede.
// Jogadores sem tag não são afetados.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSendChat))]
public static class YouTubeVoteTaggedChatPatch
{
    public static void Prefix(PlayerControl __instance, ref string chatText)
    {
        try
        {
            if (__instance == null ||
                string.IsNullOrEmpty(chatText) ||
                !LobbyTagManager.TemTag(__instance))
                return;

            // Coloca a quebra de linha em tamanho pequeno antes da mensagem.
            // Exemplo: <size=50%>\n</size>Mensagem
            const string prefixo = "<size=50%>\n</size>";
            if (!chatText.StartsWith(prefixo, StringComparison.Ordinal))
                chatText = prefixo + chatText;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível formatar mensagem de jogador com tag: " + ex.Message);
        }
    }
}

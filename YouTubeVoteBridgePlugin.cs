using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
using Il2CppInterop.Runtime.InteropTypes.Arrays;

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
    public static ConfigEntry<bool> SemFimDeJogo;

    private static bool LeitorConectado;
    private static bool PreparacaoIniciada;
    private static bool PreparacaoConcluida;
    private static float FimPreparacaoChat;
    private static float ProximaRodadaPermitida;

    private const string PREFIXO = "[YouTubeVoteBridge] ";
    private const string TITULO_SISTEMA =
        "<color=blue>YouTubeChatBridge</color>";

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
    // Snapshot das tarefas antes do Shapeshift, para preservar as concluídas se a lista for recriada.
    private sealed class EstadoTarefaShapeshift
    {
        public string Tipo;
        public int Ocorrencia;
        public bool Concluida;
    }
    private static readonly Dictionary<byte, List<EstadoTarefaShapeshift>> TarefasAntesDoShapeshift = new();
    // Registro explícito de cada conclusão: qual jogador concluiu qual tarefa.
    // Guardamos IDs e tipo em vez de manter uma referência IL2CPP que pode ser recriada pelo jogo.
    private sealed class RegistroConclusaoTarefa
    {
        public byte PlayerId;
        public uint TaskId;
        public string Tipo;
        public int Ocorrencia;
        public string NomeJogador;
    }
    private static readonly List<RegistroConclusaoTarefa> HistoricoConclusoesTarefas = new();
    private static readonly HashSet<byte> RestaurandoTarefasShapeshift = new();
    // Só tenta reaplicar tarefas quando o jogador retorna à própria aparência.
    private static readonly HashSet<byte> AguardandoRestauracaoTarefasShapeshift = new();
    public static bool RestaurandoTarefasDoShapeshift(byte playerId) =>
        RestaurandoTarefasShapeshift.Contains(playerId);

    public static void RegistrarDestransformacaoShapeshifter(PlayerControl jogador)
    {
        if (jogador == null) return;
        AguardandoRestauracaoTarefasShapeshift.Add(jogador.PlayerId);
        Instance?.Log.LogInfo(
            $"Shapeshift: DEStransformação detectada para PlayerId={jogador.PlayerId} (RpcShapeshift recebeu o próprio jogador como alvo).");
    }

    private static readonly Dictionary<byte, string> NomesOriginaisPainel = new();
    private static string NomeHostOriginalPlacar;
    private static readonly Dictionary<byte, string> NomesOriginaisPlacarLocal = new();
    private static readonly HashSet<int> ClientesComMod = new();
    private static bool SinalModEnviado;
    // A transição é visual e detectada nos clientes pela sequência de cores.
    // Não enviamos RPC personalizado, para manter compatibilidade com clientes vanilla.
    private static void NotificarEstadoRainbowVisual(byte playerId, bool ativo)
    {
        RainbowColorTransition.DefinirAtivo(playerId, ativo);
    }

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

        SemFimDeJogo = Config.Bind(
            "Configurações do Mod", "Sem fim de jogo", false,
            "Impede os encerramentos automáticos originais do Among Us. Os encerramentos solicitados pelo próprio mod continuam permitidos."
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
        PlayerControl remetente = PlayerControl.LocalPlayer;
        if (chat == null || remetente == null || string.IsNullOrWhiteSpace(mensagem))
            return;

        string nomeOriginal = remetente.Data != null
            ? remetente.Data.PlayerName
            : remetente.name;

        try
        {
            // Mesmo padrão usado pelo EHR em Utils.SendMessage:
            // o título vira o nome do remetente somente enquanto o chat cria a entrada.
            remetente.SetName(TITULO_SISTEMA);
            chat.AddChat(remetente, mensagem, false);
        }
        finally
        {
            remetente.SetName(nomeOriginal);
        }
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

        // O EHR faz a troca do nome somente no cliente destinatário:
        // SetName(TITULO) -> SendChat(texto) -> SetName(nome original),
        // tudo no mesmo pacote GameDataTo direcionado ao cliente.
        if (destinatario.AmOwner)
        {
            if (HudManager.InstanceExists && HudManager.Instance.Chat != null)
                Responder(HudManager.Instance.Chat, mensagem);
            return;
        }

        MessageWriter writer = null;
        string nomeOriginalRemetente = remetente.Data.PlayerName;

        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo: apenas o cliente destinatário
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(targetClientId);

            // 1. Define o título como nome apenas na tela do destinatário.
            writer.StartMessage(2);
            writer.WritePacked(remetente.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(remetente.Data.NetId);
            writer.Write(TITULO_SISTEMA);
            writer.EndMessage();

            // 2. Envia o texto da resposta com o título já exibido como remetente.
            writer.StartMessage(2);
            writer.WritePacked(remetente.NetId);
            writer.Write((byte)RpcCalls.SendChat);
            writer.Write(mensagem);
            writer.EndMessage();

            // 3. Restaura o nome original antes de terminar o mesmo pacote.
            writer.StartMessage(2);
            writer.WritePacked(remetente.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(remetente.Data.NetId);
            writer.Write(nomeOriginalRemetente);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;

            Instance?.Log.LogInfo(
                $"Mensagem individual enviada para PlayerId={destinatario.PlayerId}, ClientId={targetClientId} usando SetName/SendChat/SetName direcionado.");
        }
        catch (Exception ex)
        {
            Instance?.Log.LogError("Erro ao enviar mensagem individual via GameDataTo: " + ex);
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }    }

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

        if (!PodeAlterarNomeDuranteEjecao(jogador, nome))
            return;

        try
        {
            if (jogador.AmOwner)
            {
                // SetName atualiza os dados do jogador, mas a UI pode manter
                // o texto anterior durante atualizações de nome em tempo real.
                jogador.SetName(nome);
                try
                {
                    if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                        jogador.cosmetics.nameText.text = nome;
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogWarning(
                        $"Não foi possível atualizar o texto visual do próprio nome: {ex.Message}");
                }
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

            // Atualiza apenas o nome visto pelo cliente dono do jogador.
            // Este é o mesmo fluxo GameDataTo + SetName usado no aviso do tsunami:
            // não altera o nome global nem os outros clientes.
            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(jogador.OwnerId);

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
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Recycle(); } catch { }
                }
            }
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
                case "Adicionar 1 tarefa(s) a todos":
                    new EventoAdicionarTarefas(1, true).Executar();
                    break;
                case "Adicionar 5 tarefa(s) a todos":
                    new EventoAdicionarTarefas(5, true).Executar();
                    break;
                case "Adicionar 10 tarefa(s) a todos":
                    new EventoAdicionarTarefas(10, true).Executar();
                    break;
                case "Adicionar 1 tarefa(s) a um jogador aleatório":
                    new EventoAdicionarTarefas(1, false).Executar();
                    break;
                case "Adicionar 5 tarefa(s) a um jogador aleatório":
                    new EventoAdicionarTarefas(5, false).Executar();
                    break;
                case "Adicionar 10 tarefa(s) a um jogador aleatório":
                    new EventoAdicionarTarefas(10, false).Executar();
                    break;
                case "Completar 1 tarefa para todos":
                    new EventoCompletarTarefa(true).Executar();
                    break;
                case "Completar 1 tarefa para um jogador aleatório":
                    new EventoCompletarTarefa(false).Executar();
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
            ResponderComando(chat, destinatario, "Comandos: /youtube live (usa ID copiado) | colar | status | iniciar | parar | tempo 40 | intervalo 15 | preparacao 10 | config"
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
                    ResponderComando(chat, destinatario, "Defina primeiro o ID: /youtube ID ou /youtube colar."
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

                ResponderComando(chat, destinatario, "Duração da votação definida para " +
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
                    ResponderComando(chat, destinatario, "Não encontrei um ID válido de live na área de transferência."
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

            ResponderComando(chat, destinatario, "ID da live definido: " + id +
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

        ResponderComando(chat, destinatario, "ID da live: " + id +
            " | Leitor funcionando: " + (ChatEmLeitura ? "sim" : "não") +
            " | Votação ativa: " + (VotacaoAtiva.Value ? "sim" : "não") +
            " | Tempo: " + TempoVotacao.Value + " segundos."
        );
    }
 
    // ========================================================
    // COMANDO /color (ID, NOME OU RAINBOW)
    // ========================================================

    // O arco-íris pertence individualmente a cada jogador que usou /color rainbow.
    // O efeito fica ativo até o fim da partida, sem temporizador de expiração.
    private static bool RainbowGlobalAtivo => JogadoresRainbowAtivos.Count > 0;
    private static float ProximaTrocaCorRainbow;
    private static int IndiceCorRainbow;
    private static readonly Dictionary<byte, int> IndicesCorRainbowPorJogador = new();
    private static readonly HashSet<byte> JogadoresRainbowAtivos = new();
    private static readonly Dictionary<byte, int> CoresOriginaisRainbow = new();
    private static readonly Dictionary<byte, string> NomesOriginaisRainbow = new();
    // Shapeshifters rainbow com transformação ativa: não recebem RpcSetColor
    // do ciclo até que o host processe e replique a destransformação.
    private static readonly HashSet<byte> ShapeshiftersRainbowTransformados = new();
    private static readonly HashSet<byte> CoresRainbowRestauradasPorDestransformacao = new();
    private static readonly Dictionary<byte, string> NomesRainbowTemporariosPorShapeshift = new();
    private sealed class EstadoShapeshiftRainbow
    {
        public int CorOriginal;
        public string NomeOriginal;
        public bool EraRainbow;
        public bool AlvoEraRainbow;
    }
    private static readonly Dictionary<byte, EstadoShapeshiftRainbow> EstadosShapeshiftRainbow = new();
    private static bool EnviandoRpcDestransformacaoRainbow;
    private static bool ReenviandoRpcShapeshiftPreparado;
    private static bool BloqueioCompletoShapeshift;

    // API pública para eventos: true bloqueia completamente transformações e destransformações.
    public static bool ShapeshiftBloqueado => BloqueioCompletoShapeshift;
    public static void DefinirBloqueioCompletoShapeshift(bool bloquear)
    {
        BloqueioCompletoShapeshift = bloquear;
        Instance?.Log.LogInfo("Shapeshift: bloqueio completo " + (bloquear ? "ATIVADO" : "desativado") + ".");
    }
    private static readonly string[] CoresTextoRainbow =
    {
        "#FF4040", "#FF9A32", "#FFE64A", "#55E66A", "#42D9E8",
        "#4387FF", "#A45BFF", "#F05BCE", "#FF4040"
    };

    private static string CriarNomeRainbow(string nomeOriginal, int? indiceJogador = null)
    {
        const string prefixo = "Arco-íris";
        StringBuilder nome = new();
        int indice = indiceJogador ?? IndiceCorRainbow;
        for (int i = 0; i < prefixo.Length; i++)
        {
            string cor = CoresTextoRainbow[(i + indice) % CoresTextoRainbow.Length];
            nome.Append("<color=").Append(cor).Append(">").Append(prefixo[i]).Append("</color>");
        }
        nome.Append(" - ").Append(nomeOriginal ?? string.Empty);
        return nome.ToString();
    }

    // Envia SetName em GameData (broadcast), para TODOS os clientes,
    // incluindo vanilla. Assim, o nome personalizado não depende da detecção do mod.
    private static void DefinirNomeRainbowNosClientesComMod(PlayerControl jogador, string nome)
    {
        if (jogador == null || jogador.Data == null || AmongUsClient.Instance == null)
            return;
        if (!PodeAlterarNomeDuranteEjecao(jogador, nome))
            return;

        if (!EhHost())
        {
            // O host controla as alterações globais de nome.
            try
            {
                if (jogador.AmOwner)
                    jogador.SetName(nome);
                if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                    jogador.cosmetics.nameText.text = nome;
            }
            catch (Exception ex)
            {
                Instance?.Log.LogDebug("Rainbow: falha ao atualizar nome local: " + ex.Message);
            }
            return;
        }

        MessageWriter writer = null;
        try
        {
            // Tipo 5 = GameData, transmitido para todos os clientes da sala.
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.StartMessage(2); // RPC
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();
            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;

            // Atualiza os dados e a interface local do host para qualquer jogador,
            // inclusive quando o jogador ejetado pertence a outro cliente.
            // O broadcast acima continua responsável por replicar o nome para a sala.
            try
            {
                jogador.SetName(nome);
            }
            catch (Exception exSetName)
            {
                Instance?.Log.LogWarning(
                    $"Ejeção/Rainbow: SetName local falhou para PlayerId={jogador.PlayerId}: {exSetName.Message}");
            }

            try
            {
                if (jogador.Data != null)
                    jogador.Data.PlayerName = nome;
                if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                    jogador.cosmetics.nameText.text = nome;
            }
            catch (Exception exVisual)
            {
                Instance?.Log.LogWarning(
                    $"Ejeção/Rainbow: atualização visual local falhou para PlayerId={jogador.PlayerId}: {exVisual.Message}");
            }

            Instance?.Log.LogInfo(
                $"Ejeção/Rainbow: SetName enviado globalmente e aplicado localmente; PlayerId={jogador.PlayerId}, nome='{nome}'.");
        }
        catch (Exception ex)
        {
            Instance?.Log.LogWarning("Rainbow: falha ao enviar SetName para todos os clientes: " + ex);
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
    }

    private static void IniciarRainbowGlobal(PlayerControl jogador)
    {
        if (!EhHost() || jogador == null || jogador.Data == null ||
            jogador.Data.Disconnected)
            return;

        byte playerId = jogador.PlayerId;
        if (!JogadoresRainbowAtivos.Contains(playerId))
        {
            CoresOriginaisRainbow[playerId] = jogador.Data.DefaultOutfit.ColorId;
            NomesOriginaisRainbow[playerId] = jogador.Data.PlayerName ?? string.Empty;
            JogadoresRainbowAtivos.Add(playerId);
        }

        NotificarEstadoRainbowVisual(playerId, true);
        CoresRainbowRestauradasPorDestransformacao.Remove(playerId);
        if (!IndicesCorRainbowPorJogador.ContainsKey(playerId))
            IndicesCorRainbowPorJogador[playerId] = UnityEngine.Random.Range(0, Math.Max(1, Palette.ColorNames?.Length ?? 1));
        ProximaTrocaCorRainbow = 0f;
        Instance?.Log.LogInfo($"Rainbow ativado individualmente para PlayerId={playerId}; permanece ativo até o fim da partida.");
    }

    private static void EncerrarRainbowJogador(PlayerControl jogador)
    {
        if (jogador == null)
            return;

        byte playerId = jogador.PlayerId;
        if (!JogadoresRainbowAtivos.Remove(playerId))
            return;

        NotificarEstadoRainbowVisual(playerId, false);

        if (CoresOriginaisRainbow.TryGetValue(playerId, out int cor))
        {
            try { jogador.RpcSetColor((byte)Math.Clamp(cor, 0, 255)); }
            catch (Exception ex)
            {
                Instance?.Log.LogWarning("Rainbow: não foi possível restaurar cor: " + ex.Message);
            }
        }

        if (NomesOriginaisRainbow.TryGetValue(playerId, out string nome))
            DefinirNomeRainbowNosClientesComMod(jogador, nome);

        CoresOriginaisRainbow.Remove(playerId);
        NomesOriginaisRainbow.Remove(playerId);
        CoresRainbowRestauradasPorDestransformacao.Remove(playerId);
        IndicesCorRainbowPorJogador.Remove(playerId);
        ShapeshiftersRainbowTransformados.Remove(playerId);
        NomesRainbowTemporariosPorShapeshift.Remove(playerId);
        EstadosShapeshiftRainbow.Remove(playerId);
    }

    private static void EncerrarRainbowGlobal()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && JogadoresRainbowAtivos.Contains(jogador.PlayerId))
                EncerrarRainbowJogador(jogador);
        }

        // Remove também os estados de jogadores que saíram e não estão mais
        // em AllPlayerControls, para não deixar animações órfãs nos clientes.
        foreach (byte playerId in JogadoresRainbowAtivos.ToArray())
            NotificarEstadoRainbowVisual(playerId, false);

        JogadoresRainbowAtivos.Clear();
        CoresOriginaisRainbow.Clear();
        NomesOriginaisRainbow.Clear();
        CoresRainbowRestauradasPorDestransformacao.Clear();
        ShapeshiftersRainbowTransformados.Clear();
        NomesRainbowTemporariosPorShapeshift.Clear();
        EstadosShapeshiftRainbow.Clear();
        IndicesCorRainbowPorJogador.Clear();
        BloqueioCompletoShapeshift = false;
        ProximaTrocaCorRainbow = 0f;
        Instance?.Log.LogInfo("Rainbow encerrado; cores e nomes originais restaurados.");
    }

    public static void ResetarRainbowFimDePartida()
    {
        if (EhHost())
            EncerrarRainbowGlobal();
    }

    // Registra transformações recebidas pelo host com log explícito para diagnóstico.
    public static void RegistrarShapeshiftRecebido(PlayerControl jogador, PlayerControl alvo)
    {
        if (jogador == null || alvo == null || !JogadoresRainbowAtivos.Contains(jogador.PlayerId))
            return;

        if (alvo.PlayerId != jogador.PlayerId)
        {
            PausarRainbowDuranteShapeshift(jogador);
            Instance?.Log.LogInfo(
                $"Rainbow: transformação detectada para PlayerId={jogador.PlayerId}, alvo={alvo.PlayerId}; ciclo pausado.");
        }
        else
        {
            ShapeshiftersRainbowTransformados.Remove(jogador.PlayerId);
            Instance?.Log.LogInfo(
                $"Rainbow: destransformação detectada para PlayerId={jogador.PlayerId}; ciclo retomado.");
        }
    }

    // Troca temporariamente o nome global do jogador ejetado pelo texto de revelação.
    private static readonly Dictionary<byte, string> NomesAntesDaEjecao = new();
    private static byte? PlayerIdNaEjecao;
    private static string NomeTemporarioEjecao;

    // Enquanto a ejeção está ativa, nenhuma outra rotina pode sobrescrever
    // o nome do jogador ejetado. O nome temporário e o nome original são permitidos.
    public static bool PodeAlterarNomeDuranteEjecao(PlayerControl jogador, string nome)
    {
        if (jogador == null || !PlayerIdNaEjecao.HasValue ||
            jogador.PlayerId != PlayerIdNaEjecao.Value)
            return true;

        bool permitido = string.Equals(nome, NomeTemporarioEjecao, StringComparison.Ordinal) ||
            (NomesAntesDaEjecao.TryGetValue(PlayerIdNaEjecao.Value, out string original) &&
             string.Equals(nome, original, StringComparison.Ordinal));

        if (!permitido)
            Instance?.Log.LogInfo(
                $"Ejeção: alteração de nome bloqueada para PlayerId={jogador.PlayerId}; nome solicitado='{nome}'.");
        return permitido;
    }

    private static object LerMembro(object objeto, params string[] nomes)
    {
        if (objeto == null) return null;
        Type tipo = objeto.GetType();
        foreach (string nome in nomes)
        {
            try
            {
                PropertyInfo p = tipo.GetProperty(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(objeto);
                FieldInfo f = tipo.GetField(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(objeto);
            }
            catch { }
        }
        return null;
    }

    public static object LerMembroPublico(object objeto, params string[] nomes)
    {
        return LerMembro(objeto, nomes);
    }

    public static void PrepararNomeGlobalDeEjecao(object playerInfo)
    {
        if (!EhHost() || playerInfo == null) return;
        try
        {
            object infoJogador = playerInfo;
            object idValor = LerMembro(infoJogador, "PlayerId", "playerId", "PlayerID");
            if (idValor == null)
            {
                // ExileController.Begin recebe InitProperties; os dados do jogador
                // normalmente ficam numa propriedade interna como exiled.
                foreach (string membro in new[] {
                    "exiled", "Exiled", "networkedPlayer", "NetworkedPlayer",
                    "playerInfo", "PlayerInfo", "networkedPlayerInfo", "NetworkedPlayerInfo",
                    "player", "Player", "target", "Target"
                })
                {
                    object candidato = LerMembro(playerInfo, membro);
                    if (candidato == null) continue;
                    object idCandidato = LerMembro(candidato, "PlayerId", "playerId", "PlayerID");
                    Instance?.Log.LogInfo(
                        $"Ejeção diagnóstico: membro {membro} tipo={candidato.GetType().FullName}, PlayerId={idCandidato ?? "não encontrado"}.");
                    if (idCandidato != null)
                    {
                        infoJogador = candidato;
                        idValor = idCandidato;
                        break;
                    }
                }
            }
            if (idValor == null)
            {
                try
                {
                    string membros = string.Join(", ", playerInfo.GetType()
                        .GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property)
                        .Select(m => m.Name)
                        .Distinct());
                    Instance?.Log.LogWarning(
                        $"Ejeção diagnóstico: membros disponíveis em {playerInfo.GetType().FullName}: {membros}");
                }
                catch { }
                return;
            }
            byte id = Convert.ToByte(idValor);
            PlayerControl jogador = null;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.PlayerId == id) { jogador = p; break; }
            if (jogador == null || jogador.Data == null) return;

            object role = LerMembro(infoJogador, "Role", "role");
            if (role == null)
                role = LerMembro(jogador.Data, "Role", "role");
            if (role == null)
                role = LerMembro(jogador, "Role", "role");
            object roleDetalhe = LerMembro(role, "Role", "role", "RoleType", "roleType");
            string nomeFuncao = roleDetalhe?.ToString();
            if (string.IsNullOrWhiteSpace(nomeFuncao))
                nomeFuncao = role?.ToString();
            if (string.IsNullOrWhiteSpace(nomeFuncao) || nomeFuncao == "None")
                nomeFuncao = "Crewmate";
            bool impostor = nomeFuncao.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nomeFuncao.IndexOf("Shapeshifter", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            nomeFuncao.IndexOf("Phantom", StringComparison.OrdinalIgnoreCase) >= 0;
            string cor = impostor ? "red" : "#00fffb";

            string nomeOriginal = NomesOriginaisRainbow.TryGetValue(id, out string nomeRainbow)
                ? nomeRainbow : (jogador.Data.PlayerName ?? string.Empty);
            if (!NomesAntesDaEjecao.ContainsKey(id))
                NomesAntesDaEjecao[id] = nomeOriginal;
            PlayerIdNaEjecao = id;

            string nomeEjecao = $"{nomeOriginal} era <color={cor}>{nomeFuncao}</color><size=0>";
            NomeTemporarioEjecao = nomeEjecao;
            DefinirNomeRainbowNosClientesComMod(jogador, nomeEjecao);

            // Reforça a atualização visual local no host antes de a tela aparecer.
            try
            {
                if (jogador.AmOwner)
                    jogador.SetName(nomeEjecao);
                if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                    jogador.cosmetics.nameText.text = nomeEjecao;
            }
            catch (Exception exLocal)
            {
                Instance?.Log.LogWarning("Ejeção: falha ao atualizar o nome local do host: " + exLocal.Message);
            }
            Instance?.Log.LogInfo($"Ejeção: nome global definido ANTES da tela; PlayerId={id}, função={nomeFuncao}.");
        }
        catch (Exception ex)
        {
            Instance?.Log.LogWarning("Ejeção: falha ao preparar nome global: " + ex);
        }
    }

    public static void RestaurarNomeGlobalDeEjecao()
    {
        if (!EhHost() || !PlayerIdNaEjecao.HasValue) return;
        byte id = PlayerIdNaEjecao.Value;
        try
        {
            PlayerControl jogador = null;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.PlayerId == id) { jogador = p; break; }
            if (jogador != null && NomesAntesDaEjecao.TryGetValue(id, out string nome))
                DefinirNomeRainbowNosClientesComMod(jogador, nome);
            Instance?.Log.LogInfo($"Ejeção: nome restaurado após a tela; PlayerId={id}.");
        }
        catch (Exception ex)
        {
            Instance?.Log.LogWarning("Ejeção: falha ao restaurar nome: " + ex);
        }
        NomesAntesDaEjecao.Remove(id);
        PlayerIdNaEjecao = null;
        NomeTemporarioEjecao = null;
    }

    // Il2CppSystem.Collections.Generic.List<T> não implementa necessariamente
    // System.Collections.IEnumerable do .NET. Acessamos Count/Item por reflexão.
    private static List<object> EnumerarColecaoIl2Cpp(object colecao)
    {
        var itens = new List<object>();
        if (colecao == null) return itens;

        try
        {
            Type tipo = colecao.GetType();
            object countObj = LerMembro(colecao, "Count", "count", "Length", "length");
            if (countObj != null)
            {
                int count = Convert.ToInt32(countObj);
                PropertyInfo indexador = tipo.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo getItem = indexador?.GetGetMethod(true);
                if (getItem != null)
                {
                    for (int i = 0; i < count; i++)
                    {
                        try { itens.Add(getItem.Invoke(colecao, new object[] { i })); }
                        catch (Exception ex)
                        {
                            Instance?.Log.LogWarning($"Shapeshift/tarefas: falha lendo índice {i} da coleção IL2CPP: {ex.GetBaseException().Message}");
                            break;
                        }
                    }
                    return itens;
                }
            }

            // Fallback para coleções que exponham GetEnumerator, sem depender da interface .NET.
            MethodInfo getEnumerator = tipo.GetMethod("GetEnumerator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object enumerador = getEnumerator?.Invoke(colecao, null);
            if (enumerador != null)
            {
                MethodInfo moveNext = enumerador.GetType().GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                PropertyInfo current = enumerador.GetType().GetProperty("Current", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (moveNext != null && current != null)
                {
                    while (Convert.ToBoolean(moveNext.Invoke(enumerador, null)))
                        itens.Add(current.GetValue(enumerador));
                }
            }
        }
        catch (Exception ex)
        {
            Instance?.Log.LogWarning("Shapeshift/tarefas: erro ao enumerar coleção IL2CPP: " + ex.GetBaseException().Message);
        }

        return itens;
    }

    private static List<EstadoTarefaShapeshift> CapturarEstadoTarefas(PlayerControl jogador)
    {
        var resultado = new List<EstadoTarefaShapeshift>();
        if (jogador == null) return resultado;

        object tarefasObj = LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks");
        List<object> tarefas = EnumerarColecaoIl2Cpp(tarefasObj);
        if (tarefas.Count == 0)
        {
            Instance?.Log.LogWarning(
                $"Shapeshift/tarefas: nenhuma tarefa enumerada para PlayerId={jogador.PlayerId}; tipo={(tarefasObj == null ? "null" : tarefasObj.GetType().FullName)}.");
            return resultado;
        }

        var ocorrencias = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object tarefa in tarefas)
        {
            if (tarefa == null) continue;
            string tipo = LerMembro(tarefa, "TaskType", "taskType", "Type", "type")?.ToString()
                ?? tarefa.GetType().FullName;
            ocorrencias.TryGetValue(tipo, out int ocorrencia);
            ocorrencias[tipo] = ocorrencia + 1;
            object completaObj = LerMembro(tarefa, "IsComplete", "isComplete", "Complete", "complete");
            bool concluida = false;
            try { if (completaObj != null) concluida = Convert.ToBoolean(completaObj); } catch { }
            // Marca a ocorrência como concluída somente se o registro pertence a este jogador.
            if (HistoricoConclusoesTarefas.Any(t =>
                t.PlayerId == jogador.PlayerId && t.Tipo == tipo && t.Ocorrencia == ocorrencia))
                concluida = true;
            resultado.Add(new EstadoTarefaShapeshift { Tipo = tipo, Ocorrencia = ocorrencia, Concluida = concluida });
        }

        Instance?.Log.LogInfo(
            $"Shapeshift/tarefas: snapshot PlayerId={jogador.PlayerId}, total={resultado.Count}, concluídas={resultado.Count(t => t.Concluida)}.");
        return resultado;
    }

    public static void RegistrarTarefaConcluida(PlayerControl jogador, uint taskId)
    {
        if (jogador == null)
            return;

        EventoAdicionarTarefas.RegistrarRpcConclusao(jogador, taskId);

        if (RestaurandoTarefasShapeshift.Contains(jogador.PlayerId))
            return;

        List<object> tarefas = EnumerarColecaoIl2Cpp(LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks"));
        var ocorrencias = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (object tarefa in tarefas)
        {
            if (tarefa == null) continue;
            string tipo = LerMembro(tarefa, "TaskType", "taskType", "Type", "type")?.ToString()
                ?? tarefa.GetType().FullName;
            ocorrencias.TryGetValue(tipo, out int ocorrencia);
            ocorrencias[tipo] = ocorrencia + 1;

            object idObj = LerMembro(tarefa, "Id", "id", "TaskId", "taskId");
            uint idAtual;
            try { idAtual = Convert.ToUInt32(idObj); } catch { continue; }
            if (idAtual != taskId) continue;

            // A mesma tarefa pode gerar callbacks repetidos. Não duplicamos o registro
            // da mesma combinação jogador + ID; tarefas distintas continuam separadas.
            bool jaRegistrada = HistoricoConclusoesTarefas.Any(t =>
                t.PlayerId == jogador.PlayerId && t.TaskId == taskId);
            if (!jaRegistrada)
            {
                HistoricoConclusoesTarefas.Add(new RegistroConclusaoTarefa
                {
                    PlayerId = jogador.PlayerId,
                    TaskId = taskId,
                    Tipo = tipo,
                    Ocorrencia = ocorrencia,
                    NomeJogador = jogador.Data?.PlayerName ?? jogador.name
                });
            }

            Instance?.Log.LogInfo(
                $"Shapeshift/tarefas: registro jogador={jogador.Data?.PlayerName ?? jogador.name}, PlayerId={jogador.PlayerId}, NetId={jogador.NetId}, taskId={taskId}, tipo={tipo}, ocorrência={ocorrencia}, duplicado={jaRegistrada}.");
            return;
        }

        Instance?.Log.LogWarning(
            $"Shapeshift/tarefas: GameData.CompleteTask recebeu PlayerId={jogador.PlayerId}, NetId={jogador.NetId}, taskId={taskId}, mas não encontrou essa tarefa na lista do jogador.");
    }

    public static void LimparRegistroTarefasShapeshift()
    {
        HistoricoConclusoesTarefas.Clear();
        EventoAdicionarTarefas.LimparHistoricoRpcConclusoes();
        TarefasAntesDoShapeshift.Clear();
        AguardandoRestauracaoTarefasShapeshift.Clear();
        RestaurandoTarefasShapeshift.Clear();
    }

    public static void RegistrarTarefasAntesDoShapeshift(PlayerControl jogador, PlayerControl alvo)
    {
        if (jogador == null || alvo == null || jogador.PlayerId == alvo.PlayerId) return;
        List<EstadoTarefaShapeshift> estado = CapturarEstadoTarefas(jogador);
        if (estado.Count == 0)
        {
            Instance?.Log.LogWarning(
                $"Shapeshift detectado para PlayerId={jogador.PlayerId}, mas não foi possível capturar tarefas; snapshot anterior preservado.");
            return;
        }

        TarefasAntesDoShapeshift[jogador.PlayerId] = estado;
        Instance?.Log.LogInfo(
            $"Shapeshift detectado: PlayerId={jogador.PlayerId} transformando-se em PlayerId={alvo.PlayerId}; snapshot de tarefas registrado.");
    }

    public static void RestaurarTarefasDepoisDeAtualizar(PlayerControl jogador)
    {
        if (jogador == null ||
            !AguardandoRestauracaoTarefasShapeshift.Contains(jogador.PlayerId) ||
            !TarefasAntesDoShapeshift.TryGetValue(jogador.PlayerId, out List<EstadoTarefaShapeshift> antigas))
            return;

        List<EstadoTarefaShapeshift> atuais = CapturarEstadoTarefas(jogador);
        if (atuais.Count == 0)
        {
            Instance?.Log.LogWarning(
                $"Shapeshift/tarefas: lista atual vazia para PlayerId={jogador.PlayerId}; snapshot mantido.");
            return;
        }

        int restauradas = 0;
        RestaurandoTarefasShapeshift.Add(jogador.PlayerId);
        try
        {
            foreach (EstadoTarefaShapeshift antiga in antigas.Where(t => t.Concluida))
            {
                EstadoTarefaShapeshift atual = atuais.FirstOrDefault(t =>
                    t.Tipo == antiga.Tipo && t.Ocorrencia == antiga.Ocorrencia);
                if (atual == null || atual.Concluida) continue;

                object tarefasObj = LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks");
                List<object> tarefas = EnumerarColecaoIl2Cpp(tarefasObj);
                object tarefaAlvo = null;
                int ocorrencia = 0;
                foreach (object tarefa in tarefas)
                {
                    if (tarefa == null) continue;
                    string tipo = LerMembro(tarefa, "TaskType", "taskType", "Type", "type")?.ToString()
                        ?? tarefa.GetType().FullName;
                    if (tipo != antiga.Tipo) continue;
                    if (ocorrencia++ == antiga.Ocorrencia) { tarefaAlvo = tarefa; break; }
                }

                object idTarefa = LerMembro(tarefaAlvo, "Id", "id", "TaskId", "taskId");
                // EHR usa RpcCompleteTask(task.Id), não CompleteTask:
                // o RPC marca a tarefa correta como concluída também no estado de jogo.
                MethodInfo completar = typeof(PlayerControl).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "RpcCompleteTask" && m.GetParameters().Length == 1)
                    ?? typeof(PlayerControl).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "CompleteTask" && m.GetParameters().Length == 1);
                if (idTarefa == null || completar == null)
                {
                    Instance?.Log.LogWarning(
                        $"Shapeshift/tarefas: ID ou método RpcCompleteTask/CompleteTask não encontrado para '{antiga.Tipo}'.");
                    continue;
                }

                try
                {
                    Type parametro = completar.GetParameters()[0].ParameterType;
                    object idConvertido = parametro.IsInstanceOfType(idTarefa)
                        ? idTarefa
                        : Convert.ChangeType(idTarefa, Nullable.GetUnderlyingType(parametro) ?? parametro);
                    completar.Invoke(jogador, new[] { idConvertido });
                    restauradas++;
                }
                catch (Exception ex)
                {
                    Instance?.Log.LogWarning(
                        $"Shapeshift/tarefas: falha ao restaurar '{antiga.Tipo}' para PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
                }
            }
        }
        finally
        {
            RestaurandoTarefasShapeshift.Remove(jogador.PlayerId);
        }

        Instance?.Log.LogInfo(
            $"Shapeshift/tarefas: PlayerId={jogador.PlayerId}; tarefas concluídas reaplicadas={restauradas}.");
        TarefasAntesDoShapeshift.Remove(jogador.PlayerId);
        AguardandoRestauracaoTarefasShapeshift.Remove(jogador.PlayerId);
    }

    public static void DefinirEstadoShapeshiftRainbow(
        PlayerControl jogador, bool transformado, PlayerControl alvo, bool shouldAnimate)
    {
        if (jogador == null || !JogadoresRainbowAtivos.Contains(jogador.PlayerId))
            return;
        if (transformado)
        {
            ShapeshiftersRainbowTransformados.Add(jogador.PlayerId);
            Instance?.Log.LogInfo($"Rainbow: RpcShapeshift (padrão EHR) detectou transformação PlayerId={jogador.PlayerId}, alvo={alvo?.PlayerId}; pausado.");
        }
        else if (ShapeshiftersRainbowTransformados.Remove(jogador.PlayerId))
        {
            Instance?.Log.LogInfo($"Rainbow: RpcShapeshift (padrão EHR) detectou destransformação PlayerId={jogador.PlayerId}; retomado.");
        }
    }

    public static void AtualizarRainbow()
    {
        // O arco-íris deve funcionar no lobby e durante a partida.
        // Apenas a decoração do nome fica restrita à partida.
        if (!EhHost() || !RainbowGlobalAtivo)
            return;

        if (Time.realtimeSinceStartup < ProximaTrocaCorRainbow)
            return;

        ProximaTrocaCorRainbow = Time.realtimeSinceStartup + 0.5f;
        int totalCores = Palette.ColorNames?.Length ?? 0;
        if (totalCores <= 0)
            return;

        IndiceCorRainbow = (IndiceCorRainbow + 1) % totalCores;
        foreach (byte playerId in JogadoresRainbowAtivos.ToArray())
        {
            PlayerControl jogador = null;
            foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
            {
                if (candidato != null && candidato.PlayerId == playerId)
                {
                    jogador = candidato;
                    break;
                }
            }

            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            try
            {
                // A pausa é por jogador: enquanto transformado, NÃO enviar
                // RpcSetColor para ele. Ao destransformar, o patch remove o ID
                // da lista e este ciclo volta a aplicar as cores automaticamente.
                bool rainbowPausado = ShapeshiftersRainbowTransformados.Contains(playerId);
                if (!IndicesCorRainbowPorJogador.TryGetValue(playerId, out int indiceJogador))
                {
                    indiceJogador = UnityEngine.Random.Range(0, totalCores);
                    IndicesCorRainbowPorJogador[playerId] = indiceJogador;
                }

                if (!rainbowPausado)
                {
                    // Cada jogador tem fase e passo próprios; os ciclos não ficam sincronizados.
                    int passo = UnityEngine.Random.Range(1, Math.Max(2, totalCores));
                    indiceJogador = (indiceJogador + passo) % totalCores;
                    IndicesCorRainbowPorJogador[playerId] = indiceJogador;
                    jogador.RpcSetColor((byte)indiceJogador);
                }

                string nomeOriginal = NomesRainbowTemporariosPorShapeshift.TryGetValue(playerId, out string nomeTemporario)
                    ? nomeTemporario
                    : (NomesOriginaisRainbow.TryGetValue(playerId, out string salvo)
                        ? salvo
                        : (jogador.Data.PlayerName ?? string.Empty));

                // O Rainbow não pode sobrescrever o nome temporário da ejeção.
                if (PlayerIdNaEjecao.HasValue && PlayerIdNaEjecao.Value == playerId)
                    continue;

                if (LobbyTagManager.PartidaIniciada() && ShipStatus.Instance != null)
                    DefinirNomeRainbowNosClientesComMod(jogador, CriarNomeRainbow(nomeOriginal, indiceJogador));
                else
                    DefinirNomeRainbowNosClientesComMod(jogador, nomeOriginal);
            }
            catch (Exception ex)
            {
                Instance?.Log.LogWarning("Rainbow: falha ao animar jogador " +
                    playerId + ": " + ex.Message);
            }
        }
    }

    // PlayerVoteArea mudou os nomes dos membros entre versões do jogo.
    // Resolver o ID por reflexão evita depender de um membro que não existe
    // em algumas versões das GameLibs.
    private static byte? ObterPlayerIdDaArea(PlayerVoteArea area)
    {
        if (area == null)
            return null;

        Type tipo = area.GetType();
        string[] nomes = { "TargetPlayerId", "targetPlayerId", "PlayerId", "playerId", "TargetId" };
        foreach (string nome in nomes)
        {
            FieldInfo campo = tipo.GetField(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (campo != null)
            {
                try { return Convert.ToByte(campo.GetValue(area)); }
                catch { }
            }

            PropertyInfo propriedade = tipo.GetProperty(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
            {
                try { return Convert.ToByte(propriedade.GetValue(area)); }
                catch { }
            }
        }

        // Fallback para versões em que o membro tem outro nome, mas continua
        // sendo um byte relacionado ao jogador-alvo.
        foreach (FieldInfo campo in tipo.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (campo.FieldType != typeof(byte) ||
                (!campo.Name.Contains("player", StringComparison.OrdinalIgnoreCase) &&
                 !campo.Name.Contains("target", StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                byte id = (byte)campo.GetValue(area);
                if (JogadoresRainbowAtivos.Contains(id))
                    return id;
            }
            catch { }
        }

        return null;
    }

    // A tela de reunião usa PlayerVoteArea.NameText, separado do nome acima do personagem.
    // O cliente host não recebe de volta o próprio SetName pela rede, então reaplicamos
    // a tag diretamente na interface da reunião em cada atualização local.
    public static void AtualizarTagRainbowNaReuniao()
    {
        try
        {
            MeetingHud reuniao = MeetingHud.Instance;
            if (reuniao == null || reuniao.playerStates == null ||
                !LobbyTagManager.PartidaIniciada() || ShipStatus.Instance == null)
                return;

            foreach (PlayerVoteArea area in reuniao.playerStates)
            {
                byte? playerId = ObterPlayerIdDaArea(area);
                if (!playerId.HasValue || !JogadoresRainbowAtivos.Contains(playerId.Value))
                    continue;

                if (!NomesOriginaisRainbow.TryGetValue(
                    playerId.Value, out string nomeOriginal))
                    continue;

                if (area.NameText != null)
                    area.NameText.text = CriarNomeRainbow(nomeOriginal, IndicesCorRainbowPorJogador.TryGetValue(playerId.Value, out int indiceTag) ? indiceTag : 0);
            }
        }
        catch (Exception ex)
        {
            Instance?.Log.LogDebug("Rainbow: falha ao atualizar tag na reunião: " + ex.Message);
        }
    }

    public static bool JogadorTemRainbowParaDestransformacao(PlayerControl jogador)
    {
        return RainbowGlobalAtivo &&
            jogador != null &&
            JogadoresRainbowAtivos.Contains(jogador.PlayerId);
    }

    public static void PausarRainbowDuranteShapeshift(PlayerControl shapeshifter)
    {
        if (shapeshifter == null ||
            !RainbowGlobalAtivo ||
            !JogadoresRainbowAtivos.Contains(shapeshifter.PlayerId))
            return;

        if (ShapeshiftersRainbowTransformados.Add(shapeshifter.PlayerId))
        {
            Instance?.Log.LogInfo(
                $"Rainbow: pausa de troca de cor ativada para Shapeshifter PlayerId={shapeshifter.PlayerId}.");
        }
    }

    // Intercepta a tentativa normal de voltar à forma original enquanto o
    // rainbow está ativo. Cancela a chamada inicial, restaura a cor do
    // Shapeshifter e emite um novo RPC de destransformação que será replicado.
    public static bool InterceptarDestransformacaoRainbow(
        PlayerControl shapeshifter,
        PlayerControl alvo,
        bool shouldAnimate)
    {
        if (EnviandoRpcDestransformacaoRainbow ||
            shapeshifter == null ||
            alvo == null ||
            shapeshifter.PlayerId != alvo.PlayerId)
            return true;

        byte id = shapeshifter.PlayerId;
        bool tinhaEstado = EstadosShapeshiftRainbow.TryGetValue(id, out EstadoShapeshiftRainbow estado);
        bool rainbowAtivo = JogadoresRainbowAtivos.Contains(id);
        if (!tinhaEstado && !rainbowAtivo)
            return true;

        try
        {
            // Primeiro restaura a identidade que existia antes da transformação.
            ShapeshiftersRainbowTransformados.Remove(id);
            NomesRainbowTemporariosPorShapeshift.Remove(id);
            if (tinhaEstado && !estado.EraRainbow)
            {
                JogadoresRainbowAtivos.Remove(id);
                NotificarEstadoRainbowVisual(id, false);
                CoresOriginaisRainbow.Remove(id);
                NomesOriginaisRainbow.Remove(id);
                IndicesCorRainbowPorJogador.Remove(id);
            }

            int corOriginal = tinhaEstado
                ? estado.CorOriginal
                : (CoresOriginaisRainbow.TryGetValue(id, out int salva)
                    ? salva
                    : (int)(shapeshifter.Data?.DefaultOutfit.ColorId ?? 0));
            string nomeOriginal = tinhaEstado
                ? estado.NomeOriginal
                : (NomesOriginaisRainbow.TryGetValue(id, out string salvoNome)
                    ? salvoNome
                    : (shapeshifter.Data?.PlayerName ?? string.Empty));

            // O nome/cor são preparados antes do RPC de reversão.
            if (tinhaEstado && estado.EraRainbow)
            {
                JogadoresRainbowAtivos.Add(id);
                CoresOriginaisRainbow[id] = corOriginal;
                NomesOriginaisRainbow[id] = nomeOriginal;
                NotificarEstadoRainbowVisual(id, true);
            }
            shapeshifter.RpcSetColor((byte)Math.Clamp(corOriginal, 0, 255));
            DefinirNomeRainbowNosClientesComMod(shapeshifter, nomeOriginal);

            EnviandoRpcDestransformacaoRainbow = true;
            ReenviandoRpcShapeshiftPreparado = true;
            shapeshifter.RpcShapeshift(shapeshifter, shouldAnimate);
            Instance?.Log.LogInfo($"Shapeshift: destransformação interceptada e reenviada; PlayerId={id}, estadoRainbowAnterior={(tinhaEstado ? estado.EraRainbow : rainbowAtivo)}.");
            return false;
        }
        catch (Exception ex)
        {
            Instance?.Log.LogError("Shapeshift: falha ao preparar/retransmitir destransformação: " + ex);
            return true;
        }
        finally
        {
            ReenviandoRpcShapeshiftPreparado = false;
            EnviandoRpcDestransformacaoRainbow = false;
            EstadosShapeshiftRainbow.Remove(id);
        }
    }

    private static string ObterNomeBaseRainbow(PlayerControl jogador)
    {
        if (jogador == null) return string.Empty;
        if (NomesOriginaisRainbow.TryGetValue(jogador.PlayerId, out string salvo))
            return salvo;
        return jogador.Data?.PlayerName ?? string.Empty;
    }

    private static bool PrepararEReenviarShapeshift(PlayerControl shapeshifter, PlayerControl alvo, bool shouldAnimate)
    {
        if (shapeshifter == null || alvo == null || shapeshifter.PlayerId == alvo.PlayerId)
            return true;
        if (!EhHost())
            return true;

        byte id = shapeshifter.PlayerId;
        bool eraRainbow = JogadoresRainbowAtivos.Contains(id);
        bool alvoRainbow = JogadoresRainbowAtivos.Contains(alvo.PlayerId);
        if (!eraRainbow && !alvoRainbow)
            return true;

        if (!EstadosShapeshiftRainbow.ContainsKey(id))
        {
            string nomeBase = eraRainbow ? ObterNomeBaseRainbow(shapeshifter) : (shapeshifter.Data?.PlayerName ?? string.Empty);
            int corBase = eraRainbow && CoresOriginaisRainbow.TryGetValue(id, out int salva)
                ? salva : (int)(shapeshifter.Data?.DefaultOutfit.ColorId ?? 0);
            EstadosShapeshiftRainbow[id] = new EstadoShapeshiftRainbow {
                CorOriginal = corBase, NomeOriginal = nomeBase, EraRainbow = eraRainbow, AlvoEraRainbow = alvoRainbow
            };
        }

        // Se o próprio Shapeshifter é rainbow, pause o ciclo antes do RPC que copia a aparência.
        if (eraRainbow)
            PausarRainbowDuranteShapeshift(shapeshifter);

        if (alvoRainbow)
        {
            string nomeAlvo = ObterNomeBaseRainbow(alvo);
            if (!eraRainbow)
            {
                CoresOriginaisRainbow[id] = shapeshifter.Data?.DefaultOutfit.ColorId ?? 0;
                NomesOriginaisRainbow[id] = shapeshifter.Data?.PlayerName ?? string.Empty;
                JogadoresRainbowAtivos.Add(id);
                if (!IndicesCorRainbowPorJogador.ContainsKey(id))
                    IndicesCorRainbowPorJogador[id] = UnityEngine.Random.Range(0, Math.Max(1, Palette.ColorNames?.Length ?? 1));
                NotificarEstadoRainbowVisual(id, true);
            }
            NomesRainbowTemporariosPorShapeshift[id] = nomeAlvo;
            ShapeshiftersRainbowTransformados.Remove(id);
        }

        try
        {
            ReenviandoRpcShapeshiftPreparado = true;
            shapeshifter.RpcShapeshift(alvo, shouldAnimate);
        }
        finally
        {
            ReenviandoRpcShapeshiftPreparado = false;
        }

        if (alvoRainbow)
        {
            // Aplica o nome com o prefixo Arco-íris + o nome/tag original do alvo.
            int indice = IndicesCorRainbowPorJogador.TryGetValue(id, out int idx) ? idx : 0;
            DefinirNomeRainbowNosClientesComMod(shapeshifter, CriarNomeRainbow(ObterNomeBaseRainbow(alvo), indice));
            int corAtual = indice % Math.Max(1, Palette.ColorNames?.Length ?? 1);
            try { shapeshifter.RpcSetColor((byte)corAtual); } catch { }
            ShapeshiftersRainbowTransformados.Remove(id);
        }

        Instance?.Log.LogInfo($"Shapeshift: RPC preparado e reenviado; jogador={id}, alvo={alvo.PlayerId}, shapeshifterRainbow={eraRainbow}, alvoRainbow={alvoRainbow}.");
        return false;
    }

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
                "Use: /color (id/nome) ou /color rainbow. Exemplo: /color 0, /color Red ou /color rainbow.");
            return;
        }

        bool pedidoRainbow = string.Equals(argumento, "rainbow", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argumento, "arco-íris", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argumento, "arcoiris", StringComparison.OrdinalIgnoreCase);

        int idCor = -1;
        if (!pedidoRainbow && int.TryParse(argumento, out int id))
        {
            if (id >= 0 && id < Palette.ColorNames.Length)
                idCor = id;
        }
        else if (!pedidoRainbow)
        {
            for (int i = 0; i < Palette.ColorNames.Length; i++)
            {
                string nomeCor = Palette.ColorNames[i].ToString();
                if (nomeCor.StartsWith("Color", StringComparison.OrdinalIgnoreCase))
                    nomeCor = nomeCor.Substring("Color".Length);

                if (string.Equals(nomeCor, argumento, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Palette.ColorNames[i].ToString(), argumento, StringComparison.OrdinalIgnoreCase))
                {
                    idCor = i;
                    break;
                }
            }
        }

        if (idCor < 0 && !pedidoRainbow)
        {
            string lista = string.Join(", ",
                Enumerable.Range(0, Palette.ColorNames.Length)
                    .Select(i => i + "=" + Palette.ColorNames[i].ToString()));
            ResponderComando(chat, jogador,
                "Cor inválida. Use um ID de 0 a " + (Palette.ColorNames.Length - 1) +
                " ou um nome exato. Cores: " + lista);
            return;
        }

        try
        {
            if (pedidoRainbow)
            {
                if (LobbyTagManager.PartidaIniciada())
                {
                    ResponderComando(chat, jogador,
                        "O comando /color rainbow só pode ser usado no lobby, antes de a partida começar.");
                    Instance?.Log.LogInfo(
                        $"/color rainbow recusado durante a partida para PlayerId={jogador.PlayerId}.");
                    return;
                }

                IniciarRainbowGlobal(jogador);
                ResponderComando(chat, jogador,
                    "Modo Arco-íris ativado para você. O efeito continuará durante a partida e terminará quando ela acabar.");
                return;
            }

            EncerrarRainbowJogador(jogador);
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
                $"Olá {nomeJogador}, esta é uma sala modificada pelo YouTubeChatBridge. " +
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
            // Cores fixas só podem ser escolhidas no lobby. /color rainbow
            // pode ser ativado antes ou durante a partida e dura até ela terminar.
            bool rainbowPedido = Regex.IsMatch(
                mensagem,
                @"^/color\s+(rainbow|arco-íris|arcoiris)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (LobbyTagManager.PartidaIniciada() && !rainbowPedido)
            {
                const string aviso = "<color=red>Durante a partida não é permitido o uso desse comando.";
                if (ehLocal)
                    YouTubeVoteBridgePlugin.Responder(__instance, aviso);
                else if (host)
                    YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, aviso);
                return false;
            }

            if (host)
            {
                YouTubeVoteBridgePlugin.ProcessarComandoCor(__instance, sourcePlayer, mensagem);
                return false;
            }

            // Clientes com o mod ocultam comandos recebidos de outros jogadores,
            // mas deixam o próprio comando seguir para o host.
            return ehLocal;
        }

        // /ytconfig é somente consulta e pode ser usado por qualquer jogador,
        // inclusive clientes vanilla. Os subcomandos /youtube ficam reservados ao host.
        if (mensagem.Equals("/ytconfig", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/ytconfiguracoes", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/ytconfigurações", StringComparison.OrdinalIgnoreCase))
        {
            if (host && ehLocal)
            {
                YouTubeVoteBridgePlugin.Responder(
                    __instance, PainelVotacaoRpc.ObterTextoConfiguracoes());
            }
            else if (host)
            {
                // O host responde ao jogador que enviou o comando, com os
                // valores reais do host, inclusive quando o cliente é vanilla.
                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(
                    sourcePlayer, PainelVotacaoRpc.ObterTextoConfiguracoes());
            }
            // Em clientes que não são host, não exibir a configuração local:
            // o host também recebe a mensagem e envia a resposta correta.
            return false;
        }

        if (mensagem.Equals("/help", StringComparison.OrdinalIgnoreCase) ||
            mensagem.Equals("/ajuda", StringComparison.OrdinalIgnoreCase))
        {
            const string ajuda = "Comandos: /color (id/nome/rainbow) | /help | /ytconfig (ver configurações) | /youtube (...) e /tag update (somente host).";
            if (ehLocal)
                YouTubeVoteBridgePlugin.Responder(__instance, ajuda);
            else if (host)
                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, ajuda);
            return false;
        }

        if (mensagem.Equals("/tag update", StringComparison.OrdinalIgnoreCase))
        {
            if (!host || !ehLocal)
            {
                if (ehLocal)
                    YouTubeVoteBridgePlugin.Responder(__instance, "<color=red>Somente o host pode usar /tag update.");
                else if (host)
                    YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, "<color=red>Somente o host pode usar /tag update.");
                return false;
            }

            LobbyTagManager.AtualizarTagsRemotas();
            YouTubeVoteBridgePlugin.Responder(__instance, "Atualização das tags solicitada. Aguarde o download do GitHub.");
            return false;
        }

        if (!mensagem.StartsWith("/youtube", StringComparison.OrdinalIgnoreCase))
            return true;

        // Todo /youtube (...) é exclusivo do host. Não encaminhar comandos de
        // clientes ao host e nunca executar subcomandos recebidos de terceiros.
        if (!host || !ehLocal)
        {
            const string avisoPermissao = "Somente o host pode usar comandos /youtube. Use /ytconfig para consultar as configurações.";
            if (ehLocal)
                YouTubeVoteBridgePlugin.Responder(__instance, avisoPermissao);
            else if (host)
                YouTubeVoteBridgePlugin.EnviarMensagemIndividual(sourcePlayer, avisoPermissao);
            return false;
        }

        YouTubeVoteBridgePlugin.ProcessarComando(__instance, mensagem);
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
        EventoAdicionarTarefas.AtualizarRestauracoesAgendadas();

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
        RainbowColorTransition.Atualizar();
        YouTubeVoteBridgePlugin.AtualizarRainbow();
        EventoEscudoTemporario.Atualizar();
        EventoCooldownIndividual.Atualizar();
        EventoCongelarJogador.Atualizar();
        EventoVelocidadeJogadores.Atualizar();
        EventoTsunami.Atualizar();
        EventoVisaoReduzida.Atualizar();
        EventoTrocarIdentidades.Atualizar();
    }
}


[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.Start))]
public static class YouTubeVoteRestoreLobbyTagsBeforeMatchPatch
{
    public static void Postfix()
    {
        // O jogo pode atualizar os nomes durante ShipStatus.Start. Restaurar no
        // Postfix faz a restauração depois dessa inicialização, evitando que os
        // nomes voltem a aparecer como "Jogador" quando a partida começa.
        if (AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
            LobbyTagManager.RestaurarTodos();
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


// O EHR limpa o estado dos modos/eventos no ciclo de vida da partida,
// tanto ao começar uma nova partida (CoStartGame) quanto ao terminar (OnGameEnd).
// Estes eventos duram até a reunião, mas não podem depender dela para serem limpos.
[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
public static class YouTubeVoteResetTaskEventsOnStartPatch
{
    public static void Postfix()
    {
        EventoTesteMenu.ResetarEstadoPartida("início de partida");
        YouTubeVoteBridgePlugin.LimparRegistroTarefasShapeshift();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Shapeshift/tarefas: histórico de jogador + tarefa concluída limpo no início da partida.");
        EventoTarefaMataJogador.Desativar();
        EventoTarefaSuicidio.Desativar();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Eventos de tarefa resetados no início da partida.");
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
public static class YouTubeVoteResetTaskEventsOnEndPatch
{
    public static void Postfix()
    {
        EventoTesteMenu.ResetarEstadoPartida("fim de partida");
        YouTubeVoteBridgePlugin.LimparRegistroTarefasShapeshift();
        YouTubeVoteBridgePlugin.ResetarRainbowFimDePartida();
        EventoTarefaMataJogador.Desativar();
        EventoTarefaSuicidio.Desativar();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Eventos de tarefa desativados ao terminar a partida, mesmo sem reunião.");
    }
}

// Captura a conclusão no mesmo ponto usado pelo EHR, com o taskId real do jogo.
[HarmonyPatch(typeof(GameData), nameof(GameData.CompleteTask))]
public static class YouTubeVoteShapeshiftTaskCompletionPatch
{
    public static void Postfix(PlayerControl pc, uint taskId)
    {
        YouTubeVoteBridgePlugin.RegistrarTarefaConcluida(pc, taskId);
    }
}

[HarmonyPatch(typeof(PlayerControl), "CompleteTask")]
public static class YouTubeVoteTaskKillPatch
{
    public static void Postfix(PlayerControl __instance)
    {
        if (__instance != null && YouTubeVoteBridgePlugin.RestaurandoTarefasDoShapeshift(__instance.PlayerId))
            return;
        EventoTarefaMataJogador.AoCompletarTarefa(__instance);
        EventoTarefaSuicidio.AoCompletarTarefa(__instance);
    }
}

// Usa TargetMethods (plural): se esta versão do Among Us não expuser nenhum
// método conhecido, a lista fica vazia sem provocar HarmonyException no carregamento.
[HarmonyPatch]
public static class YouTubeVoteShapeshiftTaskListPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] nomes = { "SetTasks", "RpcSetTasks", "AddTasks", "SetTaskList", "RpcAddTask" };
        foreach (string nome in nomes)
        {
            MethodBase metodo = AccessTools.Method(typeof(PlayerControl), nome);
            if (metodo == null)
                continue;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Shapeshift/tarefas: patch conectado a PlayerControl.{nome}.");
            yield return metodo;
        }

        // Também pode acontecer de a lista ser montada internamente por outra rotina.
        // Nesse caso, o log deixa claro que a restauração ainda não tem um gancho disponível.
    }

    public static void Postfix(PlayerControl __instance)
    {
        YouTubeVoteBridgePlugin.RestaurarTarefasDepoisDeAtualizar(__instance);
        EventoAdicionarTarefas.RestaurarTarefasConcluidas(__instance);
    }
}

// Segue o padrão do EHR: adiciona os tipos extras no Prefix de RpcSetTasks,
// antes que a lista seja enviada aos clientes vanilla.
[HarmonyPatch(typeof(NetworkedPlayerInfo), nameof(NetworkedPlayerInfo.RpcSetTasks))]
public static class YouTubeVoteAdicionarTarefasPrefixPatch
{
    public static void Prefix(NetworkedPlayerInfo __instance, ref Il2CppStructArray<byte> __0)
    {
        EventoAdicionarTarefas.AplicarAdicoesAntesDeEnviar(__instance, ref __0);
    }
}

// O evento de adicionar tarefas chama NetworkedPlayerInfo.RpcSetTasks.
 // Quando essa lista é aplicada, tentamos restaurar as tarefas que já estavam concluídas.
[HarmonyPatch]
public static class YouTubeVoteAdicionarTarefasRestauracaoPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        string[] nomes = { "RpcSetTasks", "SetTasks" };
        foreach (string nome in nomes)
        {
            MethodBase metodo = AccessTools.Method(typeof(NetworkedPlayerInfo), nome);
            if (metodo != null)
                yield return metodo;
        }
    }

    public static void Prefix(NetworkedPlayerInfo __instance)
    {
        // Em clientes com mod, captura as tarefas concluídas antes de a lista antiga
        // ser substituída. Os RPCs do host continuam sendo usados para os clientes vanilla.
        EventoAdicionarTarefas.CapturarConclusoesLocaisAntesDaRedistribuicao(__instance);
    }

    public static void Postfix(NetworkedPlayerInfo __instance)
    {
        if (__instance == null || PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null)
                continue;

            if (jogador.Data == __instance)
            {
                EventoAdicionarTarefas.RestaurarConclusoesLocaisDepoisDaRedistribuicao(__instance);
                EventoAdicionarTarefas.AgendarRestauracaoTarefasConcluidas(jogador);
                return;
            }
        }
    }
}

// EHR intercepta CheckShapeshift para identificar a habilidade real, antes do RPC
// visual. Capturamos as tarefas nesse ponto para não depender somente de RpcShapeshift.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckShapeshift))]
public static class YouTubeVoteShapeshiftCheckPatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl target)
    {
        if (__instance == null || target == null)
            return;

        // O log do próprio jogo confirma que CheckShapeshift é chamado com
        // target == jogador quando a habilidade está revertendo.
        if (__instance.PlayerId == target.PlayerId)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Shapeshift: CheckShapeshift detectou DEStransformação para PlayerId={__instance.PlayerId}; aguardando RpcShapeshift/SetTasks para restaurar tarefas.");
            YouTubeVoteBridgePlugin.RegistrarDestransformacaoShapeshifter(__instance);
            return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Shapeshift detectado via CheckShapeshift (padrão EHR): PlayerId={__instance.PlayerId}, alvo={target.PlayerId}.");
        YouTubeVoteBridgePlugin.RegistrarTarefasAntesDoShapeshift(__instance, target);
    }
}


// ============================================================
// MENSAGENS DE JOGADORES COM TAG
// ============================================================
// Não adicionamos quebra de linha às mensagens do chat dos jogadores tagueados.
// A tag continua apenas no nome do jogador; o texto enviado fica intacto.


// ============================================================
// CONTROLE DO FIM DE JOGO
// ============================================================
// Quando a opção está ativa, bloqueia chamadas normais do jogo para
// GameManager.RpcEndGame. O mod pode autorizar explicitamente um fim
// de jogo próprio usando ControleFimDeJogo.PermitirFimDoMod.
internal static class ControleFimDeJogo
{
    internal static bool PermitirFimDoMod;
}

[HarmonyPatch]
internal static class YouTubeVoteBridgeBloquearFimDeJogoOriginalPatch
{
    private static IEnumerable<MethodBase> TargetMethods()
    {
        return typeof(GameManager).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == "RpcEndGame")
            .Where(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return p.Length == 2 &&
                    p[0].ParameterType.IsEnum &&
                    p[1].ParameterType == typeof(bool);
            });
    }

    private static bool Prefix(MethodBase __originalMethod)
    {
        if (YouTubeVoteBridgePlugin.SemFimDeJogo?.Value != true ||
            ControleFimDeJogo.PermitirFimDoMod)
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Fim de jogo] Encerramento original bloqueado pela configuração 'Sem fim de jogo' (" +
            __originalMethod.Name + ").");
        return false;
    }
}


// EHR detecta o estado no Postfix de RpcShapeshift: alvo != jogador = shift;
// alvo == jogador = revert. Não reinterceptamos HandleRpc, pois isso pode impedir
// o jogo de processar o evento original.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcShapeshift))]
public static class YouTubeVoteRainbowShapeshiftPatch
{
    public static bool Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl target,
        [HarmonyArgument(1)] bool shouldAnimate)
    {
        if (__instance == null || target == null)
            return true;

        // Chamadas internas preparadas são liberadas uma única vez. Isso permite
        // cancelar o RPC inicial e reenviá-lo sem entrar em recursão.
        if (YouTubeVoteBridgePlugin.ReenviandoRpcShapeshiftPreparado)
            return true;

        if (YouTubeVoteBridgePlugin.BloqueioCompletoShapeshift)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Shapeshift: RPC bloqueado pelo bloqueio global; PlayerId={__instance.PlayerId}, alvo={target.PlayerId}.");
            return false;
        }

        if (__instance.PlayerId == target.PlayerId)
        {
            YouTubeVoteBridgePlugin.RegistrarDestransformacaoShapeshifter(__instance);
            return YouTubeVoteBridgePlugin.InterceptarDestransformacaoRainbow(
                __instance, target, shouldAnimate);
        }

        YouTubeVoteBridgePlugin.RegistrarTarefasAntesDoShapeshift(__instance, target);
        // Se o próprio Shapeshifter ou o alvo for rainbow, cancelar a chamada
        // original, preparar o estado e então emitir um novo RPC com a preparação pronta.
        return YouTubeVoteBridgePlugin.PrepararEReenviarShapeshift(
            __instance, target, shouldAnimate);
    }

    public static void Postfix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl target,
        [HarmonyArgument(1)] bool shouldAnimate)
    {
        if (__instance == null || target == null)
            return;

        if (__instance.PlayerId == target.PlayerId)
            YouTubeVoteBridgePlugin.RestaurarTarefasDepoisDeAtualizar(__instance);

        // O Prefix preparou o estado antes do RPC retransmitido. O ciclo Rainbow
        // cuida de manter a cor/nome do alvo se aplicável.
        if (YouTubeVoteBridgePlugin.JogadorTemRainbowParaDestransformacao(__instance))
            YouTubeVoteBridgePlugin.DefinirEstadoShapeshiftRainbow(
                __instance, __instance.PlayerId != target.PlayerId, target, shouldAnimate);
    }
}

// Protege o nome temporário da ejeção contra chamadas diretas de SetName
// feitas por outras rotinas do mod no host.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.SetName))]
public static class YouTubeVoteEjectionBlockOtherNamesPatch
{
    public static bool Prefix(PlayerControl __instance, string __0)
    {
        return YouTubeVoteBridgePlugin.PodeAlterarNomeDuranteEjecao(__instance, __0);
    }
}

// Begin recebe os dados de quem foi ejetado. O Prefix muda o nome global
// antes de a tela/animação de ejeção começar.
[HarmonyPatch]
public static class YouTubeVoteEjectionGlobalNamePatch
{
    private static MethodBase TargetMethod()
    {
        Type tipo = AccessTools.TypeByName("ExileController");
        return tipo == null ? null : AccessTools.Method(tipo, "Begin");
    }

    public static void Prefix(object[] __args)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Ejeção: ExileController.Begin chamado; argumentos={__args?.Length ?? 0}.");
        if (__args == null) return;
        foreach (object arg in __args)
        {
            if (arg == null) continue;
            object id = YouTubeVoteBridgePlugin.LerMembroPublico(arg, "PlayerId", "playerId", "PlayerID");
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Ejeção diagnóstico: argumento tipo={arg.GetType().FullName}, PlayerId={id ?? "não encontrado"}.");
            // Begin recebe InitProperties, que encapsula a informação do jogador.
            // Passamos o objeto mesmo sem PlayerId direto para a rotina procurar os membros internos.
            YouTubeVoteBridgePlugin.PrepararNomeGlobalDeEjecao(arg);
            if (id != null)
                return;
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "Ejeção: Begin foi chamado, mas nenhum argumento com PlayerId e Role foi encontrado.");
    }
}

[HarmonyPatch]
public static class YouTubeVoteEjectionRestoreNamePatch
{
    private static MethodBase TargetMethod()
    {
        Type tipo = AccessTools.TypeByName("ExileController");
        return tipo == null ? null : AccessTools.Method(tipo, "WrapUp");
    }

    public static void Postfix()
    {
        YouTubeVoteBridgePlugin.RestaurarNomeGlobalDeEjecao();
    }
}

// Corrige a tag na lista de votação da reunião. A interface do host é local e pode
// reconstruir os nomes sem receber o SetName que ele próprio enviou.
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Update))]
public static class YouTubeVoteRainbowMeetingNamePatch
{
    public static void Postfix()
    {
        YouTubeVoteBridgePlugin.AtualizarTagRainbowNaReuniao();
    }
}

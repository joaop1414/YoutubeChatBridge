using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Cada jogador vê somente o próprio nome personalizado.
/// O host envia SetName direcionado ao OwnerId de cada jogador remoto;
/// o nome de rede dos demais clientes não é alterado.
/// </summary>
internal static class SistemaNomesLocais
{
    private sealed class AvisoLocal
    {
        public string Texto;
        public string Cor;
        public float ExpiraEm;
    }

    private const string MARCADOR_FUNCAO_PREFIXO = "<size=0>YTVB_ROLE:";
    private const string MARCADOR_FUNCAO_FIM = "</size>";

    private static readonly Dictionary<byte, AvisoLocal> Avisos = new();
    private static readonly Dictionary<byte, string> UltimosNomesEnviados = new();
    private static readonly Dictionary<byte, string> NomesOriginaisRemotos = new();
    private static readonly Dictionary<(int ClientId, byte PlayerId), string> NomesOriginaisParaFantasmas = new();
    private static readonly Dictionary<(int ClientId, byte PlayerId), string> UltimosNomesParaFantasmas = new();
    private static float proximaAtualizacao;

    public static void DefinirAviso(byte playerId, string texto, string cor = "#FFD54A", float duracao = 0f)
    {
        if (string.IsNullOrWhiteSpace(texto))
        {
            LimparAviso(playerId);
            return;
        }

        Avisos[playerId] = new AvisoLocal
        {
            Texto = texto.Trim(),
            Cor = string.IsNullOrWhiteSpace(cor) ? "#FFD54A" : cor,
            ExpiraEm = duracao > 0f ? Time.realtimeSinceStartup + duracao : 0f
        };
        proximaAtualizacao = 0f;
    }

    public static void LimparAviso(byte playerId)
    {
        Avisos.Remove(playerId);
        proximaAtualizacao = 0f;
    }

    public static void LimparTodosAvisos()
    {
        Avisos.Clear();
        proximaAtualizacao = 0f;
    }

    public static void Atualizar()
    {
        // Ao voltar ao lobby, desfaz os SetName direcionados que ainda estiverem ativos.
        if (ShipStatus.Instance == null || !LobbyTagManager.PartidaIniciada())
        {
            RestaurarNomesDirecionados();
            return;
        }

        float agora = Time.realtimeSinceStartup;
        if (agora < proximaAtualizacao)
            return;

        proximaAtualizacao = agora + 0.08f;

        List<byte> expirados = new();
        foreach (KeyValuePair<byte, AvisoLocal> item in Avisos)
            if (item.Value == null || (item.Value.ExpiraEm > 0f && agora >= item.Value.ExpiraEm))
                expirados.Add(item.Key);
        foreach (byte id in expirados)
            Avisos.Remove(id);

        // Cada cliente com o mod altera somente o próprio HUD.
        AtualizarNomeDoProprioJogador();

        // Somente o host pode enviar SetName para os clientes vanilla.
        if (YouTubeVoteBridgePlugin.EhHost())
            EnviarNomesDirecionados();
    }

    private static void AtualizarNomeDoProprioJogador()
    {
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null || local.Data == null || local.Data.Disconnected)
            return;

        try
        {
            if (local.cosmetics?.nameText != null)
                local.cosmetics.nameText.text = CriarTexto(local);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[Nomes locais] Falha ao atualizar o próprio nome: " + ex.Message);
        }

        AtualizarNomeNaReuniao(local);

        // Um fantasma vê as funções de todos os jogadores vivos apenas no seu cliente.
        if (local.Data.IsDead && YouTubeVoteBridgePlugin.EhHost())
            AtualizarNomesVisiveisParaFantasma();
    }

    private static void AtualizarNomesVisiveisParaFantasma()
    {
        if (PlayerControl.LocalPlayer?.Data?.IsDead != true ||
            PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer)
                continue;

            try
            {
                if (jogador.cosmetics?.nameText != null)
                    jogador.cosmetics.nameText.text = CriarTexto(jogador);

                AtualizarNomeNaReuniao(jogador);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[Nomes locais] Falha ao revelar função para fantasma, PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }
    }

    private static void AtualizarNomeNaReuniao(PlayerControl jogador)
    {
        MeetingHud reuniao = MeetingHud.Instance;
        if (reuniao == null || reuniao.playerStates == null)
            return;

        foreach (PlayerVoteArea area in reuniao.playerStates)
        {
            if (area == null || area.NameText == null)
                continue;

            byte? playerId = ObterPlayerIdDaArea(area);
            if (!playerId.HasValue || playerId.Value != jogador.PlayerId)
                continue;

            try
            {
                area.NameText.text = CriarTexto(jogador);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[Nomes locais] Falha ao atualizar nome na reunião: " + ex.Message);
            }

            break;
        }
    }

    private static void EnviarNomesDirecionados()
    {
        if (AmongUsClient.Instance == null || PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.notRealPlayer ||
                jogador.Data.Disconnected || jogador.AmOwner)
                continue;

            try
            {
                string nomeOriginal = jogador.Data.PlayerName ?? string.Empty;
                string nomeFormatado = CriarTexto(jogador);

                // Guarda o texto original do lado do host. O SetName abaixo é
                // enviado somente ao dono deste PlayerControl.
                NomesOriginaisRemotos[jogador.PlayerId] = nomeOriginal;

                if (UltimosNomesEnviados.TryGetValue(jogador.PlayerId, out string anterior) &&
                    string.Equals(anterior, nomeFormatado, StringComparison.Ordinal))
                    continue;

                if (EnviarSetNameDirecionado(jogador, nomeFormatado))
                {
                    UltimosNomesEnviados[jogador.PlayerId] = nomeFormatado;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[Nomes locais] SetName direcionado enviado: PlayerId=" +
                        jogador.PlayerId + ", OwnerId=" + jogador.OwnerId + ".");
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Nomes locais] Falha ao enviar SetName direcionado para PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }

        // Clientes fantasmas recebem, somente no próprio cliente, os nomes de
        // todos os vivos com suas funções. O envio é agrupado por cliente.
        EnviarNomesVivosParaFantasmas();
    }

    private static void EnviarNomesVivosParaFantasmas()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.AllPlayerControls == null)
            return;

        HashSet<int> clientesFantasma = new();

        foreach (PlayerControl fantasma in PlayerControl.AllPlayerControls)
        {
            if (fantasma == null || fantasma.Data == null ||
                !fantasma.Data.IsDead || fantasma.Data.Disconnected ||
                fantasma.notRealPlayer || fantasma.AmOwner || fantasma.OwnerId < 0)
                continue;

            clientesFantasma.Add(fantasma.OwnerId);
            List<(PlayerControl Jogador, string Nome)> alteracoes = new();

            foreach (PlayerControl vivo in PlayerControl.AllPlayerControls)
            {
                if (vivo == null || vivo.Data == null ||
                    vivo.Data.IsDead || vivo.Data.Disconnected ||
                    vivo.notRealPlayer)
                    continue;

                var chave = (fantasma.OwnerId, vivo.PlayerId);
                if (!NomesOriginaisParaFantasmas.ContainsKey(chave))
                    NomesOriginaisParaFantasmas[chave] = vivo.Data.PlayerName ?? string.Empty;

                string nomeFormatado = CriarTexto(vivo);
                if (UltimosNomesParaFantasmas.TryGetValue(chave, out string anterior) &&
                    string.Equals(anterior, nomeFormatado, StringComparison.Ordinal))
                    continue;

                alteracoes.Add((vivo, nomeFormatado));
            }

            if (alteracoes.Count > 0 &&
                EnviarSetNamesParaCliente(fantasma.OwnerId, alteracoes))
            {
                foreach (var alteracao in alteracoes)
                    UltimosNomesParaFantasmas[(fantasma.OwnerId, alteracao.Jogador.PlayerId)] =
                        alteracao.Nome;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Nomes locais] Funções dos vivos enviadas ao fantasma OwnerId=" +
                    fantasma.OwnerId + "; jogadores=" + alteracoes.Count + ".");
            }
        }

        // Se um fantasma for revivido ou desconectar, restaura a visão normal dele.
        List<int> clientesParaRestaurar = NomesOriginaisParaFantasmas.Keys
            .Select(chave => chave.ClientId)
            .Distinct()
            .Where(clientId => !clientesFantasma.Contains(clientId))
            .ToList();

        foreach (int clientId in clientesParaRestaurar)
            RestaurarNomesDeUmFantasma(clientId);
    }

    private static bool EnviarSetNamesParaCliente(
        int clientId, List<(PlayerControl Jogador, string Nome)> nomes)
    {
        if (AmongUsClient.Instance == null || nomes == null || nomes.Count == 0)
            return false;

        const int tamanhoLote = 7;
        bool enviouAlgum = false;

        for (int inicio = 0; inicio < nomes.Count; inicio += tamanhoLote)
        {
            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(6); // GameDataTo
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(clientId);

                int fim = Math.Min(inicio + tamanhoLote, nomes.Count);
                for (int i = inicio; i < fim; i++)
                {
                    PlayerControl jogador = nomes[i].Jogador;
                    if (jogador == null || jogador.Data == null)
                        continue;

                    writer.StartMessage(2); // RPC
                    writer.WritePacked(jogador.NetId);
                    writer.Write((byte)RpcCalls.SetName);
                    writer.Write(jogador.Data.NetId);
                    writer.Write(nomes[i].Nome);
                    writer.EndMessage();
                }

                writer.EndMessage(); // GameDataTo
                AmongUsClient.Instance.SendOrDisconnect(writer);
                enviouAlgum = true;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Nomes locais] Falha ao enviar funções ao fantasma OwnerId=" +
                    clientId + ": " + ex.Message);
                return false;
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Recycle(); } catch { }
                }
            }
        }

        return enviouAlgum;
    }

    private static void RestaurarNomesDeUmFantasma(int clientId)
    {
        List<(PlayerControl Jogador, string Nome)> originais = new();
        foreach (KeyValuePair<(int ClientId, byte PlayerId), string> item in
            new Dictionary<(int ClientId, byte PlayerId), string>(NomesOriginaisParaFantasmas))
        {
            if (item.Key.ClientId != clientId)
                continue;

            PlayerControl jogador = ObterJogador(item.Key.PlayerId);
            if (jogador != null && jogador.Data != null &&
                !string.IsNullOrEmpty(item.Value))
                originais.Add((jogador, item.Value));

            NomesOriginaisParaFantasmas.Remove(item.Key);
            UltimosNomesParaFantasmas.Remove(item.Key);
        }

        if (originais.Count > 0)
            EnviarSetNamesParaCliente(clientId, originais);
    }

    private static bool EnviarSetNameDirecionado(PlayerControl jogador, string nome)
    {
        if (jogador == null || string.IsNullOrEmpty(nome) ||
            AmongUsClient.Instance == null || !YouTubeVoteBridgePlugin.EhHost())
            return false;

        try
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetName,
                SendOption.Reliable,
                jogador.OwnerId);

            // Formato idêntico ao EHR (CustomRpcSenderExtensions.RpcSetName):
            // Data.NetId do jogador, nome e flag adicional false.
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.Write(false);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Nomes locais] RPC SetName direcionado falhou: PlayerId=" +
                jogador.PlayerId + ", OwnerId=" + jogador.OwnerId + ", " + ex.Message);
            return false;
        }
    }

    private static void RestaurarNomesDirecionados()
    {
        if (AmongUsClient.Instance == null || !YouTubeVoteBridgePlugin.EhHost())
        {
            UltimosNomesEnviados.Clear();
            NomesOriginaisRemotos.Clear();
            NomesOriginaisParaFantasmas.Clear();
            UltimosNomesParaFantasmas.Clear();
            return;
        }

        foreach (int clientId in NomesOriginaisParaFantasmas.Keys
            .Select(chave => chave.ClientId).Distinct().ToArray())
            RestaurarNomesDeUmFantasma(clientId);

        if (UltimosNomesEnviados.Count == 0)
            return;

        foreach (KeyValuePair<byte, string> item in new Dictionary<byte, string>(UltimosNomesEnviados))
        {
            PlayerControl jogador = ObterJogador(item.Key);
            if (jogador == null || jogador.Data == null || jogador.AmOwner)
                continue;

            if (NomesOriginaisRemotos.TryGetValue(item.Key, out string original) &&
                !string.IsNullOrEmpty(original))
            {
                if (EnviarSetNameDirecionado(jogador, original))
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[Nomes locais] Nome original restaurado: PlayerId=" +
                        item.Key + ", OwnerId=" + jogador.OwnerId + ".");
                }
            }
        }

        UltimosNomesEnviados.Clear();
        NomesOriginaisRemotos.Clear();
    }

    private static string CriarTexto(PlayerControl jogador)
    {
        string funcao = ObterNomeFuncao(jogador);
        bool impostor = EhEquipeImpostora(jogador);
        string corFuncao = impostor ? "#FF4040" : "#00Eaff";
        string nome = ObterNomeBase(jogador);

        string resultado =
            "<color=" + corFuncao + "><size=50%>" + funcao +
            "\n</size>" + nome + "</color>";

        // Mantém os identificadores invisíveis no nome de rede. Isso permite
        // que todos os clientes reconheçam atributos e funções personalizadas.
        string marcadorFuncao = ObterMarcadorFuncao(jogador);
        if (!string.IsNullOrEmpty(marcadorFuncao))
            resultado += marcadorFuncao;

        if (EventoOportunista.EhOportunista(jogador))
        {
            resultado += " <color=#B56BFF><size=50%>[Oportunista]</size></color>";
            resultado += EventoOportunista.MARCADOR;
        }

        if (Avisos.TryGetValue(jogador.PlayerId, out AvisoLocal aviso) &&
            aviso != null && !string.IsNullOrWhiteSpace(aviso.Texto))
        {
            resultado += "\n<color=" + aviso.Cor + ">" + aviso.Texto + "</color>";
        }

        return resultado;
    }

    internal static string ObterNomeBase(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return string.Empty;

        string nome = EventoAnjoTraidor.ObterNomeOriginalParaExibicao(jogador);
        if (string.IsNullOrWhiteSpace(nome))
            nome = jogador.Data.PlayerName ?? string.Empty;

        nome = EventoOportunista.RemoverMarcador(nome);
        nome = RemoverMarcadoresFuncao(nome);

        // Se o SetName direcionado já aplicou o cabeçalho local da função,
        // remove somente esse invólucro para não empilhar cabeçalhos a cada Update.
        if (nome.StartsWith("<color=", StringComparison.OrdinalIgnoreCase))
        {
            int inicioTamanho = nome.IndexOf("<size=50%>", StringComparison.OrdinalIgnoreCase);
            int fimTamanho = inicioTamanho >= 0
                ? nome.IndexOf("</size>", inicioTamanho, StringComparison.OrdinalIgnoreCase)
                : -1;

            if (fimTamanho >= 0)
            {
                nome = nome.Substring(fimTamanho + "</size>".Length);
                if (nome.EndsWith("</color>", StringComparison.OrdinalIgnoreCase))
                    nome = nome.Substring(0, nome.Length - "</color>".Length);
            }
        }

        return nome;
    }

    internal static void SincronizarFuncaoPersonalizada(PlayerControl jogador, string funcao)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || jogador == null ||
            jogador.Data == null || string.IsNullOrWhiteSpace(funcao))
            return;

        string nomeBase = ObterNomeBase(jogador);
        bool oportunista = EventoOportunista.EhOportunista(jogador);
        string novoNome = nomeBase + CriarMarcadorFuncao(funcao) +
            (oportunista ? EventoOportunista.MARCADOR : string.Empty);

        EventoOportunista.EnviarNomeGlobal(jogador, novoNome);
    }

    internal static void LimparMarcadoresDeFuncao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                string.IsNullOrEmpty(jogador.Data.PlayerName) ||
                !jogador.Data.PlayerName.Contains(MARCADOR_FUNCAO_PREFIXO, StringComparison.Ordinal))
                continue;

            string nome = RemoverMarcadoresFuncao(jogador.Data.PlayerName);
            EventoOportunista.EnviarNomeGlobal(jogador, nome);
        }
    }

    private static string CriarMarcadorFuncao(string funcao)
    {
        string codificado = Convert.ToBase64String(Encoding.UTF8.GetBytes(funcao));
        return MARCADOR_FUNCAO_PREFIXO + codificado + MARCADOR_FUNCAO_FIM;
    }

    private static string ObterMarcadorFuncao(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return string.Empty;

        string nome = jogador.Data.PlayerName ?? string.Empty;
        int inicio = nome.IndexOf(MARCADOR_FUNCAO_PREFIXO, StringComparison.Ordinal);
        if (inicio >= 0)
        {
            int fim = nome.IndexOf(MARCADOR_FUNCAO_FIM, inicio, StringComparison.Ordinal);
            if (fim >= 0)
                return nome.Substring(inicio, fim + MARCADOR_FUNCAO_FIM.Length - inicio);
        }

        RegistroFuncoesJogadores.Atribuicao atribuicao =
            RegistroFuncoesJogadores.Obter(jogador.PlayerId);
        return atribuicao?.Personalizada == true
            ? CriarMarcadorFuncao(atribuicao.FuncaoPersonalizada)
            : string.Empty;
    }

    private static bool TentarObterFuncaoMarcada(PlayerControl jogador, out string funcao)
    {
        funcao = null;
        if (jogador == null || jogador.Data == null)
            return false;

        string nome = jogador.Data.PlayerName ?? string.Empty;
        int inicio = nome.IndexOf(MARCADOR_FUNCAO_PREFIXO, StringComparison.Ordinal);
        if (inicio < 0)
            return false;

        int conteudo = inicio + MARCADOR_FUNCAO_PREFIXO.Length;
        int fim = nome.IndexOf(MARCADOR_FUNCAO_FIM, conteudo, StringComparison.Ordinal);
        if (fim <= conteudo)
            return false;

        try
        {
            string codificado = nome.Substring(conteudo, fim - conteudo);
            funcao = Encoding.UTF8.GetString(Convert.FromBase64String(codificado));
            return !string.IsNullOrWhiteSpace(funcao);
        }
        catch
        {
            funcao = null;
            return false;
        }
    }

    private static string RemoverMarcadoresFuncao(string nome)
    {
        if (string.IsNullOrEmpty(nome))
            return nome;

        while (true)
        {
            int inicio = nome.IndexOf(MARCADOR_FUNCAO_PREFIXO, StringComparison.Ordinal);
            if (inicio < 0)
                return nome;

            int fim = nome.IndexOf(MARCADOR_FUNCAO_FIM, inicio, StringComparison.Ordinal);
            if (fim < 0)
                return nome;

            nome = nome.Remove(inicio, fim + MARCADOR_FUNCAO_FIM.Length - inicio);
        }
    }

    internal static string ObterNomeFuncao(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return "Tripulante";

        RegistroFuncoesJogadores.Atribuicao atribuicao =
            RegistroFuncoesJogadores.Obter(jogador.PlayerId);

        string nomeFuncao;
        if (atribuicao?.Personalizada == true)
            nomeFuncao = atribuicao.FuncaoPersonalizada;
        else if (TentarObterFuncaoMarcada(jogador, out string funcaoMarcada))
            nomeFuncao = funcaoMarcada;
        else if (EventoAnjoTraidor.EhAnjoTraidor(jogador))
            nomeFuncao = "Anjo Traidor";
        else
        {
            string nome = atribuicao?.FuncaoVanilla.ToString() ??
                jogador.Data.RoleType.ToString() ?? "Crewmate";
            nomeFuncao = nome.Trim().ToLowerInvariant() switch
            {
                "crewmate" => "Tripulante",
                "impostor" => "Impostor",
                "scientist" => "Cientista",
                "engineer" => "Engenheiro",
                "guardianangel" => "Anjo da Guarda",
                "shapeshifter" => "Metamorfo",
                "noisemaker" => "Sirene",
                "tracker" => "Rastreador",
                "phantom" => "Fantasma",
                "detective" => "Detetive",
                "viper" => "Víbora",
                "judge" => "Juiz",
                "impostorghost" => "Fantasma Impostor",
                "crewmateghost" => "Fantasma Tripulante",
                _ => nome
            };
        }

        if (EventoOportunista.EhOportunista(jogador))
            nomeFuncao += " + Oportunista";

        return nomeFuncao;
    }

    private static bool EhEquipeImpostora(PlayerControl jogador)
    {
        if (EventoAnjoTraidor.EhAnjoTraidor(jogador))
            return true;

        try
        {
            return jogador.Data?.Role != null && jogador.Data.Role.IsImpostor;
        }
        catch
        {
            string tipo = jogador.Data?.RoleType.ToString() ?? string.Empty;
            return tipo.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   tipo.Equals("Phantom", StringComparison.OrdinalIgnoreCase) ||
                   tipo.Equals("Viper", StringComparison.OrdinalIgnoreCase) ||
                   tipo.Equals("Shapeshifter", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static PlayerControl ObterJogador(byte playerId)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        return null;
    }

    private static byte? ObterPlayerIdDaArea(PlayerVoteArea area)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type tipo = area.GetType();
        string[] nomes = { "TargetPlayerId", "targetPlayerId", "PlayerId", "playerId", "TargetId" };

        foreach (string nome in nomes)
        {
            try
            {
                FieldInfo campo = tipo.GetField(nome, flags);
                if (campo != null)
                    return Convert.ToByte(campo.GetValue(area));

                PropertyInfo propriedade = tipo.GetProperty(nome, flags);
                if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
                    return Convert.ToByte(propriedade.GetValue(area));
            }
            catch { }
        }

        foreach (FieldInfo campo in tipo.GetFields(flags))
        {
            if (campo.FieldType != typeof(byte) ||
                (!campo.Name.Contains("player", StringComparison.OrdinalIgnoreCase) &&
                 !campo.Name.Contains("target", StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                byte id = (byte)campo.GetValue(area);
                if (ObterJogador(id) != null)
                    return id;
            }
            catch { }
        }

        return null;
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
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

    private static readonly Dictionary<byte, AvisoLocal> Avisos = new();
    private static readonly Dictionary<byte, string> UltimosNomesEnviados = new();
    private static readonly Dictionary<byte, string> NomesOriginaisRemotos = new();
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
        if (local == null || local.Data == null || local.Data.Disconnected ||
            local.cosmetics?.nameText == null)
            return;

        try
        {
            local.cosmetics.nameText.text = CriarTexto(local);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[Nomes locais] Falha ao atualizar o próprio nome: " + ex.Message);
        }

        AtualizarNomeNaReuniao(local);

        // Um fantasma vê as funções de todos os jogadores vivos apenas no seu cliente.
        if (local.Data.IsDead)
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
        if (UltimosNomesEnviados.Count == 0)
            return;

        if (AmongUsClient.Instance == null || !YouTubeVoteBridgePlugin.EhHost())
        {
            UltimosNomesEnviados.Clear();
            NomesOriginaisRemotos.Clear();
            return;
        }

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

        // Mantém o identificador invisível sincronizado no nome de rede,
        // inclusive quando o host envia SetName direcionado ao dono.
        if (EventoOportunista.EhOportunista(jogador))
            resultado += EventoOportunista.MARCADOR;

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

        return EventoOportunista.RemoverMarcador(nome);
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

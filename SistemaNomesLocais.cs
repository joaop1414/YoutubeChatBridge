using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Formata os nomes somente nos elementos visuais locais. Não altera PlayerName,
/// não envia SetName/RPC e reaplica a formatação depois das atualizações do jogo.
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
        if (ShipStatus.Instance == null || !LobbyTagManager.PartidaIniciada())
            return;

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

        AtualizarNomesNoMundo();
        AtualizarNomesNaReuniao();
    }

    private static void AtualizarNomesNoMundo()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.notRealPlayer ||
                jogador.Data.Disconnected || jogador.cosmetics?.nameText == null)
                continue;

            try
            {
                jogador.cosmetics.nameText.text = CriarTexto(jogador);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[Nomes locais] Falha ao atualizar PlayerId=" + jogador.PlayerId + ": " + ex.Message);
            }
        }
    }

    private static void AtualizarNomesNaReuniao()
    {
        MeetingHud reuniao = MeetingHud.Instance;
        if (reuniao == null || reuniao.playerStates == null)
            return;

        foreach (PlayerVoteArea area in reuniao.playerStates)
        {
            if (area == null || area.NameText == null)
                continue;

            byte? playerId = ObterPlayerIdDaArea(area);
            if (!playerId.HasValue)
                continue;

            PlayerControl jogador = ObterJogador(playerId.Value);
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            try
            {
                area.NameText.text = CriarTexto(jogador);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[Nomes locais] Falha ao atualizar reunião PlayerId=" + playerId.Value + ": " + ex.Message);
            }
        }
    }

    private static string CriarTexto(PlayerControl jogador)
    {
        string funcao = ObterNomeFuncao(jogador);
        bool impostor = EhEquipeImpostora(jogador);
        string corFuncao = impostor ? "#FF4040" : "#00Eaff";

        // O Rainbow já mantém o texto colorido em Data.PlayerName. Como a
        // formatação é apenas visual, usamos essa string sem reenviar SetName.
        string nome = EventoAnjoTraidor.ObterNomeOriginalParaExibicao(jogador);
        if (string.IsNullOrWhiteSpace(nome))
            nome = jogador.Data.PlayerName ?? string.Empty;

        string resultado =
            "<color=" + corFuncao + "><size=50%>" + funcao +
            "\n</size>" + nome + "</color>";

        if (Avisos.TryGetValue(jogador.PlayerId, out AvisoLocal aviso) &&
            aviso != null && !string.IsNullOrWhiteSpace(aviso.Texto))
        {
            resultado += "\n<color=" + aviso.Cor + ">" + aviso.Texto + "</color>";
        }

        return resultado;
    }

    private static string ObterNomeFuncao(PlayerControl jogador)
    {
        if (EventoAnjoTraidor.EhAnjoTraidor(jogador))
            return "Anjo Traidor";

        string nome = jogador.Data?.RoleType.ToString() ?? "Crewmate";
        switch (nome.Trim().ToLowerInvariant())
        {
            case "crewmate": return "Tripulante";
            case "impostor": return "Impostor";
            case "scientist": return "Cientista";
            case "engineer": return "Engenheiro";
            case "guardianangel": return "Anjo da Guarda";
            case "shapeshifter": return "Metamorfo";
            case "noisemaker": return "Sirene";
            case "tracker": return "Rastreador";
            case "phantom": return "Fantasma";
            case "detective": return "Detetive";
            case "viper": return "Víbora";
            case "judge": return "Juiz";
            case "impostorghost": return "Fantasma Impostor";
            case "crewmateghost": return "Fantasma Tripulante";
            default: return nome;
        }
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

        // Fallback para builds em que o campo do PlayerVoteArea mudou de nome.
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

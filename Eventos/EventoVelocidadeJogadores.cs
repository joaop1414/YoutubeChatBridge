using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

// Envia opções de velocidade diretamente ao cliente-alvo usando o pacote
// GameDataTo + UpdateGameData + LogicOptions, seguindo o formato do EHR.
internal static class EventoVelocidadeJogadores
{
    private sealed class Efeito
    {
        public float VelocidadeOriginal;
        public float Multiplicador;
        public float ExpiraEm;
    }

    private static readonly Dictionary<byte, Efeito> Efeitos = new();

    public static void AplicarMultiplicador(byte playerId, float multiplicador, float duracao)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        PlayerControl jogador = EncontrarJogador(playerId);
        if (jogador == null || jogador.Data == null)
            return;

        if (!Efeitos.TryGetValue(playerId, out Efeito efeito))
        {
            if (!ObterVelocidadeAtual(out float velocidadeOriginal))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível ler a velocidade atual das opções de jogo.");
                return;
            }

            efeito = new Efeito { VelocidadeOriginal = velocidadeOriginal };
            Efeitos[playerId] = efeito;
        }

        efeito.Multiplicador = multiplicador;
        efeito.ExpiraEm = Time.realtimeSinceStartup + duracao;

        float velocidade = Mathf.Clamp(efeito.VelocidadeOriginal * multiplicador, 0.0001f, 3f);
        if (EnviarOpcoesBrutas(jogador, velocidade))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Opções brutas enviadas: jogador={jogador.Data.PlayerName}, " +
                $"PlayerId={playerId}, OwnerId={jogador.OwnerId}, velocidade={velocidade:0.####}.");
        }
    }

    public static void Limpar(byte playerId)
    {
        if (Efeitos.TryGetValue(playerId, out Efeito efeito))
        {
            PlayerControl jogador = EncontrarJogador(playerId);
            if (jogador != null && jogador.Data != null && YouTubeVoteBridgePlugin.EhHost())
                EnviarOpcoesBrutas(jogador, efeito.VelocidadeOriginal);

            Efeitos.Remove(playerId);
        }
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || Efeitos.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> expirados = new();

        foreach (KeyValuePair<byte, Efeito> item in Efeitos)
            if (agora >= item.Value.ExpiraEm)
                expirados.Add(item.Key);

        foreach (byte id in expirados)
            Limpar(id);
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        return null;
    }

    private static bool ObterVelocidadeAtual(out float velocidade)
    {
        velocidade = 1f;
        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes == null)
                return false;

            velocidade = opcoes.GetFloat(FloatOptionNames.PlayerSpeedMod);
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao ler PlayerSpeedMod: " + ex.Message);
            return false;
        }
    }

    private static bool EnviarOpcoesBrutas(PlayerControl jogador, float velocidade)
    {
        if (jogador == null || AmongUsClient.Instance == null ||
            GameManager.Instance == null || !YouTubeVoteBridgePlugin.EhHost())
            return false;

        var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opcoes == null)
            return false;

        NormalGameOptionsV12 opcoesNormais = null;
        try
        {
            opcoesNormais = opcoes.TryCast<NormalGameOptionsV12>();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível converter as opções para NormalGameOptionsV12: " + ex.Message);
            return false;
        }

        if (opcoesNormais == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Envio bruto cancelado: as opções atuais não são NormalGameOptionsV12.");
            return false;
        }

        float velocidadeAnterior;
        byte[] dadosOpcoes;
        try
        {
            velocidadeAnterior = opcoesNormais.GetFloat(FloatOptionNames.PlayerSpeedMod);
            opcoesNormais.SetFloat(FloatOptionNames.PlayerSpeedMod, velocidade);

            MessageWriter serializador = MessageWriter.Get();
            try
            {
                serializador.Write(opcoesNormais.Version);
                serializador.StartMessage(0);
                serializador.Write((byte)opcoesNormais.GameMode);
                NormalGameOptionsV12.Serialize(serializador, opcoesNormais);
                serializador.EndMessage();
                dadosOpcoes = serializador.ToByteArray(false);
            }
            finally
            {
                serializador.Recycle();
                opcoesNormais.SetFloat(FloatOptionNames.PlayerSpeedMod, velocidadeAnterior);
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao serializar opções brutas: " + ex);
            return false;
        }

        bool enviou = false;
        try
        {
            int quantidadeLogica = GameManager.Instance.LogicComponents.Count;
            for (int i = 0; i < quantidadeLogica; i++)
            {
                if (GameManager.Instance.LogicComponents[i] is not LogicOptions)
                    continue;

                MessageWriter pacote = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    // Root message 0x06: GameDataTo (GameId + OwnerId).
                    pacote.StartMessage(6);
                    pacote.Write(AmongUsClient.Instance.GameId);
                    pacote.WritePacked(jogador.OwnerId);

                    // UpdateData do GameManager e payload da LogicOptions correspondente.
                    pacote.StartMessage(1);
                    pacote.WritePacked(GameManager.Instance.NetId);
                    pacote.StartMessage((byte)i);
                    pacote.WriteBytesAndSize(dadosOpcoes);
                    pacote.EndMessage();
                    pacote.EndMessage();
                    pacote.EndMessage();

                    AmongUsClient.Instance.SendOrDisconnect(pacote);
                    enviou = true;
                }
                finally
                {
                    pacote.Recycle();
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao enviar pacote bruto de opções: " + ex);
            return false;
        }

        if (!enviou)
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Nenhum componente LogicOptions encontrado para enviar as opções.");

        return enviou;
    }
}

internal static class EventoVelocidadeTemporaria
{
    private const float DuracaoSegundos = 15f;

    public static void Supervelocidade() => AplicarATodos(2.5f);
    public static void VelocidadeReduzida() => AplicarATodos(0.35f);

    private static void AplicarATodos(float multiplicador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        int afetados = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            EventoVelocidadeJogadores.AplicarMultiplicador(
                jogador.PlayerId, multiplicador, DuracaoSegundos);
            afetados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Evento de velocidade via opções brutas: multiplicador={multiplicador:0.####}x, " +
            $"jogadores={afetados}, duração={DuracaoSegundos:0}s.");
    }
}

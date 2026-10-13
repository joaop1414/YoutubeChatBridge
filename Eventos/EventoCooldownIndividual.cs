using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

// Altera temporariamente KillCooldown somente nas opções enviadas ao jogador-alvo.
// A opção original é restaurada após a reação FailedProtected.
internal static class EventoCooldownIndividual
{
    private sealed class Restauracao
    {
        public float ValorOriginal;
        public float RestaurarEm;
    }

    private static readonly Dictionary<byte, Restauracao> Restauracoes = new();
    private const float AtrasoRestauracao = 0.75f;

    public static bool PrepararParaEscudo(PlayerControl jogador, float cooldownDobrado, float cooldownOriginal)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || jogador == null ||
            AmongUsClient.Instance == null || GameManager.Instance == null)
            return false;

        bool aplicado;
        if (jogador.AmOwner)
            aplicado = DefinirLocal(cooldownDobrado);
        else
            aplicado = EnviarOpcoes(jogador, cooldownDobrado);

        if (!aplicado)
            return false;

        Restauracoes[jogador.PlayerId] = new Restauracao
        {
            ValorOriginal = cooldownOriginal,
            RestaurarEm = Time.realtimeSinceStartup + AtrasoRestauracao
        };

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Cooldown individual] Preparado para escudo: PlayerId={jogador.PlayerId}, " +
            $"OwnerId={jogador.OwnerId}, cooldown temporário={cooldownDobrado:0.##}s, " +
            $"restauração para {cooldownOriginal:0.##}s em {AtrasoRestauracao:0.##}s.");
        return true;
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || Restauracoes.Count == 0)
            return;

        float agora = Time.realtimeSinceStartup;
        List<byte> prontas = new();

        foreach (var item in Restauracoes)
            if (agora >= item.Value.RestaurarEm)
                prontas.Add(item.Key);

        foreach (byte id in prontas)
        {
            Restauracao restauracao = Restauracoes[id];
            PlayerControl jogador = EncontrarJogador(id);

            if (jogador != null && jogador.Data != null)
            {
                bool restaurado = jogador.AmOwner
                    ? DefinirLocal(restauracao.ValorOriginal)
                    : EnviarOpcoes(jogador, restauracao.ValorOriginal);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[Cooldown individual] Restauração {(restaurado ? "enviada" : "FALHOU")}: " +
                    $"PlayerId={id}, cooldown={restauracao.ValorOriginal:0.##}s.");
            }

            Restauracoes.Remove(id);
        }
    }

    private static bool DefinirLocal(float cooldown)
    {
        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes == null)
                return false;

            opcoes.SetFloat(FloatOptionNames.KillCooldown, cooldown);

            if (GameManager.Instance?.LogicComponents != null)
            {
                foreach (GameLogicComponent componente in GameManager.Instance.LogicComponents)
                    if (componente is LogicOptions logicOptions)
                        logicOptions.SetGameOptions(opcoes);
            }

            GameOptionsManager.Instance.CurrentGameOptions = opcoes;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Cooldown individual] Falha ao alterar opções locais: " + ex);
            return false;
        }
    }

    private static bool EnviarOpcoes(PlayerControl jogador, float cooldown)
    {
        var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
        if (jogador == null || opcoes == null || AmongUsClient.Instance == null ||
            GameManager.Instance == null)
            return false;

        NormalGameOptionsV12 normais;
        try
        {
            normais = opcoes.TryCast<NormalGameOptionsV12>();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Cooldown individual] Conversão de opções falhou: " + ex.Message);
            return false;
        }

        if (normais == null)
            return false;

        byte[] dados;
        float anterior = normais.GetFloat(FloatOptionNames.KillCooldown);
        try
        {
            normais.SetFloat(FloatOptionNames.KillCooldown, cooldown);
            MessageWriter serializador = MessageWriter.Get();
            try
            {
                serializador.Write(normais.Version);
                serializador.StartMessage(0);
                serializador.Write((byte)normais.GameMode);
                NormalGameOptionsV12.Serialize(serializador, normais);
                serializador.EndMessage();
                dados = serializador.ToByteArray(false);
            }
            finally
            {
                serializador.Recycle();
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Cooldown individual] Falha ao serializar opções: " + ex);
            return false;
        }
        finally
        {
            normais.SetFloat(FloatOptionNames.KillCooldown, anterior);
        }

        bool enviou = false;
        try
        {
            for (int i = 0; i < GameManager.Instance.LogicComponents.Count; i++)
            {
                if (GameManager.Instance.LogicComponents[i] is not LogicOptions)
                    continue;

                MessageWriter pacote = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    pacote.StartMessage(6);
                    pacote.Write(AmongUsClient.Instance.GameId);
                    pacote.WritePacked(jogador.OwnerId);
                    pacote.StartMessage(1);
                    pacote.WritePacked(GameManager.Instance.NetId);
                    pacote.StartMessage((byte)i);
                    pacote.WriteBytesAndSize(dados);
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
                "[Cooldown individual] Falha ao enviar opções individuais: " + ex);
            return false;
        }

        return enviou;
    }

    private static PlayerControl EncontrarJogador(byte playerId)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;
        return null;
    }
}

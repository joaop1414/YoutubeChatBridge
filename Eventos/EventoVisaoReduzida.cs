using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

// Reduz temporariamente a visão de tripulantes e impostores em conjunto.
// A opção é global, portanto a restauração sempre devolve os dois valores
// originais capturados antes da primeira ativação.
internal sealed class EventoVisaoReduzida : EventoVotacao
{
    private const float DuracaoSegundos = 20f;
    private const float MultiplicadorVisao = 0.15f;
    private static float? VisaoTripulanteOriginal;
    private static float? VisaoImpostorOriginal;
    private static float ExpiraEm;

    public override string Nome => "Visão reduzida para todos por 20 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Visão reduzida cancelada: opções da partida indisponíveis.");
                return;
            }

            if (!VisaoTripulanteOriginal.HasValue || !VisaoImpostorOriginal.HasValue)
            {
                VisaoTripulanteOriginal = opcoes.GetFloat(FloatOptionNames.CrewLightMod);
                VisaoImpostorOriginal = opcoes.GetFloat(FloatOptionNames.ImpostorLightMod);
            }

            opcoes.SetFloat(FloatOptionNames.CrewLightMod,
                Mathf.Clamp(VisaoTripulanteOriginal.Value * MultiplicadorVisao, 0.05f, 5f));
            opcoes.SetFloat(FloatOptionNames.ImpostorLightMod,
                Mathf.Clamp(VisaoImpostorOriginal.Value * MultiplicadorVisao, 0.05f, 5f));

            AplicarOpcoesLocais(opcoes);
            SincronizarConfiguracoes();

            ExpiraEm = Time.realtimeSinceStartup + DuracaoSegundos;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Visão reduzida ativada para tripulantes e impostores por " +
                DuracaoSegundos + " segundos.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao ativar visão reduzida: " + ex);
        }
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ExpiraEm <= 0f ||
            Time.realtimeSinceStartup < ExpiraEm)
            return;

        Restaurar();
    }

    private static void Restaurar()
    {
        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes != null && VisaoTripulanteOriginal.HasValue &&
                VisaoImpostorOriginal.HasValue)
            {
                opcoes.SetFloat(FloatOptionNames.CrewLightMod, VisaoTripulanteOriginal.Value);
                opcoes.SetFloat(FloatOptionNames.ImpostorLightMod, VisaoImpostorOriginal.Value);
                AplicarOpcoesLocais(opcoes);
                SincronizarConfiguracoes();
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao restaurar visão original: " + ex);
        }
        finally
        {
            ExpiraEm = 0f;
            VisaoTripulanteOriginal = null;
            VisaoImpostorOriginal = null;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Visão original da partida restaurada.");
        }
    }

    private static void AplicarOpcoesLocais(IGameOptions opcoes)
    {
        if (GameManager.Instance?.LogicComponents != null)
        {
            foreach (GameLogicComponent componente in GameManager.Instance.LogicComponents)
            {
                if (componente is LogicOptions logicOptions)
                    logicOptions.SetGameOptions(opcoes);
            }
        }

        GameOptionsManager.Instance.CurrentGameOptions = opcoes;
    }

    // Replica o envelope de LogicOptions usado pelo próprio jogo/EHR e
    // envia as opções modificadas individualmente a todos os donos dos jogadores.
    private static void SincronizarConfiguracoes()
    {
        if (AmongUsClient.Instance == null || GameManager.Instance == null ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;

        var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opcoes == null)
            return;

        NormalGameOptionsV12 normais;
        try
        {
            normais = opcoes.TryCast<NormalGameOptionsV12>();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível converter opções para sincronizar a visão: " + ex.Message);
            return;
        }

        if (normais == null)
            return;

        byte[] dados;
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

        bool enviou = false;
        for (int p = 0; p < PlayerControl.AllPlayerControls.Count; p++)
        {
            PlayerControl jogador = PlayerControl.AllPlayerControls[p];
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

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

        if (!enviou)
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Nenhum pacote de opções foi enviado para a visão reduzida.");
    }
}

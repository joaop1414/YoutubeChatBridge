using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
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

    // Usa o sincronizador do EHR se ele estiver carregado. Em vanilla, tenta
    // encontrar o mecanismo equivalente pelo nome para evitar dependência fixa.
    private static void SincronizarConfiguracoes()
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type tipoUtils = assembly.GetType("EHR.Utils", false);
            MethodInfo syncEhr = tipoUtils?.GetMethod(
                "SyncAllSettings",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

            if (syncEhr != null)
            {
                syncEhr.Invoke(null, null);
                return;
            }

            Type tipoSender = assembly.GetType("PlayerGameOptionsSender", false)
                ?? assembly.GetType("GameOptions.PlayerGameOptionsSender", false);
            if (tipoSender == null)
                continue;

            MethodInfo dirty = tipoSender.GetMethod("SetDirtyToAll",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo enviar = tipoSender.GetMethod("SendAllImmediately",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            dirty?.Invoke(null, null);
            enviar?.Invoke(null, null);
            if (dirty != null || enviar != null)
                return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "Visão alterada nas opções locais, mas não foi encontrado um sincronizador " +
            "global de opções. Verifique a visão nos clientes remotos.");
    }
}

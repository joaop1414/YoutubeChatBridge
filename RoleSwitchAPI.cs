using System;
using System.Reflection;
using AmongUs.GameOptions;

namespace YouTubeVoteBridge;

/// <summary>
/// API central para trocar a função de um jogador pelo RPC nativo.
/// Não aplica funções intermediárias: isso poderia colocar temporariamente
/// um jogador vivo no estado de fantasma e causar problemas na tela/intro.
/// </summary>
internal static class RoleSwitchAPI
{
    internal static bool TrocarFuncao(PlayerControl jogador, string nomeFuncao)
    {
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            LogAviso("Troca cancelada: jogador inválido ou desconectado.");
            return false;
        }

        if (!Enum.TryParse(typeof(RoleTypes), nomeFuncao, true, out object valor))
        {
            LogAviso($"RoleTypes não contém a função '{nomeFuncao}'.");
            return false;
        }

        return TrocarFuncao(jogador, (RoleTypes)valor);
    }

    internal static bool TrocarFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            LogAviso("Troca cancelada: jogador inválido ou desconectado.");
            return false;
        }

        try
        {
            LogInfo($"Iniciando troca direta: jogador={jogador.PlayerId}, atual={LerFuncao(jogador)}, destino={funcao}.");

            if (!Aplicar(jogador, funcao))
            {
                LogErro($"Não foi possível aplicar {funcao} ao jogador={jogador.PlayerId}.");
                return false;
            }

            LogInfo($"Troca solicitada: jogador={jogador.PlayerId}, função destino={funcao}, função observada={LerFuncao(jogador)}.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"Exceção ao trocar função do jogador={jogador.PlayerId}: {ex}");
            return false;
        }
    }

    private static bool Aplicar(PlayerControl jogador, RoleTypes funcao)
    {
        MethodInfo metodoCompatível = null;

        foreach (MethodInfo metodo in jogador.GetType().GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (metodo.Name != "RpcSetRole")
                continue;

            ParameterInfo[] parametros = metodo.GetParameters();
            if (parametros.Length == 2 &&
                parametros[0].ParameterType == typeof(RoleTypes) &&
                parametros[1].ParameterType == typeof(bool))
            {
                metodoCompatível = metodo;
                break;
            }
        }

        if (metodoCompatível == null)
        {
            LogErro("Não encontrei RpcSetRole(RoleTypes, bool). Não vou usar uma alternativa que ignore canOverrideRole.");
            return false;
        }

        metodoCompatível.Invoke(jogador, new object[] { funcao, true });

        LogInfo($"RpcSetRole chamado: jogador={jogador.PlayerId}, função={funcao}, canOverrideRole=true.");
        return true;
    }

    private static string LerFuncao(PlayerControl jogador)
    {
        try
        {
            object roleData = jogador?.Data?.Role;
            if (roleData == null)
                return "desconhecida";

            Type tipo = roleData.GetType();
            object papel = tipo.GetProperty("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);
            papel ??= tipo.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);
            return papel?.ToString() ?? "desconhecida";
        }
        catch
        {
            return "indisponível";
        }
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleSwitchAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[RoleSwitchAPI] " + mensagem);
}

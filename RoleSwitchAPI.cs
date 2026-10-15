using System;
using AmongUs.GameOptions;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// API central para trocar a função de um jogador durante a partida.
/// Primeiro força CrewmateGhost com canOverrideRole=true e depois envia a função desejada.
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
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected ||
            AmongUsClient.Instance == null)
        {
            LogAviso("Troca cancelada: jogador ou AmongUsClient indisponível.");
            return false;
        }

        if (!Enum.TryParse(typeof(RoleTypes), "CrewmateGhost", true, out object valorFantasma))
        {
            LogErro("RoleTypes não contém CrewmateGhost; não foi possível executar a troca segura.");
            return false;
        }

        RoleTypes fantasmaTripulante = (RoleTypes)valorFantasma;
        int dono = jogador.OwnerId;
        bool local = dono == AmongUsClient.Instance.ClientId;

        try
        {
            LogInfo($"Iniciando troca: jogador={jogador.PlayerId}, atual={LerFuncao(jogador)}, " +
                    $"intermediária={fantasmaTripulante}, destino={funcao}, local={local}.");

            // Etapa 1: aplicar fantasma tripulante. Para clientes remotos, o bool
            // true é o argumento canOverrideRole do RPC SetRole.
            if (!Aplicar(jogador, fantasmaTripulante, dono, local))
            {
                LogErro($"Falha ao aplicar {fantasmaTripulante} ao jogador={jogador.PlayerId}.");
                return false;
            }

            // Etapa 2: atribuir a função final, também permitindo sobrescrita.
            if (!Aplicar(jogador, funcao, dono, local))
            {
                LogErro($"Fantasma aplicado, mas a função final {funcao} falhou para jogador={jogador.PlayerId}.");
                return false;
            }

            LogInfo($"Troca concluída: jogador={jogador.PlayerId}, função solicitada={funcao}, " +
                    $"função observada={LerFuncao(jogador)}.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"Exceção ao trocar função do jogador={jogador.PlayerId}: {ex}");
            return false;
        }
    }

    private static bool Aplicar(PlayerControl jogador, RoleTypes funcao, int dono, bool local)
    {
        if (local)
        {
            if (RoleManager.Instance == null)
                return false;

            RoleManager.Instance.SetRole(jogador, funcao);

            if (jogador == PlayerControl.LocalPlayer && HudManager.Instance != null)
                HudManager.Instance.ReportButton.gameObject.SetActive(!RoleManager.IsGhostRole(funcao));

            LogInfo($"SetRole local: jogador={jogador.PlayerId}, função={funcao}.");
            return true;
        }

        MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
            jogador.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable, dono);
        writer.Write((ushort)funcao);
        writer.Write(true); // canOverrideRole = true
        AmongUsClient.Instance.FinishRpcImmediately(writer);

        LogInfo($"RPC SetRole enviado: jogador={jogador.PlayerId}, função={funcao}, " +
                $"destino={dono}, canOverrideRole=true.");
        return true;
    }

    private static string LerFuncao(PlayerControl jogador)
    {
        try
        {
            return jogador?.Data?.Role?.Role.ToString() ?? "desconhecida";
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

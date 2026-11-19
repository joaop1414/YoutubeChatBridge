using System.Collections;
using BepInEx.Unity.IL2CPP.Utils;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Sincroniza a abertura do RolePanel depois da introdução.
/// O host notifica somente clientes que confirmaram possuir o mod.
/// </summary>
internal static class RoleRevealSync
{
    internal const byte RpcRoleReveal = 252;

    private static bool notificacaoEnviada;
    private static bool aberturaAgendada;

    internal static void Resetar()
    {
        notificacaoEnviada = false;
        aberturaAgendada = false;
    }

    /// <summary>Chamado no OnDestroy da introdução, somente o host transmite.</summary>
    internal static void AoSairDaRoleReveal()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            notificacaoEnviada)
            return;

        notificacaoEnviada = true;
        AgendarAberturaLocal();

        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleRevealSync] PlayerControl.LocalPlayer nulo; não foi possível transmitir a abertura.");
            return;
        }

        foreach (int clientId in YouTubeVoteBridgePlugin.ClientesComModSnapshot())
        {
            if (clientId < 0 || clientId == AmongUsClient.Instance.ClientId)
                continue;

            MessageWriter writer = null;
            try
            {
                writer = AmongUsClient.Instance.StartRpcImmediately(
                    local.NetId,
                    RpcRoleReveal,
                    SendOption.Reliable,
                    clientId);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[RoleRevealSync] Notificação de Role Reveal enviada ao cliente modificado " +
                    clientId + ".");
            }
            catch (System.Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[RoleRevealSync] Falha ao notificar cliente " + clientId + ": " + ex.Message);
            }
        }
    }

    /// <summary>Chamado pelo handler do RPC no cliente modificado.</summary>
    internal static void Receber()
    {
        if (AmongUsClient.Instance == null || AmongUsClient.Instance.AmHost)
            return;

        AgendarAberturaLocal();
    }

    private static void AgendarAberturaLocal()
    {
        if (aberturaAgendada)
            return;

        aberturaAgendada = true;
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleRevealSync] Jogador local indisponível ao agendar RolePanel.");
            aberturaAgendada = false;
            return;
        }

        // Usa o jogador local como executor da coroutine; o HUD é consultado
        // somente depois da espera, evitando perder a notificação se ele ainda
        // estiver terminando de inicializar.
        local.StartCoroutine(AbrirPainelAposIntroducao());
    }

    private static IEnumerator AbrirPainelAposIntroducao()
    {
        // Mesmo intervalo usado pelo fluxo do EHR após IntroCutscene.OnDestroy.
        yield return new WaitForSecondsRealtime(1f);

        if (!HudManager.InstanceExists || HudManager.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleRevealSync] HUD não existe após a introdução; RolePanel não foi aberto.");
            aberturaAgendada = false;
            yield break;
        }

        Transform painel = HudManager.Instance.TaskStuff != null
            ? HudManager.Instance.TaskStuff.transform.Find("RolePanel")
            : null;

        if (painel == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleRevealSync] Não foi possível localizar TaskStuff/RolePanel.");
            aberturaAgendada = false;
            yield break;
        }

        TaskPanelBehaviour comportamento = painel.GetComponent<TaskPanelBehaviour>();
        if (comportamento == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[RoleRevealSync] RolePanel encontrado, mas TaskPanelBehaviour está nulo.");
            aberturaAgendada = false;
            yield break;
        }

        comportamento.open = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[RoleRevealSync] RolePanel aberto após a introdução.");
    }
}

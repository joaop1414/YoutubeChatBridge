using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private static bool ativo;
    private static bool armado;
    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        ativo = true;
        armado = true;
        if (ShipStatus.Instance != null) Aplicar();
    }

    internal static void IniciarPartida()
    {
        if (armado && ativo && ShipStatus.Instance != null) Aplicar();
    }

    private static void Aplicar()
    {
        if (!ativo || PlayerControl.AllPlayerControls == null) return;
        armado = false;
        int sucesso = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;
            if (InvocarMetodoVisibilidade(jogador, true)) sucesso++;
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Evento ativado: invisibilidade solicitada para {sucesso} jogador(es), até a próxima reunião.");
    }

    private static bool InvocarMetodoVisibilidade(PlayerControl jogador, bool invisivel)
    {
        if (jogador == null) return false;
        try
        {
            MethodInfo metodo = jogador.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => string.Equals(m.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase)
                    && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(bool));
            if (metodo == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade] Método SetRoleInvisibility(bool, bool, bool) não encontrado para PlayerId={jogador.PlayerId}.");
                return false;
            }
            metodo.Invoke(jogador, new object[] { invisivel, false, true });
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Falha ao alterar visibilidade de PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            return false;
        }
    }

    internal static void EncerrarNaReuniao()
    {
        if (!ativo) return;
        ativo = false;
        armado = false;
        if (PlayerControl.AllPlayerControls != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected || jogador.Data.IsDead)
                    continue;
                InvocarMetodoVisibilidade(jogador, false);
            }
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Invisibilidade] Evento encerrado na reunião; visibilidade restaurada.");
    }

    [HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.Start))]
    private static class ReuniaoInvisibilidadePatch
    {
        private static void Postfix()
        {
            if (YouTubeVoteBridgePlugin.EhHost()) EncerrarNaReuniao();
        }
    }
}

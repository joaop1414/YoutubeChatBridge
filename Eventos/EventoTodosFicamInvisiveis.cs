using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private static bool armado;

    public override string Nome => "Todos ficam invisíveis";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null)
            Aplicar();
    }

    internal static void IniciarPartida()
    {
        if (armado && ShipStatus.Instance != null)
            Aplicar();
    }

    private static void Aplicar()
    {
        armado = false;
        if (PlayerControl.AllPlayerControls == null) return;
        int sucesso = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;

            if (InvocarRpcInvisibilidade(jogador, true))
                sucesso++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Phantom] Evento 'Todos ficam invisíveis': RPC de invisibilidade enviado para {sucesso} jogador(es).");
    }

    internal static bool InvocarRpcInvisibilidade(PlayerControl jogador, bool invisivel)
    {
        if (jogador == null) return false;
        try
        {
            MethodInfo metodo = jogador.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m =>
                    (m.Name.StartsWith("Rpc", StringComparison.OrdinalIgnoreCase) &&
                     (m.Name.IndexOf("Invisib", StringComparison.OrdinalIgnoreCase) >= 0 ||
                      m.Name.IndexOf("Vanish", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                    string.Equals(m.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(bool);
                });

            if (metodo == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Phantom] Não encontrei um RPC de invisibilidade com um parâmetro bool em PlayerControl.");
                return false;
            }

            metodo.Invoke(jogador, new object[] { invisivel });
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Phantom] Falha ao chamar RPC de invisibilidade para PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            return false;
        }
    }
}

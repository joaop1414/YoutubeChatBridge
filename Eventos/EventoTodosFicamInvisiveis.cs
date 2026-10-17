using System;
using System.Reflection;
using System.Linq;
using HarmonyLib;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private static bool ativo;
    private static bool armado;
    public static bool AplicandoEvento { get; private set; }

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
        AplicandoEvento = true;
        int sucesso = 0;

        try
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || jogador.Data.IsDead)
                    continue;

                if (DefinirInvisibilidadeVisual(jogador, true))
                    sucesso++;
            }
        }
        finally
        {
            AplicandoEvento = false;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade] Evento aplicado visualmente a {sucesso} jogador(es); termina na próxima reunião.");
    }

    private static bool DefinirInvisibilidadeVisual(PlayerControl jogador, bool invisivel)
    {
        try
        {
            // Usa o mesmo mecanismo nativo de invisibilidade do Phantom para cada jogador.
            // AplicandoEvento sinaliza ao patch de bloqueio que esta chamada é do evento,
            // não uma tentativa de ativar a habilidade do Phantom.
            MethodInfo metodoNativo = typeof(PlayerControl).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "SetRoleInvisibility", StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length >= 1 &&
                    m.GetParameters()[0].ParameterType == typeof(bool));

            if (metodoNativo != null)
            {
                ParameterInfo[] parametros = metodoNativo.GetParameters();
                object[] argumentos = new object[parametros.Length];
                argumentos[0] = invisivel;
                for (int i = 1; i < parametros.Length; i++)
                    argumentos[i] = parametros[i].HasDefaultValue
                        ? parametros[i].DefaultValue
                        : (parametros[i].ParameterType.IsValueType
                            ? Activator.CreateInstance(parametros[i].ParameterType)
                            : null);
                metodoNativo.Invoke(jogador, argumentos);
            }

            // Mantém os campos visuais como complemento/fallback local.
            jogador.invisibilityAlpha = invisivel
                ? (jogador.AmOwner ? 0.5f : (PlayerControl.LocalPlayer?.Data?.Role?.IsDead == true ? 0.5f : 0f))
                : 1f;

            jogador.shouldAppearInvisible = invisivel;
            jogador.Visible = invisivel ? jogador.AmOwner : !jogador.inVent;

            if (jogador.cosmetics != null)
            {
                MethodInfo alpha = jogador.cosmetics.GetType().GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, "SetPhantomRoleAlpha", StringComparison.OrdinalIgnoreCase) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(float));

                alpha?.Invoke(jogador.cosmetics, new object[] { jogador.invisibilityAlpha });
            }

            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade] Não foi possível alterar visualmente PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
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
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || jogador.Data.IsDead)
                    continue;

                DefinirInvisibilidadeVisual(jogador, false);
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
            if (YouTubeVoteBridgePlugin.EhHost())
                EncerrarNaReuniao();
        }
    }
}

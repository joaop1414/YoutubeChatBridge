using System;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTodosFicamInvisiveis : EventoVotacao
{
    private static bool ativo;
    private static bool armado;
    private static readonly System.Collections.Generic.HashSet<byte> invisiveis = new();

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
        if (!ativo || PlayerControl.AllPlayerControls == null || AmongUsClient.Instance == null) return;
        armado = false;
        invisiveis.Clear();
        int sucesso = 0;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;

            if (DefinirInvisibilidadeLocal(jogador, true))
            {
                invisiveis.Add(jogador.PlayerId);
                EnviarSnapParaClientes(jogador, false);
                sucesso++;
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Invisibilidade/EHR] Aplicada a {sucesso} jogador(es). Para clientes remotos, usa SnapTo direcionado para fora da tela; termina na próxima reunião.");
    }

    // Equivalente ao RpcMakeInvisible do EHR: no cliente local altera alpha/visibilidade;
    // para cada outro cliente envia SnapTo direcionado para fora da tela. Clientes vanilla
    // não entendem RPCs personalizados, mas processam o RPC nativo SnapTo.
    private static bool DefinirInvisibilidadeLocal(PlayerControl jogador, bool invisivel)
    {
        try
        {
            jogador.invisibilityAlpha = invisivel
                ? (jogador.AmOwner ? 0.5f : (PlayerControl.LocalPlayer?.Data?.Role?.IsDead == true ? 0.5f : 0f))
                : 1f;
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

            if (!jogador.AmOwner)
            {
                jogador.shouldAppearInvisible = invisivel;
                jogador.Visible = invisivel ? false : !jogador.inVent;
            }
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Invisibilidade/EHR] Falha visual para PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static void EnviarSnapParaClientes(PlayerControl alvo, bool restaurar)
    {
        if (alvo?.NetTransform == null || AmongUsClient.Instance == null ||
            PlayerControl.AllPlayerControls == null)
            return;

        int clientHost = AmongUsClient.Instance.ClientId;
        var clientes = new System.Collections.Generic.HashSet<int>();
        foreach (PlayerControl espectador in PlayerControl.AllPlayerControls)
        {
            if (espectador == null || espectador.Data == null || espectador.Data.Disconnected ||
                espectador.OwnerId < 0 || espectador.OwnerId == clientHost ||
                espectador.OwnerId == alvo.OwnerId || !clientes.Add(espectador.OwnerId))
                continue;

            try
            {
                ushort seq = alvo.NetTransform.lastSequenceId;
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(6); // GameDataTo: apenas este cliente
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(espectador.OwnerId);

                if (!restaurar)
                {
                    EscreverSnap(writer, alvo, new Vector2(50f, 50f), seq);
                    EscreverSnap(writer, alvo, new Vector2(50f, 50f), (ushort)(seq + 16383));
                }
                else
                {
                    EscreverSnap(writer, alvo, new Vector2(50f, 50f), (ushort)(seq + 32767));
                    EscreverSnap(writer, alvo, new Vector2(50f, 50f), (ushort)(seq + 32767 + 16383));
                    Vector3 pos = alvo.transform.position;
                    EscreverSnap(writer, alvo, new Vector2(pos.x, pos.y), seq);
                }

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer.Recycle();
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Invisibilidade/EHR] Falha ao enviar SnapTo para ClientId={espectador.OwnerId}, PlayerId={alvo.PlayerId}: {ex.GetBaseException().Message}");
            }
        }
    }

    private static void EscreverSnap(MessageWriter writer, PlayerControl alvo, Vector2 pos, ushort sequence)
    {
        writer.StartMessage(2);
        writer.WritePacked(alvo.NetTransform.NetId);
        writer.Write((byte)RpcCalls.SnapTo);
        writer.WriteVector2(pos);
        writer.Write(sequence);
        writer.EndMessage();
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
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                    continue;

                if (invisiveis.Contains(jogador.PlayerId))
                {
                    EnviarSnapParaClientes(jogador, true);
                    DefinirInvisibilidadeLocal(jogador, false);
                }
            }
        }

        invisiveis.Clear();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Invisibilidade/EHR] Evento encerrado na reunião; visibilidade restaurada.");
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

using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;

namespace YouTubeVoteBridge;

internal sealed class EventoReviverJogadorMorto : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    private static readonly Dictionary<byte, RoleTypes> FuncoesAntesDaMorte = new();

    public override string Nome => "Reviver um jogador morto";

    // Guarda a função enquanto o jogador ainda está vivo, antes que o jogo
    // substitua a função original pela variante Ghost.
    public static void RegistrarFuncoesVivas()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null || jogador.Data.Disconnected || jogador.Data.IsDead ||
                jogador.Data.Role == null)
                continue;
            try { FuncoesAntesDaMorte[jogador.PlayerId] = jogador.Data.Role.Role; }
            catch { }
        }
    }

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        RegistrarFuncoesVivas();

        List<PlayerControl> mortos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && jogador.Data.IsDead && !jogador.Data.Disconnected)
                mortos.Add(jogador);

        if (mortos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Reviver jogador cancelado: não há jogadores mortos elegíveis.");
            return;
        }

        PlayerControl alvo = mortos[Sorteador.Next(mortos.Count)];
        RoleTypes funcaoOriginal = ObterFuncaoOriginal(alvo);
        try
        {
            // Prioriza o método RPC para que a ressurreição também chegue aos
            // clientes vanilla; usa Revive local como alternativa da versão.
            bool reviveExecutado = InvocarMetodoSemParametros(alvo, "RpcRevive");
            if (!reviveExecutado)
                reviveExecutado = InvocarMetodoSemParametros(alvo, "Revive");

            if (!reviveExecutado)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não encontrei RpcRevive() nem Revive() sem parâmetros nesta versão; ressurreição cancelada.");
                return;
            }

            // Repõe a função anterior à morte em vez de deixar CrewmateGhost/
            // ImpostorGhost. RpcSetRole é replicado pelo jogo para clientes vanilla.
            alvo.RpcSetRole(funcaoOriginal, true);
            FuncoesAntesDaMorte.Remove(alvo.PlayerId);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Ressurreição solicitada para " + (alvo.Data?.PlayerName ?? "jogador") +
                "; função restaurada para " + funcaoOriginal + " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao reviver jogador morto: " + (ex.InnerException ?? ex));
        }
    }

    private static RoleTypes ObterFuncaoOriginal(PlayerControl jogador)
    {
        if (FuncoesAntesDaMorte.TryGetValue(jogador.PlayerId, out RoleTypes funcao))
            return funcao;

        // Reserva para jogadores mortos antes de o mod começar a registrar funções.
        try
        {
            if (jogador.Data?.Role != null && jogador.Data.Role.IsImpostor)
                return RoleTypes.Impostor;
        }
        catch { }
        return RoleTypes.Crewmate;
    }

    private static bool InvocarMetodoSemParametros(PlayerControl jogador, string nome)
    {
        MethodInfo metodo = jogador.GetType().GetMethod(
            nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (metodo == null) return false;
        metodo.Invoke(jogador, null);
        return true;
    }
}
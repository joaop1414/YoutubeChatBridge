using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;

namespace YouTubeVoteBridge;

/// <summary>
/// Primeira etapa do seletor próprio de funções.
/// Substitui RoleManager.SelectRoles em todas as partidas padrão por uma
/// distribuição determinística em termos de contagem: escolhe exatamente
/// o número de impostores configurado e atribui Crewmate aos demais.
/// A distribuição de funções especiais vanilla será adicionada na próxima
/// etapa; por enquanto, o seletor não consulta as taxas de funções especiais.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SeletorProprioDeFuncoesPatch
{
    private static readonly System.Random Sorteador = new();

    public static bool Prefix()
    {
        // O host é a autoridade da atribuição. Caso a função seja chamada em
        // outro cliente, deixa o jogo seguir seu próprio fluxo local.
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return true;

        try
        {
            return !SeletorProprioDeFuncoes.Atribuir();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[SeletorDeFuncoes] Erro ao substituir SelectRoles; liberando o seletor vanilla como fallback: " + ex);
            return true;
        }
    }

    internal static int SortearIndice(int maxExclusive) => Sorteador.Next(maxExclusive);
}

internal static class SeletorProprioDeFuncoes
{
    internal static bool Atribuir()
    {
        var cliente = AmongUsClient.Instance;
        var gerenciador = RoleManager.Instance;

        if (cliente == null || !cliente.AmHost || gerenciador == null)
            return false;

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            jogadores.Add(jogador);
        }

        if (jogadores.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[SeletorDeFuncoes] Nenhum jogador válido; usando seletor vanilla.");
            return false;
        }

        int impostoresConfigurados = 1;
        try
        {
            impostoresConfigurados = GameOptionsManager.Instance.currentNormalGameOptions.NumImpostors;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[SeletorDeFuncoes] Não consegui ler NumImpostors; usando 1. " + ex.Message);
        }

        int quantidadeImpostores = Math.Clamp(impostoresConfigurados, 1, Math.Max(1, jogadores.Count - 1));
        // Em salas com apenas um jogador, não há como respeitar a regra de
        // manter ao menos um tripulante; ainda assim, não inventamos jogadores.
        if (jogadores.Count == 1)
            quantidadeImpostores = 1;

        // Fisher–Yates: a escolha não depende da ordem em que o jogo enumera
        // PlayerControl.AllPlayerControls.
        for (int i = jogadores.Count - 1; i > 0; i--)
        {
            int j = SeletorProprioDeFuncoesPatch.SortearIndice(i + 1);
            (jogadores[i], jogadores[j]) = (jogadores[j], jogadores[i]);
        }

        var atribuicoes = new List<(PlayerControl Jogador, RoleTypes Funcao)>(jogadores.Count);
        for (int i = 0; i < jogadores.Count; i++)
        {
            RoleTypes funcao = i < quantidadeImpostores
                ? RoleTypes.Impostor
                : RoleTypes.Crewmate;
            atribuicoes.Add((jogadores[i], funcao));
        }

        // Primeiro aplica localmente todas as funções, depois envia os RPCs.
        // Isso evita que a atribuição local seja observada parcialmente enquanto
        // os RPCs estão sendo enviados.
        foreach (var atribuicao in atribuicoes)
            gerenciador.SetRole(atribuicao.Jogador, atribuicao.Funcao);

        foreach (var atribuicao in atribuicoes)
        {
            MessageWriter writer = cliente.StartRpcImmediately(
                atribuicao.Jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                -1);
            writer.Write((ushort)atribuicao.Funcao);
            writer.Write(true); // canOverrideRole: permite substituir qualquer papel anterior
            cliente.FinishRpcImmediately(writer);
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[SeletorDeFuncoes] Atribuição própria aplicada: jogadores={jogadores.Count}, " +
            $"impostores={quantidadeImpostores}, tripulantes={jogadores.Count - quantidadeImpostores}. " +
            "Nesta etapa, funções especiais vanilla estão temporariamente desativadas.");

        return true;
    }
}

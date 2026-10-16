using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;

namespace YouTubeVoteBridge;

/// <summary>
/// Substitui RoleManager.SelectRoles no host. A quantidade de impostores vem
/// das opções da partida; as funções especiais são consultadas por reflexão
/// para evitar acoplamento a mudanças pequenas na API de GameOptions.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class SeletorProprioDeFuncoesPatch
{
    private static readonly System.Random Sorteador = new();

    // Temporariamente desativado: a seleção personalizada causou tela preta
    // em testes com clientes vanilla. Deixa o jogo executar o seletor original.
    public static bool Prefix()
    {
        return true;
    }

    internal static int SortearIndice(int maxExclusive) => Sorteador.Next(maxExclusive);
}

internal static class SeletorProprioDeFuncoes
{
    private static readonly RoleTypes[] FuncoesTripulantes =
    {
        RoleTypes.Scientist,
        RoleTypes.Engineer,
        RoleTypes.Tracker,
        RoleTypes.Detective,
        RoleTypes.Noisemaker
    };

    private static readonly RoleTypes[] FuncoesImpostores =
    {
        RoleTypes.Shapeshifter,
        RoleTypes.Phantom,
        RoleTypes.Viper
    };

    internal static bool Atribuir()
    {
        var cliente = AmongUsClient.Instance;
        var gerenciador = RoleManager.Instance;
        if (cliente == null || !cliente.AmHost || gerenciador == null)
            return false;

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null && !jogador.Data.Disconnected)
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
        if (jogadores.Count == 1)
            quantidadeImpostores = 1;

        Embaralhar(jogadores);

        List<PlayerControl> impostores = jogadores.Take(quantidadeImpostores).ToList();
        List<PlayerControl> tripulantes = jogadores.Skip(quantidadeImpostores).ToList();
        var atribuicoes = new Dictionary<byte, (PlayerControl Jogador, RoleTypes Funcao)>();

        foreach (PlayerControl jogador in impostores)
            atribuicoes[jogador.PlayerId] = (jogador, RoleTypes.Impostor);
        foreach (PlayerControl jogador in tripulantes)
            atribuicoes[jogador.PlayerId] = (jogador, RoleTypes.Crewmate);

        object opcoesPapeis = ObterOpcoesPapeis();
        if (opcoesPapeis != null)
        {
            AtribuirFuncoesEspeciais(impostores, FuncoesImpostores, opcoesPapeis, atribuicoes);
            AtribuirFuncoesEspeciais(tripulantes, FuncoesTripulantes, opcoesPapeis, atribuicoes);
        }
        else
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[SeletorDeFuncoes] Não consegui ler as opções de funções especiais; atribuindo apenas Impostor/Crewmate.");
        }

        // Define o papel local no host e então replica o SetRole globalmente.
        foreach (var atribuicao in atribuicoes.Values)
            gerenciador.SetRole(atribuicao.Jogador, atribuicao.Funcao);

        foreach (var atribuicao in atribuicoes.Values)
        {
            MessageWriter writer = cliente.StartRpcImmediately(
                atribuicao.Jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                -1);
            writer.Write((ushort)atribuicao.Funcao);
            writer.Write(true);
            cliente.FinishRpcImmediately(writer);
        }

        int quantidadeEspeciais = atribuicoes.Values.Count(x =>
            x.Funcao != RoleTypes.Crewmate && x.Funcao != RoleTypes.Impostor);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[SeletorDeFuncoes] Atribuição própria enviada: jogadores={jogadores.Count}, " +
            $"impostores={quantidadeImpostores}, tripulantes={jogadores.Count - quantidadeImpostores}, " +
            $"funções especiais={quantidadeEspeciais}.");

        return true;
    }

    private static void AtribuirFuncoesEspeciais(
        List<PlayerControl> candidatos,
        RoleTypes[] funcoes,
        object opcoesPapeis,
        Dictionary<byte, (PlayerControl Jogador, RoleTypes Funcao)> atribuicoes)
    {
        // Em cada função, a chance é rolada uma vez por partida. O número de
        // jogadores afetados nunca excede o limite configurado nem os candidatos.
        foreach (RoleTypes funcao in funcoes)
        {
            int chance = LerOpcaoInteira(opcoesPapeis, "GetChancePerGame", funcao, 0);
            int limite = LerOpcaoInteira(opcoesPapeis, "GetRoleCount", funcao, 0);
            if (chance <= 0 || limite <= 0 || candidatos.Count == 0)
                continue;

            if (SeletorProprioDeFuncoesPatch.SortearIndice(100) >= Math.Clamp(chance, 0, 100))
                continue;

            int atribuir = Math.Min(limite, candidatos.Count);
            for (int i = 0; i < atribuir; i++)
            {
                PlayerControl jogador = candidatos[0];
                candidatos.RemoveAt(0);
                atribuicoes[jogador.PlayerId] = (jogador, funcao);
            }
        }
    }

    private static object ObterOpcoesPapeis()
    {
        object opcoesPartida = GameOptionsManager.Instance?.currentNormalGameOptions;
        if (opcoesPartida == null)
            return null;

        Type tipo = opcoesPartida.GetType();
        return tipo.GetProperty("roleOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(opcoesPartida)
            ?? tipo.GetField("roleOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(opcoesPartida);
    }

    private static int LerOpcaoInteira(object alvo, string nomeMetodo, RoleTypes funcao, int padrao)
    {
        try
        {
            MethodInfo metodo = alvo.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m =>
                {
                    if (m.Name != nomeMetodo)
                        return false;
                    ParameterInfo[] p = m.GetParameters();
                    return p.Length == 1 && p[0].ParameterType == typeof(RoleTypes);
                });

            object valor = metodo?.Invoke(alvo, new object[] { funcao });
            return valor == null ? padrao : Convert.ToInt32(valor);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[SeletorDeFuncoes] Não consegui ler {nomeMetodo}({funcao}): {(ex.InnerException ?? ex).Message}");
            return padrao;
        }
    }

    private static void Embaralhar<T>(IList<T> lista)
    {
        for (int i = lista.Count - 1; i > 0; i--)
        {
            int j = SeletorProprioDeFuncoesPatch.SortearIndice(i + 1);
            (lista[i], lista[j]) = (lista[j], lista[i]);
        }
    }
}

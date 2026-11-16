using System;
using System.Collections.Generic;
using System.Linq;

namespace YouTubeVoteBridge;

/// <summary>Registro autoritativo de funções atribuídas pelo mod, indexado pelo PlayerId.</summary>
internal static class RegistroFuncoesJogadores
{
    internal sealed class Atribuicao
    {
        public byte PlayerId { get; }
        public RoleTypes FuncaoVanilla { get; private set; }
        public string FuncaoPersonalizada { get; private set; }
        public bool Personalizada => !string.IsNullOrWhiteSpace(FuncaoPersonalizada);

        public Atribuicao(byte playerId, RoleTypes vanilla, string personalizada = null)
        {
            PlayerId = playerId;
            FuncaoVanilla = vanilla;
            FuncaoPersonalizada = personalizada;
        }

        internal void Atualizar(RoleTypes vanilla, string personalizada = null)
        {
            FuncaoVanilla = vanilla;
            FuncaoPersonalizada = personalizada;
        }
    }

    private static readonly Dictionary<byte, Atribuicao> registros = new();
    private static readonly object sync = new();

    internal static void Registrar(PlayerControl jogador, RoleTypes vanilla, string personalizada = null)
    {
        if (jogador == null || jogador.Data == null) return;
        lock (sync)
        {
            if (registros.TryGetValue(jogador.PlayerId, out var existente))
                existente.Atualizar(vanilla, personalizada);
            else
                registros[jogador.PlayerId] = new Atribuicao(jogador.PlayerId, vanilla, personalizada);
        }
    }

    internal static void RegistrarDistribuicaoVanilla()
    {
        if (PlayerControl.AllPlayerControls == null) return;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected) continue;
            Registrar(jogador, jogador.Data.RoleType);
        }
    }

    internal static void RegistrarPersonalizada(PlayerControl jogador, string nome, RoleTypes baseVanilla)
        => Registrar(jogador, baseVanilla, nome);

    internal static Atribuicao Obter(byte playerId)
    {
        lock (sync) return registros.TryGetValue(playerId, out var item) ? item : null;
    }

    internal static string ObterNome(byte playerId)
    {
        Atribuicao item = Obter(playerId);
        return item?.FuncaoPersonalizada ?? item?.FuncaoVanilla.ToString();
    }

    internal static IReadOnlyDictionary<byte, Atribuicao> Snapshot()
    {
        lock (sync) return new Dictionary<byte, Atribuicao>(registros);
    }

    internal static void Remover(byte playerId)
    {
        lock (sync) registros.Remove(playerId);
    }

    internal static void Limpar()
    {
        lock (sync) registros.Clear();
    }
}

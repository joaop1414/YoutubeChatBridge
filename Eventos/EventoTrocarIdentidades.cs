using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace YouTubeVoteBridge;

// Troca temporariamente os nomes e os cosméticos de dois jogadores reais.
// As alterações são enviadas pelos RPCs normais do jogo e restauradas após 20s.
internal sealed class EventoTrocarIdentidades : EventoVotacao
{
    private const float DuracaoSegundos = 20f;
    private static readonly System.Random Sorteador = new();

    private sealed class Identidade
    {
        public PlayerControl Jogador;
        public string Nome;
        public int Cor;
        public string Chapeu;
        public string Roupa;
        public string Visor;
        public string Mascote;
    }

    private static readonly List<Identidade> IdentidadesAtivas = new();
    private static float ExpiraEm;

    public override string Nome => "Trocar identidades de dois jogadores por 20 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        // Evita perder os dados originais se o evento for sorteado novamente.
        if (IdentidadesAtivas.Count > 0)
            Restaurar();

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data != null && !jogador.Data.Disconnected)
                jogadores.Add(jogador);
        }

        if (jogadores.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Identidades] Evento cancelado: são necessários dois jogadores conectados.");
            return;
        }

        int primeiro = Sorteador.Next(jogadores.Count);
        int segundo;
        do { segundo = Sorteador.Next(jogadores.Count); } while (segundo == primeiro);

        Identidade a = Capturar(jogadores[primeiro]);
        Identidade b = Capturar(jogadores[segundo]);
        if (a == null || b == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Identidades] Não foi possível capturar os dados dos dois jogadores.");
            return;
        }

        IdentidadesAtivas.Add(a);
        IdentidadesAtivas.Add(b);

        AplicarNome(a.Jogador, b.Nome);
        AplicarAparencia(a.Jogador, b);
        AplicarNome(b.Jogador, a.Nome);
        AplicarAparencia(b.Jogador, a);

        ExpiraEm = Time.realtimeSinceStartup + DuracaoSegundos;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Identidades] Identidades trocadas entre PlayerId={a.Jogador.PlayerId} e " +
            $"PlayerId={b.Jogador.PlayerId} por {DuracaoSegundos}s.");
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || IdentidadesAtivas.Count == 0)
            return;

        if (Time.realtimeSinceStartup >= ExpiraEm)
            Restaurar();
    }

    private static Identidade Capturar(PlayerControl jogador)
    {
        try
        {
            object outfit = jogador.Data.DefaultOutfit;
            return new Identidade
            {
                Jogador = jogador,
                Nome = jogador.Data.PlayerName ?? string.Empty,
                Cor = jogador.Data.DefaultOutfit.ColorId,
                Chapeu = LerTexto(outfit, "HatId"),
                Roupa = LerTexto(outfit, "SkinId"),
                Visor = LerTexto(outfit, "VisorId"),
                Mascote = LerTexto(outfit, "PetId")
            };
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Identidades] Falha ao capturar jogador: " + ex.Message);
            return null;
        }
    }

    private static string LerTexto(object origem, string nome)
    {
        if (origem == null)
            return string.Empty;

        Type tipo = origem.GetType();
        object valor = tipo.GetProperty(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(origem);
        valor ??= tipo.GetField(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(origem);
        return valor?.ToString() ?? string.Empty;
    }

    private static void AplicarNome(PlayerControl jogador, string nome)
    {
        InvocarRpcTexto(jogador, "RpcSetName", nome ?? string.Empty);
    }

    private static void AplicarAparencia(PlayerControl jogador, Identidade identidade)
    {
        try { jogador.RpcSetColor((byte)Math.Clamp(identidade.Cor, 0, 255)); }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Identidades] Falha ao aplicar cor: " + ex.Message);
        }

        InvocarRpcTexto(jogador, "RpcSetHat", identidade.Chapeu);
        InvocarRpcTexto(jogador, "RpcSetSkin", identidade.Roupa);
        InvocarRpcTexto(jogador, "RpcSetVisor", identidade.Visor);
        InvocarRpcTexto(jogador, "RpcSetPet", identidade.Mascote);
    }

    private static void InvocarRpcTexto(PlayerControl jogador, string nomeMetodo, string valor)
    {
        if (jogador == null)
            return;

        try
        {
            foreach (MethodInfo metodo in jogador.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (metodo.Name != nomeMetodo)
                    continue;

                ParameterInfo[] parametros = metodo.GetParameters();
                if (parametros.Length != 1 || parametros[0].ParameterType != typeof(string))
                    continue;

                metodo.Invoke(jogador, new object[] { valor ?? string.Empty });
                return;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                $"[Identidades] Método {nomeMetodo}(string) não encontrado.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Identidades] Falha em {nomeMetodo}: " + (ex.InnerException ?? ex));
        }
    }

    private static void Restaurar()
    {
        try
        {
            foreach (Identidade identidade in IdentidadesAtivas)
            {
                if (identidade?.Jogador == null || identidade.Jogador.Data == null ||
                    identidade.Jogador.Data.Disconnected)
                    continue;

                AplicarNome(identidade.Jogador, identidade.Nome);
                AplicarAparencia(identidade.Jogador, identidade);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Identidades] Nomes e aparências originais restaurados.");
        }
        finally
        {
            IdentidadesAtivas.Clear();
            ExpiraEm = 0f;
        }
    }
}

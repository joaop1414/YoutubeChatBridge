using System;
using System.Collections;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// API para criar e controlar PlayerControls artificiais para clientes vanilla.
/// </summary>
public static class FakePlayerAPI
{
    public const byte PlayerIdFalso = 253;
    private static readonly HashSet<PlayerControl> ObjetosCriados = new();

    /// <summary>
    /// Cria personagem artificial. Nome e cor são enviados por RPC após a associação.
    /// </summary>
    public static PlayerControl Criar(Vector2 posicaoInicial, string nome = "Bot", byte corId = 0)
    {
        if (!PodeCriar(out string motivo))
        {
            LogAviso("Criação cancelada: " + motivo);
            return null;
        }

        PlayerControl falso = null;
        try
        {
            falso = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, Vector2.zero, Quaternion.identity);

            falso.PlayerId = PlayerIdFalso;
            falso.isNew = false;
            falso.notRealPlayer = true;
            falso.NetTransform.SnapTo(new Vector2(50f, 50f));

            LogDiagnostico("ANTES DO SPAWN", falso, nome, corId);
            CustomNetObject.EnviarSpawn(falso);
            ObjetosCriados.Add(falso);
            LogDiagnostico("DEPOIS DO SPAWN", falso, nome, corId);

            // O EHR espera 0,1 s após o spawn antes de associar os clientes.
            // Não fazer spawn + associação no mesmo frame.
            AmongUsClient.Instance.StartCoroutine(
                FinalizarCriacaoAposSpawn(falso, posicaoInicial, nome, corId));

            return falso;
        }
        catch (Exception ex)
        {
            LogErro("Falha ao criar PlayerControl artificial: " + ex);
            if (falso != null)
                DestruirInterno(falso, enviarRemocao: false);
            return null;
        }
    }

    private static IEnumerator FinalizarCriacaoAposSpawn(
        PlayerControl falso, Vector2 posicaoInicial, string nome, byte corId)
    {
        // Mesmos intervalos do EHR: 0,1 s para o spawn ser processado,
        // associação individual por cliente e mais 0,15 s antes do Shapeshift.
        yield return new UnityEngine.WaitForSecondsRealtime(0.1f);

        if (falso == null || !ObjetosCriados.Contains(falso) ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            yield break;

        bool associacaoOk = true;
        try
        {
            int associados = CustomNetObject.AssociarClientes(falso, PlayerIdFalso);
            LogInfo($"Associação concluída. NetId={falso.NetId}; clientes associados={associados}.");
            LogDiagnostico("DEPOIS DA ASSOCIACAO", falso, nome, corId);
        }
        catch (Exception ex)
        {
            associacaoOk = false;
            LogErro("Falha ao associar o personagem falso aos clientes: " + ex);
        }

        if (!associacaoOk)
            yield break;

        yield return new UnityEngine.WaitForSecondsRealtime(0.15f);

        if (falso == null || !ObjetosCriados.Contains(falso) ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            yield break;

        try
        {
            CustomNetObject.EnviarAparencia(falso, nome, corId);
            LogDiagnostico("DEPOIS DE ENVIAR APARENCIA", falso, nome, corId);

            if (!Mover(falso, posicaoInicial))
                LogAviso("O personagem foi criado, mas a posição não pôde ser sincronizada.");

            LogDiagnostico("FINAL DA CRIACAO", falso, nome, corId);
        }
        catch (Exception ex)
        {
            LogErro("Falha ao finalizar a aparência/posição do personagem falso: " + ex);
        }
    }

    public static bool Mover(PlayerControl falso, Vector2 novaPosicao)
    {
        if (!EhObjetoGerenciado(falso) ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        try
        {
            CustomNetObject.EnviarPosicao(falso, novaPosicao);
            return true;
        }
        catch (Exception ex)
        {
            LogErro("Falha ao mover personagem artificial: " + ex);
            return false;
        }
    }

    public static bool Destruir(PlayerControl falso)
    {
        if (!EhObjetoGerenciado(falso) ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        return DestruirInterno(falso, enviarRemocao: true);
    }

    public static void DestruirTodos()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        foreach (PlayerControl falso in new List<PlayerControl>(ObjetosCriados))
            DestruirInterno(falso, enviarRemocao: true);
    }

    public static int Quantidade => ObjetosCriados.Count;

    private static bool PodeCriar(out string motivo)
    {
        motivo = null;
        if (AmongUsClient.Instance == null) motivo = "AmongUsClient.Instance é null.";
        else if (!AmongUsClient.Instance.AmHost) motivo = "somente o host pode criar personagens artificiais.";
        else if (PlayerControl.LocalPlayer == null) motivo = "PlayerControl.LocalPlayer é null.";
        else if (AmongUsClient.Instance.PlayerPrefab == null) motivo = "PlayerPrefab não foi encontrado.";
        else if (GameManager.Instance == null || ShipStatus.Instance == null) motivo = "a partida ainda não está carregada.";
        return motivo == null;
    }

    private static bool EhObjetoGerenciado(PlayerControl falso) =>
        falso != null && ObjetosCriados.Contains(falso);

    private static bool DestruirInterno(PlayerControl falso, bool enviarRemocao)
    {
        if (falso == null)
        {
            ObjetosCriados.Remove(falso);
            return false;
        }

        bool sucesso = true;
        uint netId = falso.NetId;

        try
        {
            if (enviarRemocao && AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost)
            {
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    writer.StartMessage(5);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.StartMessage(5);
                    writer.WritePacked(netId);
                    writer.EndMessage();
                    writer.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                    writer = null;
                }
                finally { writer?.Recycle(); }
            }
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogErro("Falha ao enviar remoção: " + ex);
        }

        try
        {
            if (PlayerControl.AllPlayerControls.Contains(falso))
                PlayerControl.AllPlayerControls.Remove(falso);
        }
        catch (Exception ex) { LogAviso("Falha ao remover de AllPlayerControls: " + ex.Message); }

        try { AmongUsClient.Instance?.RemoveNetObject(falso); }
        catch (Exception ex) { sucesso = false; LogAviso("Falha ao remover NetObject: " + ex.Message); }

        try { UnityEngine.Object.Destroy(falso.gameObject); }
        catch (Exception ex) { sucesso = false; LogAviso("Falha ao destruir GameObject: " + ex.Message); }

        ObjetosCriados.Remove(falso);
        LogInfo($"Personagem artificial removido. NetId={netId}.");
        return sucesso;
    }

    private static void LogDiagnostico(string etapa, PlayerControl falso, string nomeSolicitado, byte corSolicitada)
    {
        try
        {
            string nomeReal = falso?.Data?.PlayerName ?? "<Data/PlayerName null>";
            string nomeCache = falso?.CachedPlayerData?.PlayerName ?? "<CachedPlayerData null>";
            string corReal = falso?.Data != null
                ? falso.Data.DefaultOutfit.ColorId.ToString()
                : "<Data null>";
            bool naLista = falso != null && PlayerControl.AllPlayerControls.Contains(falso);
            LogInfo(
                $"[DIAG:{etapa}] " +
                $"AmHost={AmongUsClient.Instance?.AmHost}; " +
                $"LocalPlayerId={PlayerControl.LocalPlayer?.PlayerId}; " +
                $"FakePlayerId={falso?.PlayerId}; NetId={falso?.NetId}; " +
                $"DataNetId={falso?.Data?.NetId}; " +
                $"NomeSolicitado='{nomeSolicitado}'; NomeData='{nomeReal}'; " +
                $"NomeCache='{nomeCache}'; CorSolicitada={corSolicitada}; CorData={corReal}; " +
                $"NaAllPlayerControls={naLista}; " +
                $"OwnerId={falso?.OwnerId}; CachedPlayerDataIgualLocal=" +
                $"{(falso?.CachedPlayerData != null && falso.CachedPlayerData == PlayerControl.LocalPlayer?.Data)}."
            );

            if (AmongUsClient.Instance != null)
            {
                foreach (ClientData cliente in AmongUsClient.Instance.allClients)
                {
                    if (cliente != null)
                        LogInfo($"[DIAG:CLIENTE] ClientId={cliente.Id}; Nome='{cliente.PlayerName}'; " +
                            $"CharacterPlayerId={cliente.Character?.PlayerId}; " +
                            $"CharacterNetId={cliente.Character?.NetId}.");
                }
            }
        }
        catch (Exception ex)
        {
            LogAviso($"Diagnóstico falhou na etapa '{etapa}': {ex.Message}");
        }
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[FakePlayerAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[FakePlayerAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[FakePlayerAPI] " + mensagem);
}

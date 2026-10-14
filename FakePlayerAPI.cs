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
    // 253 é reservado à API; 254 fica exclusivo do indicador visual do tsunami.
    public const byte PlayerIdFalso = CompatibilidadeClientes.PlayerIdFakeApi;
    private static readonly HashSet<PlayerControl> ObjetosCriados = new();

    private sealed class CriacaoPendente
    {
        public PlayerControl Falso;
        public Vector2 Posicao;
        public string Nome;
        public byte CorId;
        public float ExecutarEm;
        public int Etapa;
    }

    private static readonly List<CriacaoPendente> CriacoesPendentes = new();

    /// <summary>
    /// Cria personagem artificial. Nome e cor são enviados por RPC após a associação.
    /// </summary>
    public static PlayerControl Criar(Vector2 posicaoInicial, string nome = "NPC", byte corId = 0)
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

            // O IL2CPP desta versão expõe StartCoroutine(string), não
            // StartCoroutine(IEnumerator). Agendamos as etapas pelo Update.
            CriacoesPendentes.Add(new CriacaoPendente
            {
                Falso = falso,
                Posicao = posicaoInicial,
                Nome = nome,
                CorId = corId,
                ExecutarEm = Time.realtimeSinceStartup + 0.1f,
                Etapa = 0
            });

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

    /// <summary>
    /// Processa a criação em etapas temporizadas no thread principal do Unity.
    /// Chamado pelo patch de AmongUsClient.Update.
    /// </summary>
    public static void Atualizar()
    {
        if (CriacoesPendentes.Count == 0)
            return;

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        float agora = Time.realtimeSinceStartup;

        for (int i = CriacoesPendentes.Count - 1; i >= 0; i--)
        {
            CriacaoPendente pendente = CriacoesPendentes[i];
            if (agora < pendente.ExecutarEm)
                continue;

            PlayerControl falso = pendente.Falso;
            if (falso == null || !ObjetosCriados.Contains(falso))
            {
                CriacoesPendentes.RemoveAt(i);
                continue;
            }

            if (pendente.Etapa == 0)
            {
                try
                {
                    int associados = CustomNetObject.AssociarClientes(falso, PlayerIdFalso);
                    LogInfo($"Associação concluída. NetId={falso.NetId}; clientes associados={associados}.");
                    LogDiagnostico("DEPOIS DA ASSOCIACAO", falso, pendente.Nome, pendente.CorId);

                    pendente.Etapa = 1;
                    // Dar mais tempo para os clientes processarem os RPCs de associação
                    // antes de receberem o pacote separado de aparência.
                    pendente.ExecutarEm = agora + 0.5f;
                }
                catch (Exception ex)
                {
                    LogErro("Falha ao associar o personagem falso aos clientes: " + ex);
                    CriacoesPendentes.RemoveAt(i);
                }

                continue;
            }

            try
            {
                CustomNetObject.EnviarAparencia(falso, pendente.Nome, pendente.CorId);
                LogDiagnostico("DEPOIS DE ENVIAR APARENCIA", falso, pendente.Nome, pendente.CorId);

                // Sincroniza o rótulo visual separadamente da identidade de rede.
                // Cada cliente com o mod aplica o texto somente na própria tela.
                NomeNpcRpc.Publicar(falso, pendente.Nome);

                if (!Mover(falso, pendente.Posicao))
                    LogAviso("O personagem foi criado, mas a posição não pôde ser sincronizada.");

                LogDiagnostico("FINAL DA CRIACAO", falso, pendente.Nome, pendente.CorId);
            }
            catch (Exception ex)
            {
                LogErro("Falha ao finalizar a aparência/posição do personagem falso: " + ex);
            }
            finally
            {
                CriacoesPendentes.RemoveAt(i);
            }
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
        CriacoesPendentes.RemoveAll(p => p.Falso == falso);
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

using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// API centralizada para criar, movimentar e destruir PlayerControls artificiais
/// no host, reutilizando o mecanismo de rede existente em CustomNetObject.
/// 
/// IMPORTANTE:
/// - Executar somente no host e durante uma partida iniciada.
/// - Um PlayerControl artificial não é uma conexão real nem um bot completo.
/// - Esta API cuida do objeto/rede; a aparência personalizada deve ser aplicada
///   separadamente e testada na versão exata do Among Us.
/// </summary>
public static class FakePlayerAPI
{
    /// <summary>
    /// ID artificial usado pelo mecanismo atual do YouTubeVoteBridge/EHR.
    /// Não representa um jogador conectado.
    /// </summary>
    public const byte PlayerIdFalso = 254;

    private static readonly HashSet<PlayerControl> ObjetosCriados = new();

    /// <summary>
    /// Cria um PlayerControl artificial e tenta associá-lo aos clientes remotos.
    /// Retorna null se não for possível iniciar a criação.
    /// </summary>
    public static PlayerControl Criar(Vector2 posicaoInicial)
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
                AmongUsClient.Instance.PlayerPrefab,
                Vector2.zero,
                Quaternion.identity);

            falso.PlayerId = PlayerIdFalso;
            falso.isNew = false;
            falso.notRealPlayer = true;

            // Mantém o prefab fora da área visível até os clientes receberem
            // a sequência de spawn/associação.
            try
            {
                falso.NetTransform.SnapTo(new Vector2(50f, 50f));
            }
            catch (Exception ex)
            {
                LogAviso("Não foi possível posicionar o objeto antes do spawn: " + ex.Message);
            }

            // Reutiliza o código de rede que já existe no projeto.
            CustomNetObject.EnviarSpawn(falso);
            ObjetosCriados.Add(falso);

            int associados = CustomNetObject.AssociarClientes(falso, PlayerIdFalso);
            LogInfo($"PlayerControl artificial criado. NetId={falso.NetId}; clientes associados={associados}.");

            // Posiciona após enviar o spawn e a associação.
            Mover(falso, posicaoInicial);
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
    /// Atualiza a posição de um objeto criado por esta API e envia a alteração
    /// aos clientes pelo mecanismo de sincronização existente.
    /// </summary>
    public static bool Mover(PlayerControl falso, Vector2 novaPosicao)
    {
        if (!EhObjetoGerenciado(falso))
        {
            LogAviso("Mover ignorado: o objeto não foi criado pela FakePlayerAPI.");
            return false;
        }

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        try
        {
            CustomNetObject.EnviarPosicao(falso, novaPosicao);
            return true;
        }
        catch (Exception ex)
        {
            LogErro("Falha ao mover PlayerControl artificial: " + ex);
            return false;
        }
    }

    /// <summary>
    /// Remove um objeto artificial criado por esta API.
    /// </summary>
    public static bool Destruir(PlayerControl falso)
    {
        if (!EhObjetoGerenciado(falso))
            return false;

        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        return DestruirInterno(falso, enviarRemocao: true);
    }

    /// <summary>
    /// Destrói todos os objetos artificiais que ainda estiverem registrados.
    /// </summary>
    public static void DestruirTodos()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        var copia = new List<PlayerControl>(ObjetosCriados);
        foreach (PlayerControl falso in copia)
            DestruirInterno(falso, enviarRemocao: true);
    }

    /// <summary>
    /// Informa quantos objetos artificiais esta API mantém registrados localmente.
    /// </summary>
    public static int Quantidade => ObjetosCriados.Count;

    private static bool PodeCriar(out string motivo)
    {
        motivo = null;

        if (AmongUsClient.Instance == null)
        {
            motivo = "AmongUsClient.Instance é null.";
            return false;
        }

        if (!AmongUsClient.Instance.AmHost)
        {
            motivo = "somente o host pode criar objetos artificiais.";
            return false;
        }

        if (PlayerControl.LocalPlayer == null)
        {
            motivo = "PlayerControl.LocalPlayer é null.";
            return false;
        }

        if (AmongUsClient.Instance.PlayerPrefab == null)
        {
            motivo = "PlayerPrefab não foi encontrado.";
            return false;
        }

        if (GameManager.Instance == null || ShipStatus.Instance == null)
        {
            motivo = "a partida ainda não está carregada.";
            return false;
        }

        return true;
    }

    private static bool EhObjetoGerenciado(PlayerControl falso)
    {
        return falso != null && ObjetosCriados.Contains(falso);
    }

    private static bool DestruirInterno(PlayerControl falso, bool enviarRemocao)
    {
        if (falso == null)
        {
            ObjetosCriados.Remove(falso);
            return false;
        }

        bool sucesso = true;

        try
        {
            if (enviarRemocao && AmongUsClient.Instance != null &&
                AmongUsClient.Instance.AmHost)
            {
                // Mensagem de remoção no formato usado pelo CustomNetObject do EHR.
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);

                try
                {
                    writer.StartMessage(5);
                    writer.Write(AmongUsClient.Instance.GameId);

                    writer.StartMessage(5);
                    writer.WritePacked(falso.NetId);
                    writer.EndMessage();

                    writer.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                    writer = null;
                }
                finally
                {
                    writer?.Recycle();
                }
            }
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogErro("Falha ao enviar remoção do PlayerControl artificial: " + ex);
        }

        try
        {
            if (PlayerControl.AllPlayerControls.Contains(falso))
                PlayerControl.AllPlayerControls.Remove(falso);
        }
        catch (Exception ex)
        {
            LogAviso("Falha ao remover objeto de AllPlayerControls: " + ex.Message);
        }

        try
        {
            if (AmongUsClient.Instance != null)
                AmongUsClient.Instance.RemoveNetObject(falso);
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogAviso("Falha ao remover objeto da tabela de rede: " + ex.Message);
        }

        try
        {
            UnityEngine.Object.Destroy(falso.gameObject);
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogAviso("Falha ao destruir GameObject: " + ex.Message);
        }

        ObjetosCriados.Remove(falso);
        LogInfo($"PlayerControl artificial removido. NetId={falso.NetId}.");
        return sucesso;
    }

    private static void LogInfo(string mensagem)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[FakePlayerAPI] " + mensagem);
    }

    private static void LogAviso(string mensagem)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[FakePlayerAPI] " + mensagem);
    }

    private static void LogErro(string mensagem)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[FakePlayerAPI] " + mensagem);
    }
}

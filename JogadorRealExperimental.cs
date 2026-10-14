using System;
using UnityEngine;
using Hazel;

namespace YouTubeVoteBridge;

/// <summary>
/// Experimento isolado para testar se um PlayerControl com PlayerId normal,
/// corpo visível e notRealPlayer=false pode ser apresentado aos clientes vanilla.
/// F9 cria o objeto de teste; F10 tenta removê-lo.
/// IMPORTANTE: isto não cria uma conexão nem um proprietário de rede real.
/// </summary>
internal static class JogadorRealExperimental
{
    private static PlayerControl jogadorTeste;
    private static int etapa;
    private static float executarEm;
    private static Vector2 posicaoAlvo;

    public static void Atualizar()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F9))
                Iniciar();

            if (Input.GetKeyDown(KeyCode.F10))
                Remover();

            if (jogadorTeste == null || etapa == 0 ||
                Time.realtimeSinceStartup < executarEm)
                return;

            if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            {
                LogAviso("A sessão deixou de ser host; cancelando as etapas do teste.");
                etapa = 0;
                return;
            }

            if (etapa == 1)
            {
                int associados = CustomNetObject.AssociarClientes(
                    jogadorTeste, jogadorTeste.PlayerId);

                LogInfo(
                    $"[REAL-EXPERIMENTAL] Associação enviada. NetId={jogadorTeste.NetId}; " +
                    $"PlayerId={jogadorTeste.PlayerId}; clientes={associados}.");

                etapa = 2;
                executarEm = Time.realtimeSinceStartup + 0.75f;
                return;
            }

            if (etapa == 2)
            {
                CustomNetObject.EnviarAparencia(
                    jogadorTeste, "Teste Real", ObterCorDisponivel());

                CustomNetObject.EnviarPosicao(jogadorTeste, posicaoAlvo);

                LogInfo(
                    $"[REAL-EXPERIMENTAL] Teste enviado. NetId={jogadorTeste.NetId}; " +
                    $"DataNetId={jogadorTeste.Data?.NetId}; PlayerId={jogadorTeste.PlayerId}; " +
                    $"OwnerId={jogadorTeste.OwnerId}; isNew={jogadorTeste.isNew}; " +
                    $"notRealPlayer={jogadorTeste.notRealPlayer}; " +
                    $"DataName='{jogadorTeste.Data?.PlayerName}'. " +
                    "Verifique host e cliente vanilla; F10 remove o objeto.");

                etapa = 0;
            }
        }
        catch (Exception ex)
        {
            LogErro("Erro durante o teste: " + ex);
            etapa = 0;
        }
    }

    private static void Iniciar()
    {
        if (jogadorTeste != null)
        {
            LogAviso("Já existe um jogador experimental. Pressione F10 antes de repetir.");
            return;
        }

        if (AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer == null ||
            AmongUsClient.Instance.PlayerPrefab == null ||
            GameManager.Instance == null ||
            ShipStatus.Instance == null)
        {
            LogAviso("F9 exige que o host esteja dentro de uma partida carregada.");
            return;
        }

        byte? idLivre = ObterPlayerIdLivre();
        if (!idLivre.HasValue)
        {
            LogAviso("Não há PlayerId normal livre entre 1 e 14.");
            return;
        }

        PlayerControl criado = null;
        try
        {
            criado = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab,
                Vector2.zero,
                Quaternion.identity);

            criado.PlayerId = idLivre.Value;
            criado.isNew = false;
            criado.notRealPlayer = false;

            // Mantém o objeto visível e na lista local. O spawn de rede ainda
            // usa OwnerId=-2, então o objeto não terá um cliente real como dono.
            criado.NetTransform.SnapTo(new Vector2(50f, 50f));
            CustomNetObject.EnviarSpawn(
                criado, ocultarVisual: false, removerDaLista: false);

            jogadorTeste = criado;
            posicaoAlvo = (Vector2)PlayerControl.LocalPlayer.transform.position +
                new Vector2(1.25f, 0f);
            etapa = 1;
            executarEm = Time.realtimeSinceStartup + 0.35f;

            LogInfo(
                $"[REAL-EXPERIMENTAL] Spawn inicial enviado. NetId={criado.NetId}; " +
                $"DataNetId={criado.Data?.NetId}; PlayerId={criado.PlayerId}; " +
                $"OwnerId={criado.OwnerId}; idNew={criado.isNew}; " +
                $"notRealPlayer={criado.notRealPlayer}. " +
                "Isto é um teste de PlayerControl normal, não um cliente real.");
        }
        catch (Exception ex)
        {
            LogErro("Não foi possível iniciar o spawn experimental: " + ex);
            if (criado != null)
                LimparObjeto(criado, enviarRemocao: true);
            jogadorTeste = null;
            etapa = 0;
        }
    }

    private static byte? ObterPlayerIdLivre()
    {
        for (byte id = 1; id <= 14; id++)
        {
            bool ocupado = false;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            {
                if (p != null && p != jogadorTeste && p.PlayerId == id)
                {
                    ocupado = true;
                    break;
                }
            }

            if (!ocupado)
                return id;
        }

        return null;
    }

    private static byte ObterCorDisponivel()
    {
        // Cor visual de teste. Não modifica a cor dos jogadores reais.
        return 2;
    }

    private static void Remover()
    {
        if (jogadorTeste == null)
        {
            LogAviso("Não há jogador experimental para remover.");
            etapa = 0;
            return;
        }

        PlayerControl alvo = jogadorTeste;
        jogadorTeste = null;
        etapa = 0;

        bool sucesso = LimparObjeto(alvo, enviarRemocao: true);
        LogInfo(
            "[REAL-EXPERIMENTAL] Remoção solicitada. NetId=" + alvo.NetId +
            "; sucesso=" + sucesso + ".");
    }

    private static bool LimparObjeto(PlayerControl alvo, bool enviarRemocao)
    {
        if (alvo == null)
            return false;

        bool sucesso = true;
        uint netId = alvo.NetId;

        try
        {
            if (enviarRemocao && AmongUsClient.Instance != null &&
                AmongUsClient.Instance.AmHost)
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
                finally
                {
                    writer?.Recycle();
                }
            }
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogErro("Falha ao enviar remoção do objeto: " + ex.Message);
        }

        try
        {
            if (PlayerControl.AllPlayerControls.Contains(alvo))
                PlayerControl.AllPlayerControls.Remove(alvo);
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogAviso("Falha ao retirar de AllPlayerControls: " + ex.Message);
        }

        try
        {
            AmongUsClient.Instance?.RemoveNetObject(alvo);
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogAviso("Falha em RemoveNetObject: " + ex.Message);
        }

        try
        {
            UnityEngine.Object.Destroy(alvo.gameObject);
        }
        catch (Exception ex)
        {
            sucesso = false;
            LogAviso("Falha ao destruir o GameObject: " + ex.Message);
        }

        return sucesso;
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[REAL-EXPERIMENTAL] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[REAL-EXPERIMENTAL] " + mensagem);
}

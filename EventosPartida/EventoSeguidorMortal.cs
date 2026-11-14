using System;
using System.Collections.Generic;
using MoreGamemodes;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// NPC artificial que persegue o jogador vivo mais próximo.
/// A movimentação usa o Rigidbody2D do CustomNetworkTransform, igual ao
/// transporte contínuo já usado pelo evento Tsunami. SnapTo é usado apenas
/// no posicionamento inicial.
/// </summary>
internal sealed class EventoSeguidorMortal : EventoVotacao
{
    private const byte PlayerIdSeguidor = 249;
    private const float Velocidade = 1.75f;
    private const float AlcanceMorte = 0.48f;
    private const float IntervaloTrocaAlvo = 0.25f;

    private static bool armado;
    private static bool ativo;
    private static int etapaInicializacao;
    private static float proximaEtapa;
    private static float proximaBuscaAlvo;
    private static PlayerControl seguidor;
    private static PlayerControl alvoAtual;

    public override string Nome => "NPC seguidor mortal";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        armado = true;

        if (ShipStatus.Instance != null && LobbyTagManager.PartidaIniciada())
            IniciarPartida();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Seguidor] Evento armado para o início da partida.");
    }

    internal static void IniciarPartida()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !armado ||
            ShipStatus.Instance == null ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        if (ativo || seguidor != null)
            return;

        PlayerControl primeiroAlvo = ObterJogadorVivoMaisProximo(
            (Vector2)PlayerControl.LocalPlayer.transform.position);

        if (primeiroAlvo == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Não há jogadores vivos para o NPC perseguir.");
            return;
        }

        try
        {
            // Nasce deslocado do alvo para não começar sobre o jogador.
            Vector2 posicaoInicial = primeiroAlvo.GetTruePosition() + new Vector2(2.0f, 1.5f);

            seguidor = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab,
                posicaoInicial,
                Quaternion.identity);

            seguidor.PlayerId = PlayerIdSeguidor;
            seguidor.isNew = false;
            seguidor.notRealPlayer = true;

            // SnapTo somente para definir a posição inicial antes do Spawn.
            seguidor.NetTransform.SnapTo(posicaoInicial);

            var outfit = seguidor.Data?.Outfits[PlayerOutfitType.Default];
            if (outfit != null)
            {
                outfit.PlayerName = "SEGUIDOR";
                outfit.ColorId = 0;
                outfit.HatId = string.Empty;
                outfit.SkinId = string.Empty;
                outfit.PetId = string.Empty;
                outfit.VisorId = string.Empty;
            }

            try
            {
                if (seguidor.NetTransform?.body != null)
                    seguidor.NetTransform.body.velocity = Vector2.zero;
            }
            catch { }

            etapaInicializacao = 1;
            proximaEtapa = Time.realtimeSinceStartup + 0.12f;
            proximaBuscaAlvo = 0f;
            alvoAtual = null;
            ativo = true;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC criado localmente. PlayerId=" +
                PlayerIdSeguidor + ", posição inicial=" + posicaoInicial + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Falha ao criar o NPC: " + ex);
            Resetar();
        }
    }

    internal static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Reunião iniciada; removendo o NPC.");
            Resetar();
            return;
        }

        if (seguidor == null || seguidor.NetTransform == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] NPC ou CustomNetworkTransform indisponível; encerrando evento.");
            Resetar();
            return;
        }

        // Etapa 1: publicar o Spawn do NPC.
        if (etapaInicializacao == 1 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                CustomNetObject.EnviarSpawn(seguidor, ocultarVisual: false);
                etapaInicializacao = 2;
                proximaEtapa = Time.realtimeSinceStartup + 0.12f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Spawn enviado; aguardando associação aos clientes.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha ao enviar Spawn: " + ex);
                Resetar();
                return;
            }
        }

        // Etapa 2: associar o PlayerId reservado ao objeto de rede.
        if (etapaInicializacao == 2 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                int clientes = CustomNetObject.AssociarClientes(
                    seguidor, PlayerIdSeguidor);

                if (PlayerControl.LocalPlayer?.Data != null)
                    seguidor.CachedPlayerData = PlayerControl.LocalPlayer.Data;

                etapaInicializacao = 3;
                proximaEtapa = Time.realtimeSinceStartup + 0.15f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC associado. Clientes processados=" + clientes + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha ao associar o NPC: " + ex);
                Resetar();
                return;
            }
        }

        if (etapaInicializacao < 3)
            return;

        if (seguidor.NetTransform.body == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Rigidbody2D do CustomNetworkTransform não foi encontrado.");
            Resetar();
            return;
        }

        // O Tsunami já usa este mesmo caminho: velocidade no Rigidbody2D
        // pertencente ao CustomNetworkTransform. O componente de rede transmite
        // posição e velocidade; não enviamos SnapTo durante a perseguição.
        PlayerControl alvo = ObterAlvoAtual();
        if (alvo == null)
        {
            seguidor.NetTransform.body.velocity = Vector2.zero;
            return;
        }

        Vector2 origem = seguidor.GetTruePosition();
        Vector2 destino = alvo.GetTruePosition();
        Vector2 diferenca = destino - origem;
        float distancia = diferenca.magnitude;

        if (distancia <= AlcanceMorte)
        {
            seguidor.NetTransform.body.velocity = Vector2.zero;
            MatarAlvo(alvo);
            alvoAtual = null;
            proximaBuscaAlvo = 0f;
            return;
        }

        Vector2 direcao = diferenca / distancia;
        seguidor.NetTransform.body.velocity = direcao * Velocidade;
    }

    private static PlayerControl ObterAlvoAtual()
    {
        if (alvoAtual != null &&
            alvoAtual.Data != null &&
            !alvoAtual.Data.IsDead &&
            !alvoAtual.Data.Disconnected)
        {
            if (Time.realtimeSinceStartup < proximaBuscaAlvo)
                return alvoAtual;
        }

        proximaBuscaAlvo = Time.realtimeSinceStartup + IntervaloTrocaAlvo;
        alvoAtual = ObterJogadorVivoMaisProximo(seguidor.GetTruePosition());
        return alvoAtual;
    }

    private static PlayerControl ObterJogadorVivoMaisProximo(Vector2 origem)
    {
        PlayerControl melhor = null;
        float menorDistancia = float.MaxValue;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == seguidor ||
                jogador.Data == null ||
                jogador.Data.IsDead ||
                jogador.Data.Disconnected)
                continue;

            float distancia = Vector2.Distance(origem, jogador.GetTruePosition());
            if (distancia < menorDistancia)
            {
                menorDistancia = distancia;
                melhor = jogador;
            }
        }

        return melhor;
    }

    private static void MatarAlvo(PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            alvo == null ||
            alvo.Data == null ||
            alvo.Data.IsDead ||
            alvo.Data.Disconnected ||
            PlayerControl.LocalPlayer == null)
            return;

        try
        {
            // O host executa o assassinato para usar o fluxo vanilla de morte.
            // O NPC é apenas o agente que determina o contato e o momento.
            PlayerControl.LocalPlayer.RpcMurderPlayer(alvo, true);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] O NPC alcançou " + alvo.Data.PlayerName +
                " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Falha ao matar o alvo: " + ex);
        }
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        etapaInicializacao = 0;
        proximaEtapa = 0f;
        proximaBuscaAlvo = 0f;
        alvoAtual = null;

        if (seguidor != null)
        {
            try
            {
                if (seguidor.NetTransform?.body != null)
                    seguidor.NetTransform.body.velocity = Vector2.zero;
            }
            catch { }

            try
            {
                CustomNetObject.Despawn(seguidor);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Falha ao enviar Despawn: " + ex.Message);
                try { UnityEngine.Object.Destroy(seguidor.gameObject); } catch { }
            }
        }

        seguidor = null;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Seguidor] Estado reiniciado.");
    }
}

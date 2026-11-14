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
    private const string NomeSeguidor = "<color=red>NPC ASSASINO</color>";

    private static bool armado;
    private static bool ativo;
    private static bool aguardandoFimRoleReveal;
    private static int etapaInicializacao;
    private static float proximaEtapa;
    private static float proximaBuscaAlvo;
    private static PlayerControl seguidor;
    private static PlayerControl alvoAtual;

    // Evita repetir chamadas de transporte enquanto a animação vanilla está ativa.
    private static bool transporteEmAndamento;
    private static float limiteTransporte;
    private static float proximoTransporte;

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

        // ShipStatus.Start ocorre antes do fim da Role Reveal.
        // Aqui apenas armamos o evento; o NPC será criado no OnDestroy da IntroCutscene.
        ativo = true;
        aguardandoFimRoleReveal = true;
        etapaInicializacao = 0;
        proximaEtapa = float.MaxValue;
        alvoAtual = null;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Seguidor] Evento preparado; aguardando o fim da Role Reveal.");
    }

    internal static void AoSairDaRoleReveal()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !armado ||
            !ativo ||
            !aguardandoFimRoleReveal ||
            ShipStatus.Instance == null ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        aguardandoFimRoleReveal = false;

        PlayerControl primeiroAlvo = ObterJogadorVivoMaisProximo(
            (Vector2)PlayerControl.LocalPlayer.transform.position);

        if (primeiroAlvo == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Role Reveal terminou, mas não há jogadores vivos para posicionar o NPC.");
            Resetar();
            return;
        }

        try
        {
            Vector2 posicaoInicial = primeiroAlvo.GetTruePosition() + new Vector2(2.0f, 1.5f);

            seguidor = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab,
                posicaoInicial,
                Quaternion.identity);

            seguidor.PlayerId = PlayerIdSeguidor;
            seguidor.isNew = false;
            seguidor.notRealPlayer = true;
            seguidor.NetTransform.SnapTo(posicaoInicial);

            var outfit = seguidor.Data?.Outfits[PlayerOutfitType.Default];
            if (outfit != null)
            {
                outfit.PlayerName = NomeSeguidor;
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

            // Mesmo fluxo em etapas do Raio/Tsunami:
            // criar localmente -> Spawn -> associar NetId/PlayerId -> aparência.
            etapaInicializacao = 1;
            proximaEtapa = Time.realtimeSinceStartup + 0.12f;
            proximaBuscaAlvo = 0f;
            proximoTransporte = 0f;
            transporteEmAndamento = false;
            alvoAtual = null;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Role Reveal encerrada. NPC ASSASINO criado; iniciando sincronização. PlayerId=" +
                PlayerIdSeguidor + ", posição inicial=" + posicaoInicial + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Falha ao criar NPC após a Role Reveal: " + ex);
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

        if (aguardandoFimRoleReveal || seguidor == null)
            return;

        if (seguidor.NetTransform == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] CustomNetworkTransform indisponível; encerrando evento.");
            Resetar();
            return;
        }

        // Etapa 1: publicar o Spawn pelo mesmo fluxo de rede do Raio/Tsunami.
        if (etapaInicializacao == 1 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                CustomNetObject.EnviarSpawn(seguidor, ocultarVisual: false);
                if (PlayerControl.AllPlayerControls.Contains(seguidor))
                    PlayerControl.AllPlayerControls.Remove(seguidor);

                etapaInicializacao = 2;
                proximaEtapa = Time.realtimeSinceStartup + 0.12f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Spawn enviado; aguardando associação do NPC aos clientes.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha ao enviar Spawn: " + ex);
                Resetar();
                return;
            }
        }

        // Etapa 2: associar NetId e PlayerId em cada cliente, como no Raio.
        if (etapaInicializacao == 2 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                int clientes = CustomNetObject.AssociarClientes(seguidor, PlayerIdSeguidor);

                if (PlayerControl.LocalPlayer?.Data != null)
                    seguidor.CachedPlayerData = PlayerControl.LocalPlayer.Data;

                etapaInicializacao = 3;
                proximaEtapa = Time.realtimeSinceStartup + 0.15f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Associação concluída. Clientes processados=" + clientes + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha ao associar o NPC: " + ex);
                Resetar();
                return;
            }
        }

        // Etapa 3: publicar o nome formatado e a aparência do NPC.
        if (etapaInicializacao == 3 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                CustomNetObject.EnviarAparencia(seguidor, NomeSeguidor, 0);
                etapaInicializacao = 4;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Aparência publicada: " + NomeSeguidor);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha ao publicar a aparência do NPC: " + ex);
                Resetar();
                return;
            }
        }

        if (etapaInicializacao < 4)
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

        // Antes de perseguir em linha reta, procura uma travessia vanilla
        // que aproxime o NPC do alvo: escadas e tirolesas do Fungle.
        if (TentarUsarEscadaOuTirolesa(alvo))
            return;

        Vector2 direcao = diferenca / distancia;
        seguidor.NetTransform.body.velocity = direcao * Velocidade;
    }

    /// <summary>
    /// Usa os mecanismos de transporte nativos. A seleção é geométrica:
    /// só considera uma travessia quando o destino dela reduz claramente
    /// a distância até o jogador perseguido.
    /// </summary>
    private static bool TentarUsarEscadaOuTirolesa(PlayerControl alvo)
    {
        if (seguidor == null || seguidor.MyPhysics == null ||
            seguidor.NetTransform?.body == null || alvo == null)
            return false;

        float agora = Time.realtimeSinceStartup;

        if (transporteEmAndamento)
        {
            // Os RPCs vanilla controlam a animação e o deslocamento.
            // Não aplicamos velocidade enquanto o transporte estiver ativo.
            if (seguidor.onLadder || seguidor.inMovingPlat || !seguidor.moveable)
            {
                if (agora < limiteTransporte)
                {
                    seguidor.NetTransform.body.velocity = Vector2.zero;
                    return true;
                }

                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Transporte excedeu o tempo limite; retomando perseguição.");
            }

            transporteEmAndamento = false;
        }

        if (agora < proximoTransporte)
            return false;

        Vector2 origem = seguidor.GetTruePosition();
        Vector2 posAlvo = alvo.GetTruePosition();
        float distanciaDireta = Vector2.Distance(origem, posAlvo);
        float melhorCusto = float.MaxValue;
        Ladder melhorEscada = null;
        ZiplineConsole melhorTirolesa = null;

        // Escadas: cada Ladder aponta para a outra extremidade por Destination.
        Ladder[] escadas = ShipStatus.Instance.GetComponentsInChildren<Ladder>();
        foreach (Ladder escada in escadas)
        {
            if (escada == null || !escada.isActiveAndEnabled ||
                escada.Destination == null || !escada.Destination.isActiveAndEnabled)
                continue;

            Vector2 entrada = escada.transform.position;
            Vector2 saida = escada.Destination.transform.position;
            float custo = Vector2.Distance(origem, entrada) +
                          Vector2.Distance(saida, posAlvo);

            if (Vector2.Distance(saida, posAlvo) >= distanciaDireta - 0.75f ||
                custo >= distanciaDireta - 0.25f ||
                custo >= melhorCusto)
                continue;

            melhorCusto = custo;
            melhorEscada = escada;
            melhorTirolesa = null;
        }

        // Tirolesas: o console define a direção e o ponto de desembarque.
        ZiplineConsole[] consoles = ShipStatus.Instance.GetComponentsInChildren<ZiplineConsole>();
        foreach (ZiplineConsole console in consoles)
        {
            if (console == null || !console.isActiveAndEnabled ||
                console.zipline == null || !console.zipline.isActiveAndEnabled ||
                console.destination == null || !console.destination.isActiveAndEnabled)
                continue;

            Vector3 pontoPouso = console.atTop
                ? console.zipline.landingPositionBottom.position
                : console.zipline.landingPositionTop.position;
            Vector2 saida = console.zipline.transform.TransformPoint(pontoPouso);
            Vector2 entrada = console.transform.position;
            float custo = Vector2.Distance(origem, entrada) +
                          Vector2.Distance(saida, posAlvo);

            if (Vector2.Distance(saida, posAlvo) >= distanciaDireta - 0.75f ||
                custo >= distanciaDireta - 0.25f ||
                custo >= melhorCusto)
                continue;

            melhorCusto = custo;
            melhorTirolesa = console;
            melhorEscada = null;
        }

        if (melhorEscada == null && melhorTirolesa == null)
            return false;

        Vector2 pontoEntrada = melhorEscada != null
            ? (Vector2)melhorEscada.transform.position
            : (Vector2)melhorTirolesa.transform.position;
        float alcance = melhorEscada != null
            ? melhorEscada.UsableDistance
            : melhorTirolesa.UsableDistance;

        float distanciaEntrada = Vector2.Distance(origem, pontoEntrada);
        if (distanciaEntrada > alcance)
        {
            Vector2 direcaoEntrada = (pontoEntrada - origem).normalized;
            seguidor.NetTransform.body.velocity = direcaoEntrada * Velocidade;
            return true;
        }

        seguidor.NetTransform.body.velocity = Vector2.zero;

        try
        {
            if (melhorEscada != null)
            {
                melhorEscada.CanUse(seguidor.Data, out bool permitidoEscada, out _);
                if (!permitidoEscada || melhorEscada.IsCoolingDown())
                {
                    proximoTransporte = agora + 0.5f;
                    return false;
                }

                seguidor.MyPhysics.RpcClimbLadder(melhorEscada);
                transporteEmAndamento = true;
                limiteTransporte = agora + 8f;
                proximoTransporte = agora + 1f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC iniciou travessia de escada.");
                return true;
            }

            melhorTirolesa.CanUse(seguidor.Data, out bool permitidoTirolesa, out _);
            if (!permitidoTirolesa || melhorTirolesa.IsCoolingDown())
            {
                proximoTransporte = agora + 0.5f;
                return false;
            }

            seguidor.CmdCheckUseZipline(
                seguidor, melhorTirolesa.zipline, melhorTirolesa.atTop);
            transporteEmAndamento = true;
            limiteTransporte = agora + 12f;
            proximoTransporte = agora + 1f;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC iniciou travessia de tirolesa.");
            return true;
        }
        catch (Exception ex)
        {
            transporteEmAndamento = false;
            proximoTransporte = agora + 1f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Não foi possível iniciar transporte: " + ex.Message);
            return false;
        }
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
        aguardandoFimRoleReveal = false;
        etapaInicializacao = 0;
        proximaEtapa = 0f;
        proximaBuscaAlvo = 0f;
        alvoAtual = null;
        transporteEmAndamento = false;
        limiteTransporte = 0f;
        proximoTransporte = 0f;

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

using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using MoreGamemodes;
using AmongUs.GameOptions;
using UnityEngine;
using Hazel;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// NPC artificial que persegue o jogador vivo mais próximo.
/// A movimentação usa o Rigidbody2D do CustomNetworkTransform, igual ao
/// transporte contínuo já usado pelo evento Tsunami. SnapTo também é enviado
/// periodicamente para manter os clientes remotos próximos da posição real.
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

    // Estado recebido pelos clientes com o mod para reproduzir a perseguição localmente.
    private static uint netIdSeguidorCliente;
    private static byte alvoReplicadoCliente = byte.MaxValue;
    private static Vector2 destinoReplicadoCliente;
    private static bool destinoReplicadoRecebido;
    private static byte ultimoAlvoNotificado = byte.MaxValue;
    private static bool alvoInicialNotificado;

    // Recuperação de vida do NPC usando o registro reservado no GameData.
    private static float proximaVerificacaoVidaNpc;
    private static float restauracaoNpcAgendadaPara;
    private static bool restauracaoNpcPendente;
    private static bool diagnosticoRegistroNpcFeito;
    private static bool avisoRegistroNpcAusente;
    private static bool avisoRegistroNpcCompartilhado;

    // Patrulha usada quando nenhum jogador válido está disponível como alvo.
    private const float DistanciaChegadaPatrulha = 0.22f;
    private const float DuracaoDestinoPatrulha = 14f;
    private static bool possuiDestinoPatrulha;
    private static Vector2 destinoPatrulha;
    private static float limiteDestinoPatrulha;
    private static float proximaTentativaDestinoPatrulha;

    // Evita repetir chamadas de transporte enquanto a animação vanilla está ativa.
    private static bool transporteEmAndamento;
    private static float limiteTransporte;
    private static float proximoTransporte;
    private const float IntervaloSnapToRede = 1f;
    private static float proximoSnapToRede;
    private static ushort sequenciaSnapToRede;
    private static bool sequenciaSnapToRedeInicializada;
    private static Vent ventEntradaAtual;
    private static Vent ventSaidaAtual;
    private static bool aguardandoSaidaVent;
    private static float proximaTentativaVent;

    // Caminho A*: recalculado periodicamente e quando o alvo muda de posição.
    private const float TamanhoCelula = 0.35f;
    // Fallback usado somente antes de o prefab do NPC disponibilizar seu collider.
    // Durante a navegação, as consultas usam o formato e as dimensões do collider real.
    private const float RaioColisaoFallback = 0.15f;
    private static Collider2D colliderFisicoNpc;
    private const float IntervaloRecalculoCaminho = 0.65f;
    private const float DistanciaRecalculoAlvo = 0.65f;
    // A busca usa os 8 vizinhos do grid. A suavização posterior remove
    // pontos intermediários quando a linha está livre, permitindo trajetórias
    // em qualquer ângulo (não apenas nas direções fixas do grid).
    private static readonly Vector2Int[] DirecoesCaminho =
    {
        new Vector2Int(1, 0),
        new Vector2Int(1, 1),
        new Vector2Int(0, 1),
        new Vector2Int(-1, 1),
        new Vector2Int(-1, 0),
        new Vector2Int(-1, -1),
        new Vector2Int(0, -1),
        new Vector2Int(1, -1)
    };
    private static List<Vector2> caminhoAtual = new();
    private static int indiceCaminho;
    private static Vector2 destinoCaminhoAnterior;
    private static float proximoRecalculoCaminho;
    private static bool mostrarCaminhoNpc;
    private static LineRenderer renderizadorCaminhoNpc;
    private static Material materialCaminhoNpc;

    // Detector de travamento: observa o deslocamento real enquanto há
    // movimento solicitado e procura uma saída lateral quando o NPC fica preso.
    private static Vector2 ultimaPosicaoMonitorada;
    private static float proximaVerificacaoTravamento;
    private static bool monitoramentoMovimentoIniciado;
    private static float inicioSemDeslocamento;
    private static bool avisoMonitoramentoMovimento;
    private const float TempoParaConsiderarTravado = 2.0f;
    private const float DeslocamentoMinimoMonitoramento = 0.06f;
    private static bool emManobraDestravamento;
    private static Vector2 pontoDestravamento;
    private static float limiteManobraDestravamento;

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

        if (!TentarObterCentroSalaInicial(out Vector2 posicaoInicial, out SystemTypes salaInicial))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Não foi possível localizar a sala de spawn configurada para este mapa.");
            Resetar();
            return;
        }

        try
        {
            seguidor = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab,
                posicaoInicial,
                Quaternion.identity);

            seguidor.PlayerId = PlayerIdSeguidor;
            colliderFisicoNpc = null;
            seguidor.isNew = false;
            seguidor.notRealPlayer = true;
            seguidor.NetTransform.SnapTo(posicaoInicial);

            // A aparência será aplicada somente depois que o NPC existir na rede
            // e estiver associado aos clientes, evitando configurar cosméticos
            // antes da inicialização do NetworkedPlayerInfo.

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
            proximoSnapToRede = 0f;
            sequenciaSnapToRede = 0;
            sequenciaSnapToRedeInicializada = false;
            transporteEmAndamento = false;
            alvoAtual = null;
            LimparCaminho();

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

    // Define a sala de spawn por mapa e usa o centro geométrico do collider
    // da sala, em vez de posicionar o NPC perto de um jogador.
    private static bool TentarObterCentroSalaInicial(out Vector2 centro, out SystemTypes salaDesejada)
    {
        centro = Vector2.zero;
        salaDesejada = SystemTypes.Hallway;

        if (ShipStatus.Instance == null || GameOptionsManager.Instance?.CurrentGameOptions == null)
            return false;

        int mapa = GameOptionsManager.Instance.CurrentGameOptions.MapId;
        salaDesejada = mapa switch
        {
            0 => SystemTypes.Shields,       // The Skeld
            1 => SystemTypes.Greenhouse,    // MIRA HQ - Estufa
            2 => SystemTypes.Security,       // Polus - Segurança
            4 => SystemTypes.Cockpit,        // Airship - Cabine (Cockpit)
            5 => SystemTypes.Laboratory,    // The Fungle
            _ => SystemTypes.Hallway
        };

        if (mapa != 0 && mapa != 1 && mapa != 2 && mapa != 4 && mapa != 5)
            return false;

        foreach (var sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || sala.roomArea == null || sala.RoomId != salaDesejada)
                continue;

            Bounds limitesSala = sala.roomArea.bounds;
            Vector2 centroSala = limitesSala.center;
            if (!TentarEncontrarPontoLivreNaSala(limitesSala, centroSala, out centro))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] A sala " + salaDesejada +
                    " foi encontrada, mas não há ponto livre para o spawn dentro dela.");
                return false;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Spawn definido em ponto livre da sala " + salaDesejada +
                " (mapa " + mapa + "): " + centro +
                ". Centro geométrico da sala: " + centroSala + ".");
            return true;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "[F7/Seguidor] Sala " + salaDesejada + " não encontrada em AllRooms no mapa " + mapa + ".");
        return false;
    }

    // Procura uma posição livre dentro da sala. O centro geométrico pode
    // coincidir com mesas, consoles, vents ou outros objetos sólidos.
    private static bool TentarEncontrarPontoLivreNaSala(
        Bounds limitesSala, Vector2 centroSala, out Vector2 pontoLivre)
    {
        pontoLivre = centroSala;

        float margem = RaioColisaoFallback + 0.12f;
        float passo = 0.30f;
        float menorDistancia = float.MaxValue;
        bool encontrou = false;

        // Varre a área em ordem de proximidade do centro, priorizando
        // posições próximas sem colocar o NPC sobre objetos sólidos.
        for (float y = limitesSala.min.y + margem;
             y <= limitesSala.max.y - margem;
             y += passo)
        {
            for (float x = limitesSala.min.x + margem;
                 x <= limitesSala.max.x - margem;
                 x += passo)
            {
                Vector2 candidato = new Vector2(x, y);
                float distancia = Vector2.Distance(candidato, centroSala);
                if (distancia >= menorDistancia)
                    continue;

                Collider2D[] colisoes = ObterColisoesNaPosicao(
                    candidato, Constants.ShipAndAllObjectsMask);

                bool bloqueado = false;
                foreach (Collider2D colisor in colisoes)
                {
                    if (colisor == null || colisor.isTrigger)
                        continue;

                    bloqueado = true;
                    break;
                }

                if (bloqueado)
                    continue;

                menorDistancia = distancia;
                pontoLivre = candidato;
                encontrou = true;
            }
        }

        return encontrou;
    }

    private static bool diagnosticoApisMovimentoExecutado;

    private static void DiagnosticarApisMovimento()
    {
        if (diagnosticoApisMovimentoExecutado || seguidor == null)
            return;

        diagnosticoApisMovimentoExecutado = true;

        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        var objetos = new[]
        {
            new { Nome = "PlayerPhysics", Instancia = (object)seguidor.MyPhysics },
            new { Nome = "CustomNetworkTransform", Instancia = (object)seguidor.NetTransform }
        };

        foreach (var item in objetos)
        {
            if (item.Instancia == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Diagnóstico de movimento: " + item.Nome + " está nulo.");
                continue;
            }

            Type tipo = item.Instancia.GetType();
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Inspecionando APIs de movimento de " + item.Nome +
                ": " + tipo.FullName + ".");

            int metodosEncontrados = 0;
            foreach (var metodo in tipo.GetMethods(flags))
            {
                string nome = metodo.Name;
                if (nome.IndexOf("Position", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nome.IndexOf("Move", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nome.IndexOf("SnapTo", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nome.IndexOf("FixedUpdate", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string parametros = string.Join(", ", Array.ConvertAll(
                    metodo.GetParameters(), p => p.ParameterType.FullName));

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] API MOVIMENTO: " + item.Nome + "." +
                    nome + "(" + parametros + ") -> " + metodo.ReturnType.FullName + ".");
                metodosEncontrados++;
            }

            if (metodosEncontrados == 0)
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Nenhum método relacionado a Position/Move/SnapTo/FixedUpdate encontrado em " +
                    item.Nome + ".");

            int membrosEncontrados = 0;
            foreach (var membro in tipo.GetMembers(flags))
            {
                string nome = membro.Name;
                if (nome.IndexOf("Position", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nome.IndexOf("Velocity", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nome.IndexOf("Sequence", StringComparison.OrdinalIgnoreCase) < 0 &&
                    nome.IndexOf("Sync", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (!(membro is System.Reflection.FieldInfo) &&
                    !(membro is System.Reflection.PropertyInfo))
                    continue;

                Type tipoMembro = membro is System.Reflection.FieldInfo campo
                    ? campo.FieldType
                    : ((System.Reflection.PropertyInfo)membro).PropertyType;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] MEMBRO DE SINCRONIZAÇÃO: " + item.Nome + "." +
                    nome + " : " + tipoMembro.FullName + ".");
                membrosEncontrados++;
            }

            if (membrosEncontrados == 0)
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Nenhum campo/propriedade Position/Velocity/Sequence/Sync encontrado em " +
                    item.Nome + ".");
        }
    }

    // Clientes vanilla recebem somente uma correção de posição por segundo.
    // Clientes com o mod ficam fora deste RPC e reproduzem a perseguição localmente.
    private static void EnviarSnapToRedePeriodico()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            seguidor == null ||
            seguidor.NetTransform == null ||
            AmongUsClient.Instance == null ||
            transporteEmAndamento ||
            aguardandoSaidaVent)
            return;

        float agora = Time.realtimeSinceStartup;
        if (agora < proximoSnapToRede)
            return;

        proximoSnapToRede = agora + IntervaloSnapToRede;

        try
        {
            var transformacao = seguidor.NetTransform;
            Vector2 posicao = seguidor.GetTruePosition();

            // A sequência é mantida separadamente: não chamamos SnapTo nem
            // SetDirtyBit no transform do host, pois isso interfere no movimento local.
            if (!sequenciaSnapToRedeInicializada)
            {
                sequenciaSnapToRede = (ushort)(transformacao.lastSequenceId + 8);
                sequenciaSnapToRedeInicializada = true;
            }
            else
            {
                sequenciaSnapToRede = (ushort)(sequenciaSnapToRede + 8);
            }

            ushort sequenciaRede = sequenciaSnapToRede;

            int enviados = 0;
            foreach (var cliente in AmongUsClient.Instance.allClients.ToArray())
            {
                if (cliente == null ||
                    cliente.Id == AmongUsClient.Instance.ClientId ||
                    cliente.Id < 0 ||
                    YouTubeVoteBridgePlugin.ClienteTemMod(cliente.Id))
                    continue;

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    transformacao.NetId,
                    (byte)RpcCalls.SnapTo,
                    SendOption.Reliable,
                    cliente.Id);

                writer.Write(posicao.x);
                writer.Write(posicao.y);
                writer.Write(sequenciaRede);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                enviados++;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                $"[F7/Seguidor] SnapTo enviado a {enviados} cliente(s) vanilla: posição={posicao}, sequência={sequenciaRede}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao enviar SnapTo periódico: " + ex.Message);
        }
    }

    // O host informa aos clientes com o mod qual jogador está sendo perseguido.
    // O PlayerId permite que cada cliente acompanhe a posição atual desse jogador,
    // sem precisar receber uma coordenada nova a cada frame.
    private static void EnviarDestinoAosClientesComMod(PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            seguidor == null ||
            PlayerControl.LocalPlayer == null ||
            AmongUsClient.Instance == null)
            return;

        byte playerIdAlvo = alvo != null ? alvo.PlayerId : byte.MaxValue;
        Vector2 destino = alvo != null ? alvo.GetTruePosition() : seguidor.GetTruePosition();

        try
        {
            int enviados = 0;
            foreach (int clientId in ObterClientesComModConectados())
            {
                if (clientId == AmongUsClient.Instance.ClientId || clientId < 0)
                    continue;

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId,
                    246,
                    SendOption.Reliable,
                    clientId);

                writer.WritePacked(seguidor.NetId);
                writer.Write(playerIdAlvo);
                writer.Write(destino.x);
                writer.Write(destino.y);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
                enviados++;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Seguidor] Destino enviado por RPC aos clientes com mod: alvo={playerIdAlvo}, posição={destino}, clientes={enviados}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao enviar destino aos clientes com mod: " + ex.Message);
        }
    }

    private static int[] ObterClientesComModConectados()
    {
        // A lista é mantida pelo sinal RPC 248. O filtro de conexão evita
        // tentar enviar para IDs antigos que já saíram da sala.
        var conectados = new HashSet<int>();
        foreach (var cliente in AmongUsClient.Instance.allClients.ToArray())
        {
            if (cliente != null && cliente.Id >= 0 &&
                YouTubeVoteBridgePlugin.ClienteTemMod(cliente.Id))
                conectados.Add(cliente.Id);
        }
        return conectados.ToArray();
    }

    // Chamado no Update de cada cliente com o mod. O NPC usa a posição replicada
    // do alvo para caminhar localmente, enquanto o host continua sendo a autoridade
    // para mortes, seleção de alvo e transportes especiais.
    internal static void ReceberDestinoCliente(
        uint netIdNpc, byte playerIdAlvo, Vector2 destino)
    {
        netIdSeguidorCliente = netIdNpc;
        alvoReplicadoCliente = playerIdAlvo;
        destinoReplicadoCliente = destino;
        destinoReplicadoRecebido = true;
    }

    internal static void AtualizarCliente()
    {
        if (YouTubeVoteBridgePlugin.EhHost() ||
            !destinoReplicadoRecebido ||
            AmongUsClient.Instance == null)
            return;

        if (MeetingHud.Instance != null || ShipStatus.Instance == null)
        {
            PararMovimentoCliente();
            return;
        }

        PlayerControl npc = null;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.NetId == netIdSeguidorCliente)
            {
                npc = jogador;
                break;
            }
        }

        if (npc == null || npc.NetTransform?.body == null)
            return;

        if (seguidor != npc)
        {
            seguidor = npc;
            LimparCaminho();
            monitoramentoMovimentoIniciado = false;
            emManobraDestravamento = false;
        }

        // Escadas, tirolesas e outros transportes continuam sendo controlados
        // pelos RPCs nativos recebidos do host.
        if (!npc.moveable || npc.onLadder || npc.inMovingPlat)
        {
            npc.NetTransform.body.velocity = Vector2.zero;
            return;
        }

        if (alvoReplicadoCliente == byte.MaxValue)
        {
            npc.NetTransform.body.velocity = Vector2.zero;
            LimparCaminho();
            return;
        }

        PlayerControl alvo = null;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador != npc &&
                jogador.PlayerId == alvoReplicadoCliente &&
                jogador.Data != null && !jogador.Data.IsDead &&
                !jogador.Data.Disconnected)
            {
                alvo = jogador;
                break;
            }
        }

        Vector2 destino = alvo != null
            ? alvo.GetTruePosition()
            : destinoReplicadoCliente;
        Vector2 diferenca = destino - npc.GetTruePosition();

        if (diferenca.magnitude <= AlcanceMorte)
        {
            npc.NetTransform.body.velocity = Vector2.zero;
            LimparCaminho();
        }
        else
        {
            // Reutiliza o A* do host no cliente para evitar que o NPC tente
            // atravessar paredes ao perseguir um alvo que mudou de posição.
            MoverAte(destino);
        }
    }

    internal static void PararMovimentoCliente()
    {
        if (YouTubeVoteBridgePlugin.EhHost())
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.NetId == netIdSeguidorCliente &&
                jogador.NetTransform?.body != null)
            {
                jogador.NetTransform.body.velocity = Vector2.zero;
                break;
            }
        }

        destinoReplicadoRecebido = false;
        netIdSeguidorCliente = 0;
        alvoReplicadoCliente = byte.MaxValue;
    }

    private static void AtualizarRegistroVidaNpc()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            seguidor == null ||
            GameData.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        float agora = Time.realtimeSinceStartup;
        if (agora < proximaVerificacaoVidaNpc && !restauracaoNpcPendente)
            return;

        proximaVerificacaoVidaNpc = agora + 0.5f;

        try
        {
            // O PlayerId reservado precisa apontar para um NetworkedPlayerInfo
            // próprio. Nunca alteramos os dados do host caso o ID 249 não exista
            // ou esteja retornando a mesma referência do jogador local.
            var dadosNpc = GameData.Instance.GetPlayerById(PlayerIdSeguidor);
            var dadosLocal = PlayerControl.LocalPlayer.Data;

            if (dadosNpc == null)
            {
                if (!avisoRegistroNpcAusente)
                {
                    avisoRegistroNpcAusente = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] GameData ainda não possui um registro próprio para PlayerId=249. A restauração foi ignorada para não afetar o host.");
                }

                restauracaoNpcPendente = false;
                return;
            }

            if (ReferenceEquals(dadosNpc, dadosLocal))
            {
                if (!avisoRegistroNpcCompartilhado)
                {
                    avisoRegistroNpcCompartilhado = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] GetPlayerById(249) retornou os mesmos dados do jogador local. IsDead não será alterado para evitar reviver o host.");
                }

                restauracaoNpcPendente = false;
                return;
            }

            if (!diagnosticoRegistroNpcFeito)
            {
                diagnosticoRegistroNpcFeito = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Registro próprio do NPC encontrado: PlayerId=249, IsDead=" +
                    dadosNpc.IsDead + ", dados locais compartilhados=" +
                    ReferenceEquals(dadosNpc, dadosLocal) + ".");
            }

            // Se existe um registro independente para o ID 249, associa o NPC
            // a ele. Isso impede que CachedPlayerData continue herdando a morte
            // do jogador local.
            if (!ReferenceEquals(seguidor.CachedPlayerData, dadosNpc))
            {
                bool dadosAntigosMortos = seguidor.Data != null && seguidor.Data.IsDead;
                seguidor.CachedPlayerData = dadosNpc;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] CachedPlayerData do NPC associado ao registro PlayerId=249. Dados anteriores indicavam morto=" +
                    dadosAntigosMortos + ", registro próprio IsDead=" + dadosNpc.IsDead + ".");
            }

            if (dadosNpc.IsDead && !restauracaoNpcPendente)
            {
                restauracaoNpcPendente = true;
                restauracaoNpcAgendadaPara = agora + 0.75f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC marcado como morto no registro 249. Restauração agendada para 0,75s.");
                return;
            }

            if (!restauracaoNpcPendente || agora < restauracaoNpcAgendadaPara)
                return;

            // Busca novamente pelo ID para não usar uma referência antiga.
            var dadosAtuais = GameData.Instance.GetPlayerById(PlayerIdSeguidor);
            if (dadosAtuais == null ||
                ReferenceEquals(dadosAtuais, PlayerControl.LocalPlayer.Data))
            {
                restauracaoNpcPendente = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Registro independente do NPC deixou de estar disponível; restauração cancelada.");
                return;
            }

            seguidor.CachedPlayerData = dadosAtuais;
            dadosAtuais.IsDead = false;
            dadosAtuais.SetDirtyBit(1u << PlayerIdSeguidor);
            AmongUsClient.Instance.SendAllStreamedObjects();

            restauracaoNpcPendente = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC revivido pela rede usando PlayerId=249 (IsDead=false, SetDirtyBit e SendAllStreamedObjects).");
        }
        catch (Exception ex)
        {
            restauracaoNpcPendente = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Falha ao restaurar o NPC pelo PlayerId=249: " + ex);
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

        AtualizarRenderizacaoCaminho();

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

        // Etapa 3: aplicar nome e cosméticos somente depois do Spawn
        // e da associação do PlayerId reservado em cada cliente.
        if (etapaInicializacao == 3 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                CustomNetObject.EnviarAparencia(
                    seguidor,
                    NomeSeguidor,
                    0,
                    "hat_pk02_TenGallonHat",
                    "skin_Wall",
                    "pet_EmptyPet",
                    "visor_EmptyVisor");

                // Aguarda a inicialização visual do personagem terminar.
                // Alguns clientes podem recriar/atualizar os cosméticos logo após o Spawn.
                etapaInicializacao = 4;
                proximaEtapa = Time.realtimeSinceStartup + 0.75f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Primeira aplicação da aparência enviada; aguardando para reaplicar.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha na primeira aplicação da aparência: " + ex);
                Resetar();
                return;
            }
        }

        // Etapa 4: reaplicar a aparência após a inicialização visual do NPC.
        if (etapaInicializacao == 4 &&
            Time.realtimeSinceStartup >= proximaEtapa)
        {
            try
            {
                CustomNetObject.EnviarAparencia(
                    seguidor,
                    NomeSeguidor,
                    0,
                    "hat_pk02_TenGallonHat",
                    "skin_Wall",
                    "pet_EmptyPet",
                    "visor_EmptyVisor");

                etapaInicializacao = 5;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Aparência reaplicada após 0,75s: skin_Wall, chapéu TenGallonHat e cor 0.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Seguidor] Falha ao reaplicar a aparência do NPC: " + ex);
                Resetar();
                return;
            }
        }

        if (etapaInicializacao < 5)
            return;

        // Mantém os clientes remotos informados da posição aproximada do NPC.
        // O movimento contínuo continua sendo controlado pelo Rigidbody2D no host.
        EnviarSnapToRedePeriodico();

        // Corrige a associação dos dados e restaura somente o registro do ID 249.
        AtualizarRegistroVidaNpc();

        // Verifica em runtime se esta versão expõe RpcMove no PlayerPhysics
        // ou no CustomNetworkTransform, sem presumir uma assinatura inexistente.
        DiagnosticarApisMovimento();

        if (seguidor.NetTransform.body == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Rigidbody2D do CustomNetworkTransform não foi encontrado.");
            Resetar();
            return;
        }

        // O Tsunami já usa este mesmo caminho: velocidade no Rigidbody2D
        // pertencente ao CustomNetworkTransform. O SnapTo periódico serve apenas
        // como correção aproximada para os clientes remotos.
        PlayerControl alvo = ObterAlvoAtual();
        if (alvo == null)
        {
            Patrulhar();
            return;
        }

        // Ao encontrar um jogador novamente, abandona o destino de patrulha
        // e força o A* a calcular uma rota nova para a perseguição.
        if (possuiDestinoPatrulha)
        {
            possuiDestinoPatrulha = false;
            LimparCaminho();
            emManobraDestravamento = false;
            monitoramentoMovimentoIniciado = false;
        }

        // Se o NPC estiver usando uma ventilação, conclui a travessia antes
        // de calcular alcance de morte ou tentar qualquer outro transporte.
        if (TentarUsarVentilacao(alvo))
            return;

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

        if (TentarUsarEscadaOuTirolesa(alvo))
            return;

        MoverAte(destino);
    }

    /// <summary>
    /// Usa os mecanismos de transporte nativos. A seleção é geométrica:
    /// só considera uma travessia quando o destino dela reduz claramente
    /// a distância até o jogador perseguido.
    /// </summary>
    private static object LerMembro(object instancia, params string[] nomes)
    {
        if (instancia == null)
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type tipo = instancia.GetType();

        foreach (string nome in nomes)
        {
            PropertyInfo propriedade = tipo.GetProperty(nome, flags);
            if (propriedade != null)
                return propriedade.GetValue(instancia);

            FieldInfo campo = tipo.GetField(nome, flags);
            if (campo != null)
                return campo.GetValue(instancia);
        }

        return null;
    }

    private static int ObterIdVent(Vent vent)
    {
        object valor = LerMembro(vent, "Id", "id", "VentId", "ventId");
        return valor == null ? -1 : Convert.ToInt32(valor);
    }
    private static Vent[] ObterVentsConectados(Vent vent)
    {
        if (vent == null)
            return Array.Empty<Vent>();

        return new[]
        {
            LerMembro(vent, "Left", "left") as Vent,
            LerMembro(vent, "Right", "right") as Vent,
            LerMembro(vent, "Center", "center") as Vent
        }
        .Where(v => v != null && v != vent && v.isActiveAndEnabled)
        .Distinct()
        .ToArray();
    }

    private static bool InvocarRpcVent(string nomeMetodo, Vent vent)
    {
        if (seguidor?.MyPhysics == null || vent == null)
            return false;

        int ventId = ObterIdVent(vent);
        if (ventId < 0)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        MethodInfo[] metodos = seguidor.MyPhysics.GetType().GetMethods(flags)
            .Where(m => string.Equals(m.Name, nomeMetodo, StringComparison.Ordinal))
            .ToArray();

        foreach (MethodInfo metodo in metodos)
        {
            ParameterInfo[] parametros = metodo.GetParameters();
            if (parametros.Length != 1)
                continue;

            object argumento;
            Type tipoParametro = parametros[0].ParameterType;
            if (tipoParametro == typeof(int))
                argumento = ventId;
            else if (tipoParametro == typeof(uint))
                argumento = (uint)ventId;
            else if (tipoParametro == typeof(byte))
                argumento = (byte)ventId;
            else if (tipoParametro.IsAssignableFrom(typeof(Vent)))
                argumento = vent;
            else
                continue;

            metodo.Invoke(seguidor.MyPhysics, new[] { argumento });
            return true;
        }

        return false;
    }

    /// <summary>
    /// Usa a rede vanilla de dutos como atalho quando a opção estiver ativada.
    /// </summary>
    private static bool TentarUsarVentilacao(PlayerControl alvo)
    {
        if (YouTubeVoteBridgePlugin.PermitirVentilacaoNPC == null ||
            !YouTubeVoteBridgePlugin.PermitirVentilacaoNPC.Value ||
            seguidor == null || seguidor.MyPhysics == null ||
            seguidor.NetTransform?.body == null || alvo == null)
            return false;

        float agora = Time.realtimeSinceStartup;

        if (aguardandoSaidaVent)
        {
            seguidor.NetTransform.body.velocity = Vector2.zero;

            if (agora < proximaTentativaVent)
                return true;

            if (InvocarRpcVent("RpcExitVent", ventSaidaAtual))
            {
                aguardandoSaidaVent = false;
                transporteEmAndamento = false;
                ventEntradaAtual = null;
                ventSaidaAtual = null;
                proximoTransporte = agora + 1.0f;
                proximaTentativaVent = agora + 1.0f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC solicitou saída da ventilação.");
                return true;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] RpcExitVent não encontrado; cancelando tentativa de ventilação.");
            aguardandoSaidaVent = false;
            ventEntradaAtual = null;
            ventSaidaAtual = null;
            transporteEmAndamento = false;
            return false;
        }

        if (agora < proximaTentativaVent || transporteEmAndamento)
            return false;

        List<Vent> listaVents = new List<Vent>();
        foreach (Vent vent in ShipStatus.Instance.GetComponentsInChildren<Vent>())
        {
            if (vent != null && vent.isActiveAndEnabled && ObterIdVent(vent) >= 0)
                listaVents.Add(vent);
        }
        Vent[] vents = listaVents.ToArray();

        if (vents.Length < 2)
            return false;

        Vector2 origem = seguidor.GetTruePosition();
        Vector2 posAlvo = alvo.GetTruePosition();
        float distanciaDireta = Vector2.Distance(origem, posAlvo);
        Vent melhorEntrada = null;
        Vent melhorSaida = null;
        float melhorCusto = float.MaxValue;

        foreach (Vent entrada in vents)
        {
            float distanciaEntrada = Vector2.Distance(origem, entrada.transform.position);
            Vent[] conectadas = ObterVentsConectados(entrada);
            bool saidaAleatoria = conectadas.Length == 0;

            // Primeiro usa a ligação Left/Right/Center do jogo.
            // Se a versão/mapa não expuser uma ligação, escolhe uma saída
            // aleatória entre as outras ventilações ativas.
            if (saidaAleatoria)
            {
                Vent[] alternativas = vents
                    .Where(v => v != entrada)
                    .ToArray();

                if (alternativas.Length == 0)
                    continue;

                conectadas = new[]
                {
                    alternativas[UnityEngine.Random.Range(0, alternativas.Length)]
                };
            }

            foreach (Vent saida in conectadas)
            {
                float distanciaSaida = Vector2.Distance(saida.transform.position, posAlvo);
                float custo = distanciaEntrada + distanciaSaida;

                // Para uma conexão conhecida, só usa o atalho se ele realmente
                // encurtar a perseguição. Na saída aleatória, aceita a escolha
                // mesmo sem ganho geométrico, conforme fallback solicitado.
                if (!saidaAleatoria &&
                    (distanciaSaida >= distanciaDireta - 0.8f ||
                     custo >= distanciaDireta - 0.6f))
                    continue;

                if (custo >= melhorCusto)
                    continue;

                melhorCusto = custo;
                melhorEntrada = entrada;
                melhorSaida = saida;
            }
        }

        if (melhorEntrada == null || melhorSaida == null)
            return false;

        float distanciaAEntrada = Vector2.Distance(origem, melhorEntrada.transform.position);
        float alcanceUso = 0.8f;
        object alcanceObj = LerMembro(melhorEntrada, "UsableDistance", "usableDistance");
        if (alcanceObj != null)
        {
            try { alcanceUso = Convert.ToSingle(alcanceObj); }
            catch { }
        }

        if (distanciaAEntrada > alcanceUso)
        {
            MoverAte(melhorEntrada.transform.position);
            return true;
        }

        seguidor.NetTransform.body.velocity = Vector2.zero;

        try
        {
            if (!InvocarRpcVent("RpcEnterVent", melhorEntrada))
            {
                proximaTentativaVent = agora + 2f;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] RpcEnterVent não encontrado com uma assinatura compatível.");
                return false;
            }

            ventEntradaAtual = melhorEntrada;
            ventSaidaAtual = melhorSaida;
            aguardandoSaidaVent = true;
            transporteEmAndamento = true;
            proximaTentativaVent = agora + 1.0f;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC solicitou entrada no duto " + ObterIdVent(melhorEntrada) +
                " e saída pelo duto " + ObterIdVent(melhorSaida) + ".");
            return true;
        }
        catch (Exception ex)
        {
            aguardandoSaidaVent = false;
            transporteEmAndamento = false;
            ventEntradaAtual = null;
            ventSaidaAtual = null;
            proximaTentativaVent = agora + 2f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao usar ventilação: " + ex.Message);
            return false;
        }
    }

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
            MoverAte(pontoEntrada);
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

    /// <summary>
    /// Calcula uma rota A* usando os colliders reais do mapa. O NPC segue
    /// os pontos da rota e recalcula quando o alvo se desloca ou o caminho expira.
    /// </summary>
    private static void MoverAte(Vector2 destino)
    {
        if (seguidor?.NetTransform?.body == null)
            return;

        Vector2 origem = seguidor.GetTruePosition();
        float agora = Time.realtimeSinceStartup;
        var corpo = seguidor.NetTransform.body;

        // Se uma manobra de escape já começou, termina-a antes de calcular
        // uma nova rota. O ponto de escape sempre é validado contra paredes.
        if (emManobraDestravamento)
        {
            if (Vector2.Distance(origem, pontoDestravamento) <= 0.12f ||
                agora >= limiteManobraDestravamento)
            {
                emManobraDestravamento = false;
                corpo.velocity = Vector2.zero;
                LimparCaminho();
                proximoRecalculoCaminho = 0f;
                ultimaPosicaoMonitorada = origem;
                inicioSemDeslocamento = agora;
                proximaVerificacaoTravamento = agora + 0.5f;
                avisoMonitoramentoMovimento = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Escape encerrado na posição " + origem +
                    ". Forçando recálculo completo da rota A*.");
            }
            else
            {
                Vector2 direcaoEscape = pontoDestravamento - origem;
                corpo.velocity = direcaoEscape.normalized * Velocidade;
                return;
            }
        }

        // Monitoramento por deslocamento real: não depende do Rigidbody.velocity,
        // pois o jogo pode zerá-lo mesmo quando o NPC continua recebendo destino.
        if (!monitoramentoMovimentoIniciado)
        {
            monitoramentoMovimentoIniciado = true;
            ultimaPosicaoMonitorada = origem;
            inicioSemDeslocamento = agora;
            proximaVerificacaoTravamento = agora + 0.5f;
            avisoMonitoramentoMovimento = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Monitorando movimento solicitado. Posição inicial=" + origem +
                ", destino=" + destino + ".");
        }
        else if (agora >= proximaVerificacaoTravamento)
        {
            float deslocamento = Vector2.Distance(origem, ultimaPosicaoMonitorada);
            ultimaPosicaoMonitorada = origem;
            proximaVerificacaoTravamento = agora + 0.5f;

            if (deslocamento >= DeslocamentoMinimoMonitoramento)
            {
                if (avisoMonitoramentoMovimento)
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Seguidor] Movimento retomado; deslocamento observado=" +
                        deslocamento.ToString("F3") + ". Limpando rota antiga e recalculando A*.");

                if (avisoMonitoramentoMovimento)
                {
                    LimparCaminho();
                    proximoRecalculoCaminho = 0f;
                }

                inicioSemDeslocamento = agora;
                avisoMonitoramentoMovimento = false;
            }
            else if (agora - inicioSemDeslocamento >= TempoParaConsiderarTravado)
            {
                if (!avisoMonitoramentoMovimento)
                {
                    avisoMonitoramentoMovimento = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] Possível travamento detectado: sem deslocamento por " +
                        (agora - inicioSemDeslocamento).ToString("F1") + "s. Posição=" + origem +
                        ", velocidade Rigidbody=" + corpo.velocity + ". Iniciando escape aleatório.");
                }

                if (TentarEncontrarPontoDestravamento(origem, destino, out Vector2 escape))
                {
                    pontoDestravamento = escape;
                    emManobraDestravamento = true;
                    limiteManobraDestravamento = agora + 0.85f;
                    LimparCaminho();
                    corpo.velocity = (escape - origem).normalized * Velocidade;

                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] Escape aleatório iniciado: " + origem + " -> " + escape +
                        ". Após o deslocamento, a rota A* será recalculada.");
                    return;
                }

                LimparCaminho();
                proximoRecalculoCaminho = 0f;
                corpo.velocity = Vector2.zero;
                inicioSemDeslocamento = agora;

                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Nenhum escape aleatório seguro encontrado. A* será tentado novamente.");
                return;
            }
        }

        bool precisaRecalcular =
            caminhoAtual.Count == 0 ||
            indiceCaminho >= caminhoAtual.Count ||
            agora >= proximoRecalculoCaminho ||
            Vector2.Distance(destino, destinoCaminhoAnterior) >= DistanciaRecalculoAlvo;

        if (precisaRecalcular)
        {
            List<Vector2> novoCaminho = CalcularCaminhoAStar(origem, destino);
            destinoCaminhoAnterior = destino;
            proximoRecalculoCaminho = agora + IntervaloRecalculoCaminho;

            if (novoCaminho == null || novoCaminho.Count == 0)
            {
                caminhoAtual.Clear();
                indiceCaminho = 0;
                seguidor.NetTransform.body.velocity = Vector2.zero;
                return;
            }

            caminhoAtual = novoCaminho;
            indiceCaminho = 0;
        }

        while (indiceCaminho < caminhoAtual.Count &&
               Vector2.Distance(origem, caminhoAtual[indiceCaminho]) <= 0.16f)
            indiceCaminho++;

        if (indiceCaminho >= caminhoAtual.Count)
        {
            seguidor.NetTransform.body.velocity = Vector2.zero;
            return;
        }

        // Tenta avançar diretamente para o ponto mais distante que ainda
        // tenha linha livre, evitando movimentos desnecessários em zigue-zague.
        int melhorIndice = indiceCaminho;
        for (int i = caminhoAtual.Count - 1; i > indiceCaminho; i--)
        {
            if (PodeMoverEntre(origem, caminhoAtual[i]))
            {
                melhorIndice = i;
                break;
            }
        }

        Vector2 proximoPonto = caminhoAtual[melhorIndice];
        Vector2 direcao = proximoPonto - origem;        if (direcao.sqrMagnitude < 0.0001f)
        {
            seguidor.NetTransform.body.velocity = Vector2.zero;
            return;
        }

        corpo.velocity = direcao.normalized * Velocidade;
    }

    /// <summary>
    /// Faz o NPC circular pelo mapa quando não há jogadores válidos para perseguir.
    /// Os destinos são sorteados dentro das salas e a movimentação usa o mesmo A*,
    /// as mesmas verificações de colisão e o mesmo detector de travamento da perseguição.
    /// </summary>
    private static void Patrulhar()
    {
        if (seguidor?.NetTransform?.body == null || ShipStatus.Instance == null)
            return;

        var corpo = seguidor.NetTransform.body;
        float agora = Time.realtimeSinceStartup;

        // Se uma travessia iniciada durante a perseguição ainda estiver acontecendo,
        // deixa a animação vanilla terminar antes de voltar a andar.
        if (transporteEmAndamento)
        {
            if (seguidor.onLadder || seguidor.inMovingPlat || !seguidor.moveable)
            {
                corpo.velocity = Vector2.zero;
                return;
            }

            transporteEmAndamento = false;
        }

        Vector2 origem = seguidor.GetTruePosition();
        bool chegouAoDestino = possuiDestinoPatrulha &&
            Vector2.Distance(origem, destinoPatrulha) <= DistanciaChegadaPatrulha;
        bool destinoExpirou = possuiDestinoPatrulha && agora >= limiteDestinoPatrulha;

        if (!possuiDestinoPatrulha || chegouAoDestino || destinoExpirou)
        {
            corpo.velocity = Vector2.zero;
            LimparCaminho();
            emManobraDestravamento = false;
            monitoramentoMovimentoIniciado = false;

            if (agora < proximaTentativaDestinoPatrulha)
                return;

            if (!TentarEscolherDestinoPatrulha(origem, out Vector2 novoDestino))
            {
                possuiDestinoPatrulha = false;
                proximaTentativaDestinoPatrulha = agora + 1f;
                return;
            }

            destinoPatrulha = novoDestino;
            possuiDestinoPatrulha = true;
            limiteDestinoPatrulha = agora + DuracaoDestinoPatrulha;
            proximaTentativaDestinoPatrulha = agora + 0.25f;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Sem alvos; NPC patrulhando até " + destinoPatrulha + ".");
        }

        MoverAte(destinoPatrulha);
    }

    /// <summary>
    /// Sorteia pontos livres dentro das salas do mapa. A rota entre salas
    /// é resolvida pelo A* já existente, portanto o ponto não precisa ter
    /// visão direta a partir da posição atual do NPC.
    /// </summary>
    private static bool TentarEscolherDestinoPatrulha(Vector2 origem, out Vector2 destino)
    {
        destino = origem;

        if (ShipStatus.Instance == null || seguidor == null)
            return false;

        var salas = ShipStatus.Instance.AllRooms
            .Where(sala => sala != null && sala.roomArea != null)
            .ToArray();

        if (salas.Length == 0)
            return false;

        // Tenta várias salas e posições para evitar escolher pontos dentro de paredes,
        // móveis ou fora do contorno real da sala.
        for (int tentativa = 0; tentativa < 80; tentativa++)
        {
            var sala = salas[UnityEngine.Random.Range(0, salas.Length)];
            Bounds limites = sala.roomArea.bounds;

            Vector2 candidato = new Vector2(
                UnityEngine.Random.Range(limites.min.x, limites.max.x),
                UnityEngine.Random.Range(limites.min.y, limites.max.y));

            if (!sala.roomArea.OverlapPoint(candidato))
                continue;

            // Valida somente se o próprio ponto está livre. Não exige caminho
            // em linha reta desde a posição atual, pois o A* contorna paredes.
            if (!PodeMoverEntre(candidato, candidato))
                continue;

            if (Vector2.Distance(origem, candidato) < 1.25f)
                continue;

            destino = candidato;
            return true;
        }

        return false;
    }

    private static bool TentarEncontrarPontoDestravamento(
        Vector2 origem, Vector2 destino, out Vector2 melhorPonto)
    {
        melhorPonto = origem;
        float[] distancias = { 0.80f, 0.60f, 0.40f, 0.24f };
        var direcoes = DirecoesCaminho.OrderBy(_ => UnityEngine.Random.value).ToArray();

        foreach (float distancia in distancias)
        {
            foreach (Vector2Int vetor in direcoes)
            {
                Vector2 direcao = new Vector2(vetor.x, vetor.y).normalized;
                Vector2 candidato = origem + direcao * distancia;
                if (!EstaDentroDosLimitesDoMapa(candidato) || !PodeMoverEntre(origem, candidato))
                    continue;
                melhorPonto = candidato;
                return true;
            }
        }
        return false;
    }

    private static bool EstaDentroDosLimitesDoMapa(Vector2 ponto)
    {
        if (ShipStatus.Instance == null)
            return false;
        Bounds limites = new Bounds();
        bool encontrouSala = false;
        foreach (var sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || !sala.roomArea)
                continue;
            if (!encontrouSala)
            {
                limites = sala.roomArea.bounds;
                encontrouSala = true;
            }
            else
                limites.Encapsulate(sala.roomArea.bounds);
        }
        if (!encontrouSala)
            return false;
        limites.Expand(1.0f);
        return limites.Contains(ponto);
    }

    private static bool EstaDentroDeAlgumaSala(Vector2 ponto)
    {
        if (ShipStatus.Instance == null)
            return false;

        foreach (var sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || !sala.roomArea)
                continue;

            if (sala.roomArea.bounds.Contains(ponto))
                return true;
        }

        return false;
    }

    private static List<Vector2> CalcularCaminhoAStar(Vector2 inicio, Vector2 destino)
    {
        if (ShipStatus.Instance == null || seguidor == null)
            return null;

        Bounds limites = new Bounds();
        bool possuiSala = false;

        foreach (var sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || !sala.roomArea)
                continue;

            if (possuiSala)
                limites.Encapsulate(sala.roomArea.bounds);
            else
            {
                limites = sala.roomArea.bounds;
                possuiSala = true;
            }
        }

        if (!possuiSala)
            return null;

        limites.Expand(TamanhoCelula * 2f);
        Vector2 minimo = limites.min;
        int largura = Mathf.CeilToInt(limites.size.x / TamanhoCelula);
        int altura = Mathf.CeilToInt(limites.size.y / TamanhoCelula);

        if (largura <= 0 || altura <= 0 || largura * altura > 30000)
            return null;

        Vector2Int inicioGrid = ParaGrid(inicio, minimo, largura, altura);
        Vector2Int destinoGrid = ParaGrid(destino, minimo, largura, altura);

        if (!EncontrarCelulaLivre(ref inicioGrid, inicio, minimo, largura, altura) ||
            !EncontrarCelulaLivre(ref destinoGrid, destino, minimo, largura, altura))
            return null;

        var abertos = new PriorityQueue<Vector2Int, float>();
        var cacheCelulasLivres = new Dictionary<Vector2Int, bool>();
        var custos = new Dictionary<Vector2Int, float>();
        var anteriores = new Dictionary<Vector2Int, Vector2Int>();
        var fechados = new HashSet<Vector2Int>();

        custos[inicioGrid] = 0f;
        abertos.Enqueue(inicioGrid, Heuristica(inicioGrid, destinoGrid));

        int expandidos = 0;
        while (abertos.Count > 0 && expandidos < 12000)
        {
            Vector2Int atual = abertos.Dequeue();
            if (!fechados.Add(atual))
                continue;

            expandidos++;
            if (atual == destinoGrid)
            {
                List<Vector2> caminho = ReconstruirCaminho(
                    inicioGrid, destinoGrid, anteriores, minimo);

                if (caminho != null && caminho.Count > 0 &&
                    PodeMoverEntre(caminho[caminho.Count - 1], destino))
                    caminho.Add(destino);

                return caminho;
            }

            foreach (Vector2Int direcao in DirecoesCaminho)
            {
                Vector2Int vizinho = atual + direcao;
                if (vizinho.x < 0 || vizinho.y < 0 ||
                    vizinho.x >= largura || vizinho.y >= altura ||
                    fechados.Contains(vizinho))
                    continue;

                if (!CelulaLivre(vizinho, minimo, cacheCelulasLivres))
                    continue;

                Vector2 pontoAtual = CentroCelula(atual, minimo);
                Vector2 pontoVizinho = CentroCelula(vizinho, minimo);

                // A trajetória é validada com o formato real do collider do NPC.
                if (!PodePassarEntre(pontoAtual, pontoVizinho))
                    continue;

                float custoDistancia = Mathf.Sqrt(
                    direcao.x * direcao.x + direcao.y * direcao.y);
                float novoCusto = custos[atual] + custoDistancia;

                if (custos.TryGetValue(vizinho, out float custoExistente) &&
                    novoCusto >= custoExistente)
                    continue;

                custos[vizinho] = novoCusto;
                anteriores[vizinho] = atual;
                abertos.Enqueue(vizinho, novoCusto + Heuristica(vizinho, destinoGrid));
            }
        }

        return null;
    }

    private static Collider2D ObterColliderFisicoNpc()
    {
        if (seguidor == null)
            return null;

        if (colliderFisicoNpc != null && colliderFisicoNpc.enabled)
            return colliderFisicoNpc;

        colliderFisicoNpc = seguidor.GetComponent<Collider2D>();
        if (colliderFisicoNpc == null)
        {
            Collider2D[] colliders = seguidor.GetComponentsInChildren<Collider2D>(true);
            foreach (Collider2D candidato in colliders)
            {
                if (candidato != null && candidato.enabled && !candidato.isTrigger)
                {
                    colliderFisicoNpc = candidato;
                    break;
                }
            }
        }

        return colliderFisicoNpc;
    }

    private static Vector2 ObterCentroColliderNaPosicao(Collider2D collider, Vector2 posicaoRaiz)
    {
        Vector2 deslocamentoCentro = (Vector2)collider.bounds.center -
                                     (Vector2)seguidor.transform.position;
        return posicaoRaiz + deslocamentoCentro;
    }

    private static Vector2 ObterEscalaAbsoluta(Transform transformacao)
    {
        Vector3 escala = transformacao.lossyScale;
        return new Vector2(Mathf.Abs(escala.x), Mathf.Abs(escala.y));
    }

    // Usa o formato real do collider do personagem na posição hipotética.
    // Circle, Box e Capsule mantêm sua geometria; outros tipos usam um círculo
    // conservador baseado nos limites reais do collider.
    private static Collider2D[] ObterColisoesNaPosicao(Vector2 posicaoRaiz, int mascara)
    {
        Collider2D collider = ObterColliderFisicoNpc();
        if (collider == null)
            return Physics2D.OverlapCircleAll(posicaoRaiz, RaioColisaoFallback, mascara);

        Vector2 centro = ObterCentroColliderNaPosicao(collider, posicaoRaiz);
        Vector2 escala = ObterEscalaAbsoluta(collider.transform);
        float angulo = collider.transform.eulerAngles.z;

        if (collider is CircleCollider2D circulo)
        {
            float raio = circulo.radius * Mathf.Max(escala.x, escala.y);
            return Physics2D.OverlapCircleAll(centro, raio, mascara);
        }

        if (collider is BoxCollider2D caixa)
        {
            Vector2 tamanho = Vector2.Scale(caixa.size, escala);
            return Physics2D.OverlapBoxAll(centro, tamanho, angulo, mascara);
        }

        if (collider is CapsuleCollider2D capsula)
        {
            Vector2 tamanho = Vector2.Scale(capsula.size, escala);
            return Physics2D.OverlapCapsuleAll(
                centro, tamanho, capsula.direction, angulo, mascara);
        }

        float raioFallback = collider.bounds.extents.magnitude;
        return Physics2D.OverlapCircleAll(centro, raioFallback, mascara);
    }

    // Faz o cast usando a mesma geometria do collider do personagem.
    private static RaycastHit2D[] ObterColisoesNoDeslocamento(
        Vector2 origemRaiz, Vector2 direcaoMovimento, float distancia, int mascara)
    {
        Collider2D collider = ObterColliderFisicoNpc();
        if (collider == null)
        {
            return Physics2D.CircleCastAll(
                origemRaiz, RaioColisaoFallback, direcaoMovimento, distancia, mascara);
        }

        Vector2 centro = ObterCentroColliderNaPosicao(collider, origemRaiz);
        Vector2 escala = ObterEscalaAbsoluta(collider.transform);
        float angulo = collider.transform.eulerAngles.z;

        if (collider is CircleCollider2D circulo)
        {
            float raio = circulo.radius * Mathf.Max(escala.x, escala.y);
            return Physics2D.CircleCastAll(
                centro, raio, direcaoMovimento, distancia, mascara);
        }

        if (collider is BoxCollider2D caixa)
        {
            Vector2 tamanho = Vector2.Scale(caixa.size, escala);
            return Physics2D.BoxCastAll(
                centro, tamanho, angulo, direcaoMovimento, distancia, mascara);
        }

        if (collider is CapsuleCollider2D capsula)
        {
            Vector2 tamanho = Vector2.Scale(capsula.size, escala);
            return Physics2D.CapsuleCastAll(
                centro, tamanho, capsula.direction, angulo,
                direcaoMovimento, distancia, mascara);
        }

        float raioFallback = collider.bounds.extents.magnitude;
        return Physics2D.CircleCastAll(
            centro, raioFallback, direcaoMovimento, distancia, mascara);
    }

    private static bool CelulaLivre(
        Vector2Int celula, Vector2 minimo, Dictionary<Vector2Int, bool> cache)
    {
        if (cache.TryGetValue(celula, out bool livre))
            return livre;

        Vector2 centro = CentroCelula(celula, minimo);
        Collider2D[] colisoes = ObterColisoesNaPosicao(
            centro, Constants.ShipAndAllObjectsMask);

        livre = true;
        foreach (Collider2D colisor in colisoes)
        {
            if (colisor == null || colisor.isTrigger ||
                colisor.transform.IsChildOf(seguidor.transform))
                continue;

            livre = false;
            break;
        }

        cache[celula] = livre;
        return livre;
    }

    // Não adiciona uma margem artificial: a suavização respeita somente
    // o espaço realmente ocupado pelo collider do personagem.
    private static bool SegmentoMantemAfastamento(Vector2 inicio, Vector2 fim)
    {
        return PodePassarEntre(inicio, fim);
    }

    private static Vector2Int ParaGrid(Vector2 ponto, Vector2 minimo, int largura, int altura)
    {
        return new Vector2Int(
            Mathf.Clamp(Mathf.FloorToInt((ponto.x - minimo.x) / TamanhoCelula), 0, largura - 1),
            Mathf.Clamp(Mathf.FloorToInt((ponto.y - minimo.y) / TamanhoCelula), 0, altura - 1));
    }

    private static Vector2 CentroCelula(Vector2Int celula, Vector2 minimo)
    {
        return minimo + new Vector2(
            (celula.x + 0.5f) * TamanhoCelula,
            (celula.y + 0.5f) * TamanhoCelula);
    }

    private static bool EncontrarCelulaLivre(
        ref Vector2Int celula, Vector2 origem, Vector2 minimo, int largura, int altura)
    {
        Vector2 centro = CentroCelula(celula, minimo);
        if (PodeMoverEntre(origem, centro))
            return true;

        float menor = float.MaxValue;
        Vector2Int melhor = celula;

        for (int raio = 1; raio <= 5; raio++)
        {
            for (int x = -raio; x <= raio; x++)
            for (int y = -raio; y <= raio; y++)
            {
                if (Mathf.Abs(x) != raio && Mathf.Abs(y) != raio)
                    continue;

                Vector2Int candidato = celula + new Vector2Int(x, y);
                if (candidato.x < 0 || candidato.y < 0 ||
                    candidato.x >= largura || candidato.y >= altura)
                    continue;

                Vector2 posicao = CentroCelula(candidato, minimo);
                if (!PodeMoverEntre(origem, posicao))
                    continue;

                float distancia = Vector2.Distance(origem, posicao);
                if (distancia < menor)
                {
                    menor = distancia;
                    melhor = candidato;
                }
            }

            if (menor < float.MaxValue)
            {
                celula = melhor;
                return true;
            }
        }

        return false;
    }

    private static bool PodePassarEntre(Vector2 origem, Vector2 destino)
    {
        if (seguidor == null)
            return false;

        Vector2 delta = destino - origem;
        float distancia = delta.magnitude;
        if (distancia <= 0.001f)
            return true;

        RaycastHit2D[] impactos = ObterColisoesNoDeslocamento(
            origem, delta / distancia, distancia, Constants.ShipAndAllObjectsMask);

        foreach (RaycastHit2D impacto in impactos)
        {
            Collider2D colisor = impacto.collider;
            if (colisor == null || colisor.isTrigger ||
                colisor.transform.IsChildOf(seguidor.transform))
                continue;

            return false;
        }

        return true;
    }

    private static bool PodeMoverEntre(Vector2 origem, Vector2 destino)
    {
        if (seguidor == null)
            return false;

        int mascara = Constants.ShipAndAllObjectsMask;
        Collider2D[] sobreposicoes = ObterColisoesNaPosicao(destino, mascara);

        foreach (Collider2D colisor in sobreposicoes)
        {
            if (colisor == null || colisor.isTrigger ||
                colisor.transform.IsChildOf(seguidor.transform))
                continue;
            return false;
        }

        return PodePassarEntre(origem, destino);
    }

    private static float Heuristica(Vector2Int a, Vector2Int b)
    {
        int dx = a.x - b.x;
        int dy = a.y - b.y;

        // Distância euclidiana: admissível para as 32 direções e seus custos.
        return Mathf.Sqrt(dx * dx + dy * dy);
    }

    private static List<Vector2> ReconstruirCaminho(
        Vector2Int inicio, Vector2Int fim,
        Dictionary<Vector2Int, Vector2Int> anteriores, Vector2 minimo)
    {
        var caminho = new List<Vector2>();
        Vector2Int atual = fim;
        caminho.Add(CentroCelula(atual, minimo));

        while (atual != inicio)
        {
            if (!anteriores.TryGetValue(atual, out Vector2Int anterior))
                return null;

            atual = anterior;
            caminho.Add(CentroCelula(atual, minimo));
        }

        caminho.Reverse();

        // Suaviza a rota: remove pontos intermediários quando existe
        // uma linha de movimento desobstruída entre dois pontos mais distantes.
        var suavizado = new List<Vector2>();
        int indice = 0;
        while (indice < caminho.Count)
        {
            suavizado.Add(caminho[indice]);
            int maisDistante = indice + 1;

            for (int teste = caminho.Count - 1; teste > indice + 1; teste--)
            {
                if (PodeMoverEntre(caminho[indice], caminho[teste]) &&
                    SegmentoMantemAfastamento(caminho[indice], caminho[teste]))
                {
                    maisDistante = teste;
                    break;
                }
            }

            indice = maisDistante;
        }

        return suavizado;
    }

    internal static bool AlternarVisualizacaoCaminho()
    {
        mostrarCaminhoNpc = !mostrarCaminhoNpc;
        if (!mostrarCaminhoNpc)
            OcultarRenderizacaoCaminho();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F8/Seguidor] Visualização do caminho " +
            (mostrarCaminhoNpc ? "ativada." : "desativada."));
        return mostrarCaminhoNpc;
    }

    private static void AtualizarRenderizacaoCaminho()
    {
        if (!mostrarCaminhoNpc || seguidor == null ||
            caminhoAtual == null || caminhoAtual.Count == 0)
        {
            OcultarRenderizacaoCaminho();
            return;
        }

        try
        {
            if (renderizadorCaminhoNpc == null)
            {
                GameObject objeto = new GameObject("YouTubeVoteBridge_NPC_Path");
                renderizadorCaminhoNpc = objeto.AddComponent<LineRenderer>();
                renderizadorCaminhoNpc.useWorldSpace = true;
                renderizadorCaminhoNpc.loop = false;
                renderizadorCaminhoNpc.widthMultiplier = 0.045f;
                renderizadorCaminhoNpc.numCapVertices = 2;
                renderizadorCaminhoNpc.numCornerVertices = 2;
                renderizadorCaminhoNpc.startColor = new Color(0.1f, 0.9f, 1f, 0.95f);
                renderizadorCaminhoNpc.endColor = new Color(0.2f, 1f, 0.35f, 0.95f);
                renderizadorCaminhoNpc.sortingOrder = 500;
                Shader shader = Shader.Find("Sprites/Default");
                if (shader != null)
                {
                    materialCaminhoNpc = new Material(shader);
                    renderizadorCaminhoNpc.material = materialCaminhoNpc;
                }
            }

            int primeiroIndice = Mathf.Clamp(indiceCaminho, 0, caminhoAtual.Count);
            int pontosRestantes = caminhoAtual.Count - primeiroIndice;
            int quantidade = pontosRestantes + 1;
            if (quantidade < 2)
            {
                OcultarRenderizacaoCaminho();
                return;
            }

            renderizadorCaminhoNpc.positionCount = quantidade;
            Vector3 posicaoNpc = seguidor.transform.position;
            posicaoNpc.z -= 0.15f;
            renderizadorCaminhoNpc.SetPosition(0, posicaoNpc);
            for (int i = 0; i < pontosRestantes; i++)
            {
                Vector2 ponto = caminhoAtual[primeiroIndice + i];
                renderizadorCaminhoNpc.SetPosition(i + 1,
                    new Vector3(ponto.x, ponto.y, posicaoNpc.z));
            }
            renderizadorCaminhoNpc.enabled = true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F8/Seguidor] Não foi possível desenhar o caminho do NPC: " + ex.Message);
            OcultarRenderizacaoCaminho();
        }
    }

    private static void OcultarRenderizacaoCaminho()
    {
        if (renderizadorCaminhoNpc != null)
        {
            renderizadorCaminhoNpc.positionCount = 0;
            renderizadorCaminhoNpc.enabled = false;
        }
    }

    private static void DestruirRenderizacaoCaminho()
    {
        if (renderizadorCaminhoNpc != null)
            UnityEngine.Object.Destroy(renderizadorCaminhoNpc.gameObject);
        if (materialCaminhoNpc != null)
            UnityEngine.Object.Destroy(materialCaminhoNpc);
        renderizadorCaminhoNpc = null;
        materialCaminhoNpc = null;
    }

    private static void LimparCaminho()
    {
        caminhoAtual.Clear();
        indiceCaminho = 0;
        destinoCaminhoAnterior = Vector2.zero;
        proximoRecalculoCaminho = 0f;
    }

    private static System.Reflection.MemberInfo membroEstadoDuto;
    private static System.Reflection.MemberInfo membroEstadoDutoPhysics;
    private static bool estadoDutoProcurado;
    private static bool estadoDutoPhysicsProcurado;
    private static bool avisoEstadoDuto;

    private static System.Reflection.MemberInfo ProcurarMembroEstadoDuto(Type tipo)
    {
        const System.Reflection.BindingFlags flags =
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic;

        return (System.Reflection.MemberInfo)tipo.GetProperty("inVent", flags) ??
               tipo.GetField("inVent", flags) ??
               (System.Reflection.MemberInfo)tipo.GetProperty("InVent", flags) ??
               tipo.GetField("InVent", flags) ??
               (System.Reflection.MemberInfo)tipo.GetProperty("isInVent", flags) ??
               tipo.GetField("isInVent", flags) ??
               (System.Reflection.MemberInfo)tipo.GetProperty("IsInVent", flags) ??
               tipo.GetField("IsInVent", flags);
    }

    // Consulta o estado vanilla do jogador. Tenta PlayerControl e, como fallback,
    // PlayerPhysics. Se a versão não expuser o estado, falha de forma segura:
    // o NPC não escolhe nem mata um jogador cujo estado no duto seja desconhecido.
    private static bool EstaNoDuto(PlayerControl jogador)
    {
        if (jogador == null)
            return true;

        try
        {
            if (!estadoDutoProcurado)
            {
                estadoDutoProcurado = true;
                membroEstadoDuto = ProcurarMembroEstadoDuto(typeof(PlayerControl));
            }

            if (membroEstadoDuto is System.Reflection.PropertyInfo propriedade)
                return propriedade.GetValue(jogador) is bool valorPropriedade && valorPropriedade;

            if (membroEstadoDuto is System.Reflection.FieldInfo campo)
                return campo.GetValue(jogador) is bool valorCampo && valorCampo;

            object fisica = jogador.MyPhysics;
            if (fisica != null)
            {
                if (!estadoDutoPhysicsProcurado)
                {
                    estadoDutoPhysicsProcurado = true;
                    membroEstadoDutoPhysics = ProcurarMembroEstadoDuto(fisica.GetType());
                }

                if (membroEstadoDutoPhysics is System.Reflection.PropertyInfo propriedadeFisica)
                    return propriedadeFisica.GetValue(fisica) is bool valorPropriedadeFisica && valorPropriedadeFisica;

                if (membroEstadoDutoPhysics is System.Reflection.FieldInfo campoFisica)
                    return campoFisica.GetValue(fisica) is bool valorCampoFisica && valorCampoFisica;
            }

            if (!avisoEstadoDuto)
            {
                avisoEstadoDuto = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Não foi possível localizar o estado inVent em PlayerControl nem PlayerPhysics. O NPC vai ignorar jogadores por segurança.");
            }
        }
        catch (Exception ex)
        {
            if (!avisoEstadoDuto)
            {
                avisoEstadoDuto = true;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Falha ao consultar o estado do duto: " + ex.Message);
            }
        }

        return true;
    }

    private static PlayerControl ObterAlvoAtual()
    {
        if (alvoAtual != null &&
            alvoAtual.Data != null &&
            !alvoAtual.Data.IsDead &&
            !alvoAtual.Data.Disconnected &&
            !EstaNoDuto(alvoAtual))
        {
            if (Time.realtimeSinceStartup < proximaBuscaAlvo)
                return alvoAtual;
        }
        else
        {
            // Abandona imediatamente o alvo quando ele entra em um duto.
            alvoAtual = null;
            proximaBuscaAlvo = 0f;
        }

        proximaBuscaAlvo = Time.realtimeSinceStartup + IntervaloTrocaAlvo;
        alvoAtual = ObterJogadorVivoMaisProximo(seguidor.GetTruePosition());

        byte novoPlayerIdAlvo = alvoAtual != null
            ? alvoAtual.PlayerId
            : byte.MaxValue;

        if (!alvoInicialNotificado || novoPlayerIdAlvo != ultimoAlvoNotificado)
        {
            alvoInicialNotificado = true;
            ultimoAlvoNotificado = novoPlayerIdAlvo;
            EnviarDestinoAosClientesComMod(alvoAtual);
        }

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
                jogador.Data.Disconnected ||
                EstaNoDuto(jogador))
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

    private static void MatarAlvo(PlayerControl alvo)    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            alvo == null ||
            alvo.Data == null ||
            alvo.Data.IsDead ||
            alvo.Data.Disconnected ||
            EstaNoDuto(alvo) ||
            PlayerControl.LocalPlayer == null)
            return;

        try
        {
            // Primeiro tenta registrar o NPC como autor da morte.
            // Se o jogo rejeitar o RPC por o NPC artificial não possuir autoridade
            // de rede, o próprio alvo comete suicídio como alternativa compatível.
            seguidor.RpcMurderPlayer(alvo, true);

            if (alvo.Data != null && !alvo.Data.IsDead)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] O jogo não aceitou o NPC como autor da morte; usando fallback Alvo killed Alvo.");

                alvo.RpcMurderPlayer(alvo, true);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC tentou matar " + alvo.Data.PlayerName +
                " (PlayerId=" + alvo.PlayerId + "). Autor inicial: NPC ASSASINO.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Falha ao matar o alvo: " + ex);
        }
    }

    internal static void Resetar()
    {
        if (YouTubeVoteBridgePlugin.EhHost() && seguidor != null)
            EnviarDestinoAosClientesComMod(null);

        mostrarCaminhoNpc = false;
        DestruirRenderizacaoCaminho();
        ativo = false;
        armado = false;
        aguardandoFimRoleReveal = false;
        etapaInicializacao = 0;
        proximaVerificacaoVidaNpc = 0f;
        restauracaoNpcAgendadaPara = 0f;
        restauracaoNpcPendente = false;
        diagnosticoRegistroNpcFeito = false;
        avisoRegistroNpcAusente = false;
        avisoRegistroNpcCompartilhado = false;
        diagnosticoApisMovimentoExecutado = false;
        netIdSeguidorCliente = 0;
        alvoReplicadoCliente = byte.MaxValue;
        destinoReplicadoCliente = Vector2.zero;
        destinoReplicadoRecebido = false;
        ultimoAlvoNotificado = byte.MaxValue;
        alvoInicialNotificado = false;
        proximaEtapa = 0f;
        proximaBuscaAlvo = 0f;
        alvoAtual = null;
        possuiDestinoPatrulha = false;
        destinoPatrulha = Vector2.zero;
        limiteDestinoPatrulha = 0f;
        proximaTentativaDestinoPatrulha = 0f;
        transporteEmAndamento = false;
        aguardandoSaidaVent = false;
        ventEntradaAtual = null;
        ventSaidaAtual = null;
        proximaTentativaVent = 0f;
        limiteTransporte = 0f;
        proximoTransporte = 0f;
        proximoSnapToRede = 0f;
        sequenciaSnapToRede = 0;
        sequenciaSnapToRedeInicializada = false;
        monitoramentoMovimentoIniciado = false;
        inicioSemDeslocamento = 0f;
        avisoMonitoramentoMovimento = false;
        ultimaPosicaoMonitorada = Vector2.zero;
        proximaVerificacaoTravamento = 0f;
        emManobraDestravamento = false;
        pontoDestravamento = Vector2.zero;
        limiteManobraDestravamento = 0f;
        LimparCaminho();

        colliderFisicoNpc = null;

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
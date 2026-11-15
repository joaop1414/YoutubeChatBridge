using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using MoreGamemodes;
using AmongUs.GameOptions;
using UnityEngine;
using Hazel;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes;

namespace YouTubeVoteBridge;

/// <summary>
/// NPC Assassino controlado por uma IA com estados de patrulha, perseguição,
/// investigação, sabotagem e transporte. A movimentação usa o Rigidbody2D do
/// CustomNetworkTransform e a posição é replicada pelo transform nativo.
/// </summary>
internal sealed class EventoSeguidorMortal : EventoVotacao
{
    private const byte PlayerIdSeguidor = 249;
    private const float Velocidade = 1.35f;
    private const float PenalidadeProximidadeParede = 0.24f;
    // Hitbox virtual independente dos colliders físicos do prefab do NPC.
    // A colisão é calculada entre os centros visuais dos dois personagens.
    // Margem ampliada para compensar pequenas diferenças entre a posição replicada
    // do NPC e o centro visual do jogador durante o contato.
    private const float RaioHitboxVirtualNpc = 1.05f;
    private const float DeslocamentoCentroVisualFallback = 0.36f;
    private const float IntervaloTrocaAlvo = 0.25f;
    private const string NomeSeguidor = "<color=red>NPC ASSASSINO</color>";

    private static bool armado;
    private static bool ativo;
    private static bool aguardandoFimRoleReveal;
    private static int etapaInicializacao;
    private static float proximaEtapa;
    private static float instanteSpawnNpc;
    private static float proximaBuscaAlvo;
    private static float proximoKillNpc;
    private const float IntervaloReplicacaoDestino = 0.15f;
    private const float DistanciaMinimaReplicacaoDestino = 0.12f;
    private static float proximaReplicacaoDestino;
    private static Vector2 ultimoDestinoReplicado;
    private static byte ultimoAlvoReplicado = byte.MaxValue;
    private static bool destinoReplicadoInicializado;
    private static PlayerControl alvoIgnoradoNavegacao;
    private static float alcanceVisaoTripulanteCacheado;
    private static float proximaAtualizacaoAlcanceVisao;
    private static float proximaTentativaAbrirPorta;
    private static bool resolvendoSabotagemLuzes;
    private static Vector2 destinoEletrica;
    private static float proximaAcaoInterruptor;
    private const float IntervaloInterruptorLuzes = 0.5f;
    private static bool resolvendoOutraSabotagem;
    private static SystemTypes sabotagemAtual;
    private static Vector2 destinoOutraSabotagem;
    private static int consoleO2Atual;
    private static bool aguardandoSegundoJogadorReator;
    private static float proximaTentativaReparo;
    private static float aguardarPerseguicaoAte;
    private const float PausaAposSabotagem = 8f;
    private static SystemTypes ultimaSabotagemSemSala;
    private static bool avisoSabotagemSemSala;
    private static readonly HashSet<SystemTypes> sistemasSemEstadoAtivoDiagnosticados = new();
    // EHR mantém wrappers concretos dos sistemas, pois Systems pode expor apenas ISystemType.
    private static readonly Dictionary<SystemTypes, object> sistemasTipadosCache = new();
    private static PlayerControl seguidor;
    private static PlayerControl alvoAtual;
    private static readonly InteligenciaArtificialAssassino inteligencia = new();
    private static readonly List<PlayerControl> jogadoresParaIA = new();

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
    private static bool possuiDestinoPatrulha;
    private static Vector2 destinoPatrulha;
    private static float proximaTentativaDestinoPatrulha;
    private static SystemTypes ultimaSalaPatrulha;
    private static bool possuiUltimaSalaPatrulha;

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
    // Grade mais ampla reduz os micro-waypoints e as curvas do A*; a suavização
    // ainda valida cada atalho com o collider real para não atravessar obstáculos.
    private const float TamanhoCelula = 0.30f;
    // Fallback usado somente antes de o prefab do NPC disponibilizar seu collider.
    // Durante a navegação, as consultas usam o formato e as dimensões do collider real.
    private const float RaioColisaoFallback = 0.15f;
    // Usa as dimensões reais do collider, sem margem adicional na navegação.
    private const float FatorEscalaColisaoNavegacao = 1.0f;
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
    private static int falhasConsecutivasRota;
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
    private const float TempoParaTrocarAlvoTravado = 6.0f;
    private const float DeslocamentoMinimoMonitoramento = 0.06f;
    private const float DistanciaDeslizamentoParede = 0.65f;
    private const float PassoAngularDeslizamento = 15f;
    private static Vector2 direcaoDeslizamentoCache;
    private static Vector2 destinoDeslizamentoCache;
    private static float proximaReavaliacaoDeslizamento;
    private const float IntervaloReavaliacaoDeslizamento = 0.20f;
    private static bool emManobraDestravamento;
    private static Vector2 pontoDestravamento;
    private static float limiteManobraDestravamento;

    public override string Nome => "NPC Assassino";

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
        inteligencia.Resetar();
        jogadoresParaIA.Clear();
        inteligencia.DefinirEstado(EstadoIAAssassino.Inicializando);

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
                    PararCorpo(seguidor.NetTransform.body, "AoSairDaRoleReveal (linha 230)");
            }
            catch { }

            // Mesmo fluxo em etapas do Raio/Tsunami:
            // criar localmente -> Spawn -> associar NetId/PlayerId -> aparência.
            etapaInicializacao = 1;
            proximaEtapa = Time.realtimeSinceStartup + 0.12f;
            proximaBuscaAlvo = 0f;
            proximoKillNpc = 0f;
            proximoTransporte = 0f;
            proximoSnapToRede = 0f;
            sequenciaSnapToRede = 0;
            sequenciaSnapToRedeInicializada = false;
            transporteEmAndamento = false;
            alvoAtual = null;
            inteligencia.DefinirEstado(EstadoIAAssassino.Inicializando);
            LimparCaminho();

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Role Reveal encerrada. NPC ASSASSINO criado; iniciando sincronização. PlayerId=" +
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

    // Força a serialização do CustomNetworkTransform do NPC pelo fluxo nativo
    // (Data update), em vez de enviar SnapTo que teleporta e zera a velocidade.
    private static float proximaAtualizacaoTransformVanilla;
    private const float IntervaloAtualizacaoTransformVanilla = 0.10f;

    private static void SincronizarTransformVanilla()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            seguidor?.NetTransform == null ||
            AmongUsClient.Instance == null ||
            transporteEmAndamento || aguardandoSaidaVent)
            return;

        float agora = Time.realtimeSinceStartup;
        if (agora < proximaAtualizacaoTransformVanilla)
            return;
        proximaAtualizacaoTransformVanilla = agora + IntervaloAtualizacaoTransformVanilla;

        try
        {
            var transformacao = seguidor.NetTransform;
            Type tipo = transformacao.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // InnerNetObject.SetDirtyBit marca o objeto para que o host
            // serialize LastSequenceId, TargetPosition e TargetVelocity.
            MethodInfo setDirty = null;
            for (Type atual = tipo; atual != null && setDirty == null; atual = atual.BaseType)
            {
                setDirty = atual.GetMethods(flags | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "SetDirtyBit" &&
                        m.GetParameters().Length == 1 &&
                        (m.GetParameters()[0].ParameterType == typeof(uint) ||
                         m.GetParameters()[0].ParameterType == typeof(int)));
            }

            if (setDirty == null)
            {
                if (!avisoSyncTransformVanilla)
                {
                    avisoSyncTransformVanilla = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] SetDirtyBit não encontrado no CustomNetworkTransform; movimento vanilla pode não sincronizar.");
                }
                return;
            }

            Type parametro = setDirty.GetParameters()[0].ParameterType;
            setDirty.Invoke(transformacao, new object[] { parametro == typeof(int) ? 1 : 1u });
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao marcar transform nativo para sincronização: " + ex.Message);
        }
    }

    private static bool avisoSyncTransformVanilla;

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

    // O host envia snapshots de destino aos clientes com mod.
    // Os clientes nunca substituem esse ponto pela posição atual do jogador:
    // assim também reproduzem a investigação da última posição observada.
    private static void EnviarDestinoAosClientesComMod(PlayerControl alvo, Vector2? destinoForcado = null)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            seguidor == null ||
            PlayerControl.LocalPlayer == null ||
            AmongUsClient.Instance == null)
            return;

        byte playerIdAlvo = alvo != null ? alvo.PlayerId : byte.MaxValue;
        Vector2 destino = destinoForcado ?? (alvo != null ? alvo.GetTruePosition() : seguidor.GetTruePosition());

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

            ultimoAlvoReplicado = playerIdAlvo;
            ultimoDestinoReplicado = destino;
            destinoReplicadoInicializado = true;
            proximaReplicacaoDestino = Time.realtimeSinceStartup + IntervaloReplicacaoDestino;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[F7/Seguidor] Destino enviado por RPC aos clientes com mod: alvo={playerIdAlvo}, posição={destino}, clientes={enviados}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao enviar destino aos clientes com mod: " + ex.Message);
        }
    }

    private static void AtualizarDestinoReplicado(PlayerControl alvo, Vector2 destino)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        byte playerId = alvo != null ? alvo.PlayerId : byte.MaxValue;
        float agora = Time.realtimeSinceStartup;

        bool mudouAlvo = !destinoReplicadoInicializado ||
            playerId != ultimoAlvoReplicado;
        bool mudouDestino = !destinoReplicadoInicializado ||
            Vector2.Distance(destino, ultimoDestinoReplicado) >= DistanciaMinimaReplicacaoDestino;
        bool heartbeat = agora >= proximaReplicacaoDestino;

        if (mudouAlvo || mudouDestino || heartbeat)
            EnviarDestinoAosClientesComMod(alvo, destino);
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
        if (!npc.moveable || npc.onLadder || npc.inMovingPlat ||
            npc.inVent || npc.walkingToVent)
        {
            PararCorpo(npc.NetTransform.body, "AtualizarCliente (linha 632)");
            return;
        }

        if (alvoReplicadoCliente == byte.MaxValue)
        {
            Vector2 destinoPatrulhaCliente = destinoReplicadoCliente;
            if (Vector2.Distance(npc.GetTruePosition(), destinoPatrulhaCliente) <= DistanciaChegadaPatrulha)
            {
                PararCorpo(npc.NetTransform.body, "AtualizarCliente (linha 641)");
                LimparCaminho();
            }
            else
            {
                // Quando não há alvo, o destino replicado representa a sala
                // que o NPC está visitando durante a patrulha.
                MoverAte(destinoPatrulhaCliente);
            }
            return;
        }

        // O cliente nunca consulta a posição atual do alvo para decidir a rota.
        // O host envia snapshots: posição atual quando há visão e última posição
        // conhecida durante a investigação. Isso impede perseguição através de paredes.
        PlayerControl alvoIgnorado = null;
        if (alvoReplicadoCliente != byte.MaxValue)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador != null && jogador != npc &&
                    jogador.PlayerId == alvoReplicadoCliente)
                {
                    alvoIgnorado = jogador;
                    break;
                }
            }
        }

        Vector2 destino = destinoReplicadoCliente;
        if (Vector2.Distance(npc.GetTruePosition(), destino) <= 0.18f)
        {
            PararCorpo(npc.NetTransform.body, "AtualizarCliente (destino replicado)");
            LimparCaminho();
        }
        else
        {
            alvoIgnoradoNavegacao = alvoIgnorado;
            try
            {
                MoverAte(destino);
            }
            finally
            {
                alvoIgnoradoNavegacao = null;
            }
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
                PararCorpo(jogador.NetTransform.body, "PararMovimentoCliente (linha 694)");
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

        if (aguardandoFimRoleReveal || seguidor == null)
            return;

        AtualizarRenderizacaoCaminho();

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
                instanteSpawnNpc = Time.realtimeSinceStartup;
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

                // Mantém o nome especial somente até completar 1 segundo
                // desde o envio do Spawn. A etapa seguinte reaplica os cosméticos
                // com o nome vazio para não deixar NPC ASSASSINO nas reuniões.
                etapaInicializacao = 4;
                proximaEtapa = Mathf.Max(
                    Time.realtimeSinceStartup,
                    instanteSpawnNpc + 1f);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Nome temporário aplicado; será removido 1s após o Spawn.");
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
                    string.Empty,
                    0,
                    "hat_pk02_TenGallonHat",
                    "skin_Wall",
                    "pet_EmptyPet",
                    "visor_EmptyVisor");

                etapaInicializacao = 5;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Nome NPC ASSASSINO removido 1s após o Spawn; cosméticos reaplicados.");
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

        // Não enviar SnapTo manual periódico aos clientes vanilla.
        // O CustomNetworkTransform do NPC já replica o movimento contínuo do Rigidbody2D.
        // Um RPC SnapTo paralelo com sequência artificial disputa com a sequência nativa
        // do transform e pode fazer clientes remotos aplicarem posições incorretas.

        // Atualiza o fluxo de dados nativo do transform para todos os clientes vanilla.
        SincronizarTransformVanilla();

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

        // IA direta: seleciona e persegue somente o jogador válido mais próximo.
        PlayerControl alvo = ObterAlvoAtual();
        Vector2 origem = seguidor.GetTruePosition();

        if (alvo == null)
        {
            PararCorpo(seguidor.NetTransform.body, "Atualizar (sem jogador visível)");
            alvoAtual = null;
            AtualizarDestinoReplicado(null, origem);
            EnviarDestinoAosClientesComMod(null, origem);
            inteligencia.DefinirEstado(EstadoIAAssassino.Aguardando);
            LimparCaminho();
            return;
        }

        // O centro visual evita que a IA persiga apenas o ponto dos pés.
        // Para caminhar, convertemos o centro visual novamente para o nível do chão.
        Vector2 centroAlvo = ObterCentroVisualJogador(alvo);
        Vector2 destino = ObterPontoDePerseguicao(alvo, centroAlvo);
        AtualizarDestinoReplicado(alvo, destino);

        // Dutos são considerados também durante a perseguição (antes só eram
        // processados na patrulha, por isso o NPC podia nunca tentar entrar).
        if (TentarUsarVentilacao(destino))
        {
            inteligencia.DefinirEstado(EstadoIAAssassino.Transportando);
            return;
        }

        // Mantém as travessias de mapa necessárias para alcançar o jogador.
        if (TentarUsarEscadaOuTirolesa(destino))
        {
            inteligencia.DefinirEstado(EstadoIAAssassino.Transportando);
            return;
        }

        Vector2 centroNpc = ObterCentroVisualJogador(seguidor);
        float distanciaCentros = Vector2.Distance(centroNpc, centroAlvo);
        if (distanciaCentros <= RaioHitboxVirtualNpc &&
            PodeAtacarJogador(alvo, centroNpc))
        {
            PararCorpo(seguidor.NetTransform.body, "Atualizar (hitbox virtual em contato)");
            if (MatarAlvo(alvo))
            {
                inteligencia.EsquecerAlvo();
                alvoAtual = null;
                proximaBuscaAlvo = 0f;
                EnviarDestinoAosClientesComMod(null, origem);
                return;
            }
            // Se o RPC/cooldown falhar, mantém a perseguição e tenta novamente.
        }

        alvoIgnoradoNavegacao = alvo;
        try
        {
            MoverAte(destino);
        }
        finally
        {
            alvoIgnoradoNavegacao = null;
        }
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

        // Primeiro percorre a classe concreta e suas classes-base.
        // Algumas implementações do jogo expõem membros herdados ou privados.
        for (Type atual = tipo; atual != null; atual = atual.BaseType)
        {
            foreach (string nome in nomes)
            {
                PropertyInfo propriedade = atual.GetProperty(
                    nome, flags | BindingFlags.DeclaredOnly);
                if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
                    return propriedade.GetValue(instancia);

                FieldInfo campo = atual.GetField(
                    nome, flags | BindingFlags.DeclaredOnly);
                if (campo != null)
                    return campo.GetValue(instancia);
            }
        }

        // IsActive e outros membros podem estar declarados explicitamente
        // em interfaces (por exemplo, ISystemType), não na classe concreta.
        foreach (Type interfaceTipo in tipo.GetInterfaces())
        {
            foreach (string nome in nomes)
            {
                PropertyInfo propriedade = interfaceTipo.GetProperty(nome, flags);
                if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
                    return propriedade.GetValue(instancia);
            }
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
    private static bool TentarUsarVentilacao(Vector2 destino)
    {
        if (YouTubeVoteBridgePlugin.PermitirVentilacaoNPC == null ||
            !YouTubeVoteBridgePlugin.PermitirVentilacaoNPC.Value ||
            seguidor == null || seguidor.MyPhysics == null ||
            seguidor.NetTransform?.body == null)
            return false;

        float agora = Time.realtimeSinceStartup;

        if (aguardandoSaidaVent)
        {
            PararCorpo(seguidor.NetTransform.body, "TentarUsarVentilacao (linha 1158)");

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
        Vector2 posAlvo = destino;
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

        PararCorpo(seguidor.NetTransform.body, "TentarUsarVentilacao (linha 1271)");

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

    private static bool TentarUsarEscadaOuTirolesa(Vector2 posAlvo)
    {
        if (seguidor == null || seguidor.MyPhysics == null ||
            seguidor.NetTransform?.body == null || ShipStatus.Instance == null)
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
                    PararCorpo(seguidor.NetTransform.body, "TentarUsarEscadaOuTirolesa (linha 1323)");
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
        float distanciaDireta = Vector2.Distance(origem, posAlvo);
        float melhorCusto = float.MaxValue;
        Ladder melhorEscada = null;
        ZiplineConsole melhorTirolesa = null;
        PlatformConsole melhorConsolePlataforma = null;
        MovingPlatformBehaviour melhorPlataforma = null;
        Vector2 pontoSaidaTransporte = Vector2.zero;

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
            melhorConsolePlataforma = null;
            melhorPlataforma = null;
            pontoSaidaTransporte = saida;
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
            // landingPositionBottom/Top.position já está em coordenadas mundiais.
            // TransformPoint aplicaria a transformação da tirolesa uma segunda vez.
            Vector2 saida = new Vector2(pontoPouso.x, pontoPouso.y);
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
            melhorConsolePlataforma = null;
            pontoSaidaTransporte = saida;
            melhorPlataforma = null;
        }

        // Airship: trata os dois consoles da plataforma como entrada e saída.
        // Só usa a travessia quando o outro lado reduz o custo total da perseguição.
        PlatformConsole[] consolesPlataforma =
            ShipStatus.Instance.GetComponentsInChildren<PlatformConsole>();
        foreach (PlatformConsole console in consolesPlataforma)
        {
            if (console == null || !console.isActiveAndEnabled ||
                console.Platform == null || !console.Platform.isActiveAndEnabled ||
                console.Platform.InUse)
                continue;

            PlatformConsole outraConsole = consolesPlataforma
                .Where(c => c != null && c != console &&
                    c.isActiveAndEnabled && c.Platform == console.Platform)
                .OrderBy(c => Vector2.Distance(c.transform.position, posAlvo))
                .FirstOrDefault();

            if (outraConsole == null)
                continue;

            float distanciaSaida = Vector2.Distance(
                outraConsole.transform.position, posAlvo);
            float custo = Vector2.Distance(origem, console.transform.position) +
                distanciaSaida;

            if (distanciaSaida >= distanciaDireta - 0.75f ||
                custo >= distanciaDireta - 0.25f ||
                custo >= melhorCusto)
                continue;

            melhorCusto = custo;
            melhorConsolePlataforma = console;
            melhorPlataforma = console.Platform;
            melhorEscada = null;
            melhorTirolesa = null;
            pontoSaidaTransporte = outraConsole.transform.position;
        }

        if (melhorEscada == null && melhorTirolesa == null &&
            melhorConsolePlataforma == null)
            return false;

        Vector2 pontoEntrada = melhorEscada != null
            ? (Vector2)melhorEscada.transform.position
            : melhorTirolesa != null
                ? (Vector2)melhorTirolesa.transform.position
                : (Vector2)melhorConsolePlataforma.transform.position;

        string tipoTransporte = melhorEscada != null
            ? "escada"
            : melhorTirolesa != null
                ? "tirolesa"
                : "plataforma do Airship";
        float alcance = melhorEscada != null
            ? melhorEscada.UsableDistance
            : melhorTirolesa != null
                ? melhorTirolesa.UsableDistance
                : 1.5f;

        float distanciaEntrada = Vector2.Distance(origem, pontoEntrada);
        if (distanciaEntrada > alcance)
        {
            MoverAte(pontoEntrada);
            return true;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Transporte] NPC alcançou a entrada da " + tipoTransporte +
            ". Origem=" + origem +
            ", entrada=" + pontoEntrada +
            ", saída estimada=" + pontoSaidaTransporte +
            ", alvo=" + posAlvo +
            ", distância=" + distanciaEntrada +
            ", alcance=" + alcance + ".");

        PararCorpo(seguidor.NetTransform.body, "TentarUsarEscadaOuTirolesa (entrada de transporte)");

        try
        {
            if (melhorEscada != null)
            {
                melhorEscada.CanUse(seguidor.Data, out bool permitidoEscada, out _);
                bool escadaEmCooldown = melhorEscada.IsCoolingDown();
                if (!permitidoEscada || escadaEmCooldown)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Transporte] Escada encontrada, mas não pode ser usada. CanUse=" +
                        permitidoEscada + ", cooldown=" + escadaEmCooldown +
                        ", posição NPC=" + origem + ", entrada=" + pontoEntrada + ".");
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

            if (melhorTirolesa != null)
            {
                melhorTirolesa.CanUse(seguidor.Data, out bool permitidoTirolesa, out _);
                bool tirolesaEmCooldown = melhorTirolesa.IsCoolingDown();
                if (!permitidoTirolesa || tirolesaEmCooldown)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Transporte] Tirolesa encontrada, mas não pode ser usada. CanUse=" +
                        permitidoTirolesa + ", cooldown=" + tirolesaEmCooldown +
                        ", console=" + pontoEntrada + ", posição NPC=" + origem + ".");
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

            if (melhorConsolePlataforma != null)
            {
                melhorConsolePlataforma.CanUse(
                    seguidor.Data, out bool permitidoPlataforma, out _);

                if (!permitidoPlataforma || melhorPlataforma == null ||
                    melhorPlataforma.InUse)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Transporte] Plataforma encontrada, mas não pode ser usada. CanUse=" +
                        permitidoPlataforma + ", plataforma nula=" + (melhorPlataforma == null) +
                        ", em uso=" + (melhorPlataforma != null && melhorPlataforma.InUse) +
                        ", console=" + pontoEntrada + ", posição NPC=" + origem + ".");
                    proximoTransporte = agora + 0.5f;
                    return false;
                }

                seguidor.RpcUsePlatform();
                transporteEmAndamento = true;
                limiteTransporte = agora + 20f;
                proximoTransporte = agora + 1f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC iniciou travessia da plataforma móvel do Airship.");
                return true;
            }
        }
        catch (Exception ex)
        {
            transporteEmAndamento = false;
            proximoTransporte = agora + 1f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Transporte] Falha ao iniciar " + tipoTransporte +
                ": " + ex);
            return false;
        }

        // Nenhum transporte aplicável foi iniciado. A IA pode continuar
        // normalmente pelo caminho calculado pelo A*.
        return false;
    }

    /// <summary>
    /// Movimento do NPC baseado em waypoints do novo pathfinder.
    /// Recalcula a rota quando o destino muda ou o próximo segmento fica bloqueado.
    /// </summary>
    // Centraliza toda interrupção de movimento. O log só aparece quando
    // alguém realmente troca uma velocidade não-zero por zero, identificando
    // o método responsável em vez de inundar o log a cada frame.
    private static void PararCorpo(Rigidbody2D corpo, string motivo)
    {
        if (corpo == null)
            return;

        Vector2 velocidadeAnterior = corpo.velocity;
        if (velocidadeAnterior.sqrMagnitude > 0.0001f)
        {
            Vector2 posicao = corpo.position;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Movimento] VELOCIDADE ZERADA por " + motivo +
                " | velocidade anterior=" + velocidadeAnterior +
                " | posição=" + posicao +
                " | alvo=" + (alvoAtual != null ? alvoAtual.PlayerId.ToString() : "nenhum") +
                " | patrulha=" + possuiDestinoPatrulha +
                " | sabotagemLuzes=" + resolvendoSabotagemLuzes +
                " | outraSabotagem=" + resolvendoOutraSabotagem +
                " | transporte=" + transporteEmAndamento +
                " | vent=" + aguardandoSaidaVent);
        }

        corpo.velocity = Vector2.zero;
    }

    private static void MoverAte(Vector2 destino)
    {
        if (seguidor?.NetTransform?.body == null)
            return;

        Vector2 origem = seguidor.GetTruePosition();
        float agora = Time.realtimeSinceStartup;
        var corpo = seguidor.NetTransform.body;

        // Chegou ao destino: encerra somente a navegação, sem manter estados
        // de travamento que possam bloquear a próxima ordem de movimento.
        if (Vector2.Distance(origem, destino) <= 0.18f)
        {
            PararCorpo(corpo, "MoverAte (linha 1478)");
            LimparCaminho();
            monitoramentoMovimentoIniciado = false;
            avisoMonitoramentoMovimento = false;
            return;
        }

        // Uma mudança real de destino invalida imediatamente a rota anterior.
        // Isso evita que o NPC continue tentando seguir um waypoint calculado
        // para a posição antiga do jogador/sabotagem.
        bool destinoMudou = Vector2.Distance(destino, destinoCaminhoAnterior) >= DistanciaRecalculoAlvo;
        if (destinoMudou)
        {
            caminhoAtual.Clear();
            indiceCaminho = 0;
            proximoRecalculoCaminho = 0f;
        }

        bool caminhoVazio = caminhoAtual == null || caminhoAtual.Count == 0;
        bool aguardandoNovaTentativa = caminhoVazio &&
            agora < proximoRecalculoCaminho;

        bool precisaRecalcular =
            (!aguardandoNovaTentativa && caminhoVazio) ||
            (!caminhoVazio && indiceCaminho >= caminhoAtual.Count) ||
            (!caminhoVazio && agora >= proximoRecalculoCaminho) ||
            destinoMudou;

        // Se o próximo segmento ficou bloqueado por uma porta, jogador ou
        // objeto móvel, descarta a rota e procura outra imediatamente.
        if (!precisaRecalcular && indiceCaminho < caminhoAtual.Count &&
            !PodeMoverEntre(origem, caminhoAtual[indiceCaminho]))
            precisaRecalcular = true;

        if (precisaRecalcular)
        {
            List<Vector2> novaRota = CalcularCaminhoAStar(origem, destino);
            destinoCaminhoAnterior = destino;
            proximoRecalculoCaminho = agora + IntervaloRecalculoCaminho;

            if (novaRota == null || novaRota.Count == 0)
            {
                // A rota anterior já não é válida. Esconde-a antes de tentar
                // abrir uma porta, para não continuar exibindo um trajeto velho.
                caminhoAtual.Clear();
                indiceCaminho = 0;
                AtualizarRenderizacaoCaminho();

                if (TentarAbrirPortaNoCaminho(origem, destino, agora))
                    return;

                // Se o A* falhar temporariamente (por exemplo, ao contornar
                // uma quina ou uma porta), tenta um passo curto cuja colisão
                // também é validada. O A* continua sendo recalculado em seguida.
                if (TentarDesvioLocal(origem, destino, corpo, agora))
                {
                    proximoRecalculoCaminho = agora + 0.18f;
                    avisoMonitoramentoMovimento = false;
                    return;
                }

                proximoRecalculoCaminho = agora + 0.35f;
                PararCorpo(corpo, "MoverAte sem rota A* e sem desvio seguro");

                if (!avisoMonitoramentoMovimento)
                {
                    avisoMonitoramentoMovimento = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Pathfinder] A* sem rota; NPC parado aguardando novo cálculo. Origem=" +
                        origem + ", destino=" + destino + ".");
                }

                if (possuiDestinoPatrulha &&
                    Vector2.Distance(destino, destinoPatrulha) < 0.20f)
                {
                    falhasConsecutivasRota++;
                    if (falhasConsecutivasRota >= 2)
                    {
                        falhasConsecutivasRota = 0;
                        TentarTrocarDestinoPatrulha(origem);
                    }
                }

                return;
            }

            falhasConsecutivasRota = 0;
            avisoMonitoramentoMovimento = false;
            caminhoAtual = novaRota;
            indiceCaminho = 0;
        }

        // Quando o A* falha, TentarDesvioLocal aplica uma velocidade válida
        // e agenda outra tentativa em 0,18s. Como não existe waypoint global
        // nesse intervalo, precisamos reaplicar o desvio local em cada chamada.
        // Antes, este bloco zerava a velocidade no frame seguinte, anulando
        // imediatamente o próprio fallback e deixando o NPC parado.
        if (caminhoAtual == null || caminhoAtual.Count == 0)
        {
            if (agora < proximoRecalculoCaminho &&
                TentarDesvioLocal(origem, destino, corpo, agora))
            {
                AtualizarRenderizacaoCaminho();
                return;
            }

            PararCorpo(corpo, "MoverAte aguardando nova rota sem desvio seguro");
            AtualizarRenderizacaoCaminho();
            return;
        }

        // Avança por todos os waypoints já alcançados, inclusive quando o
        // NPC passa por mais de um deles entre duas atualizações.
        while (indiceCaminho < caminhoAtual.Count &&
               Vector2.Distance(origem, caminhoAtual[indiceCaminho]) <= 0.20f)
            indiceCaminho++;

        if (indiceCaminho >= caminhoAtual.Count)
        {
            PararCorpo(corpo, "MoverAte (linha 1548)");
            if (Vector2.Distance(origem, destino) > 0.28f)
            {
                LimparCaminho();
                proximoRecalculoCaminho = agora + 0.08f;
            }
            AtualizarRenderizacaoCaminho();
            return;
        }

        Vector2 waypoint = caminhoAtual[indiceCaminho];
        Vector2 deslocamento = waypoint - origem;
        if (deslocamento.sqrMagnitude <= 0.0004f)
        {
            indiceCaminho++;
            AtualizarRenderizacaoCaminho();
            return;
        }

        // A checagem é feita no segmento completo até o waypoint. Se algo
        // obstruir o trajeto, não fica preso tentando seguir o mesmo ponto.
        if (!PodeMoverEntre(origem, waypoint))
        {
            // O A* pode retornar uma rota que atravessa uma porta fechada.
            // Antes de descartar o caminho, tenta abrir a porta que bloqueia o trajeto.
            if (TentarAbrirPortaNoCaminho(origem, destino, agora))
            {
                LimparCaminho();
                AtualizarRenderizacaoCaminho();
                return;
            }

            PararCorpo(corpo, "MoverAte (segmento bloqueado)");
            LimparCaminho();
            proximoRecalculoCaminho = agora + 0.08f;
            AtualizarRenderizacaoCaminho();
            return;
        }

        // A velocidade é reaplicada imediatamente no primeiro waypoint da
        // rota nova. Se o NPC estava parado quando recebeu outro destino,
        // registra a recuperação para confirmar que a navegação foi retomada.
        Vector2 velocidadeAnterior = corpo.velocity;
        corpo.velocity = deslocamento.normalized * Velocidade;

        if (velocidadeAnterior.sqrMagnitude <= 0.0001f)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Movimento] NPC estava parado; rota válida encontrada e velocidade retomada. " +
                "Destino atual=" + destino +
                " | waypoint=" + waypoint +
                " | velocidade aplicada=" + corpo.velocity + ".");
        }
        AtualizarRenderizacaoCaminho();
        monitoramentoMovimentoIniciado = true;
        ultimaPosicaoMonitorada = origem;
        inicioSemDeslocamento = agora;
        avisoMonitoramentoMovimento = false;
    }

    private static bool PortaEstaNaRota(
        Vector2 origem, Vector2 destino, Collider2D colisor)
    {
        if (colisor == null || !colisor.enabled)
            return false;

        Vector2 delta = destino - origem;
        float distancia = delta.magnitude;
        if (distancia <= 0.001f)
            return false;

        Vector2 direcao = delta / distancia;
        Vector2 pontoProximo = colisor.ClosestPoint(origem);
        if (Vector2.Distance(origem, pontoProximo) > 1.5f)
            return false;

        Vector2 centro = colisor.bounds.center;
        float distanciaNaRota = Vector2.Dot(centro - origem, direcao);
        if (distanciaNaRota < 0f || distanciaNaRota > distancia + 0.75f)
            return false;

        Vector2 pontoNaRota = origem + direcao * distanciaNaRota;
        return Vector2.Distance(pontoNaRota, centro) <= 0.70f;
    }

    private static bool TentarAbrirPortaNoCaminho(
        Vector2 origem, Vector2 destino, float agora)
    {
        if (seguidor == null || ShipStatus.Instance == null ||
            agora < proximaTentativaAbrirPorta)
            return false;

        if ((destino - origem).sqrMagnitude <= 0.001f)
            return false;

        // Portas de descontaminação não usam o sistema Doors normal.
        // Segue o fluxo da API de navegação: abre pelo lado de dentro ou de fora
        // e deixa o ciclo vanilla terminar antes de tentar atravessar.
        DeconSystem[] sistemasDecon =
            ShipStatus.Instance.GetComponentsInChildren<DeconSystem>();
        foreach (DeconSystem sistema in sistemasDecon)
        {
            if (sistema == null ||
                sistema.CurState != DeconSystem.States.Idle)
                continue;

            Collider2D colisorSuperior =
                LerMembro(sistema.UpperDoor, "myCollider", "MyCollider") as Collider2D;

            if (PortaEstaNaRota(origem, destino, colisorSuperior))
            {
                bool dentro = sistema.RoomArea != null &&
                    sistema.RoomArea.OverlapPoint(origem);

                if (dentro)
                    sistema.OpenFromInside(true);
                else
                    sistema.OpenDoor(true);

                proximaTentativaAbrirPorta = agora + 0.75f;
                PararCorpo(seguidor.NetTransform.body,
                    "TentarAbrirPortaNoCaminho (descontaminação superior)");

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC solicitou abertura da porta superior de descontaminação.");
                return true;
            }

            Collider2D colisorInferior =
                LerMembro(sistema.LowerDoor, "myCollider", "MyCollider") as Collider2D;

            if (PortaEstaNaRota(origem, destino, colisorInferior))
            {
                bool dentro = sistema.RoomArea != null &&
                    sistema.RoomArea.OverlapPoint(origem);

                if (dentro)
                    sistema.OpenFromInside(false);
                else
                    sistema.OpenDoor(false);

                proximaTentativaAbrirPorta = agora + 0.75f;
                PararCorpo(seguidor.NetTransform.body,
                    "TentarAbrirPortaNoCaminho (descontaminação inferior)");

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC solicitou abertura da porta inferior de descontaminação.");
                return true;
            }
        }

        var portas = ShipStatus.Instance.AllDoors;
        if (portas == null)
            return false;

        for (int i = 0; i < portas.Length; i++)
        {
            OpenableDoor porta = portas[i];
            if (porta == null || !porta.isActiveAndEnabled || porta.IsOpen)
                continue;

            bool portaDecon = sistemasDecon.Any(sistema =>
                sistema != null &&
                (ReferenceEquals(sistema.UpperDoor, porta) ||
                 ReferenceEquals(sistema.LowerDoor, porta)));
            if (portaDecon)
                continue;

            Collider2D colisor = LerMembro(porta, "myCollider", "MyCollider") as Collider2D;
            if (!PortaEstaNaRota(origem, destino, colisor))
                continue;

            try
            {
                object sistemaPortas = ShipStatus.Instance.Systems != null &&
                    ShipStatus.Instance.Systems.ContainsKey(SystemTypes.Doors)
                    ? ShipStatus.Instance.Systems[SystemTypes.Doors]
                    : null;

                bool portasAutomaticas = sistemaPortas != null &&
                    sistemaPortas.GetType().Name.IndexOf(
                        "AutoDoorsSystemType", StringComparison.OrdinalIgnoreCase) >= 0;

                if (portasAutomaticas)
                {
                    porta.SetDoorway(true);

                    FieldInfo dirtyBits = sistemaPortas.GetType().GetField(
                        "dirtyBits",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (dirtyBits != null && dirtyBits.FieldType == typeof(uint))
                    {
                        uint bits = (uint)dirtyBits.GetValue(sistemaPortas);
                        dirtyBits.SetValue(sistemaPortas, bits | (1u << i));
                    }
                }
                else
                {
                    if (porta.Id < 0 || porta.Id > 31)
                        continue;

                    ShipStatus.Instance.UpdateSystem(
                        SystemTypes.Doors, seguidor, (byte)(porta.Id | 64));
                }

                proximaTentativaAbrirPorta = agora + 0.75f;
                PararCorpo(seguidor.NetTransform.body,
                    "TentarAbrirPortaNoCaminho");

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC solicitou abertura da porta " +
                    porta.Id + " que bloqueava a rota.");
                return true;
            }
            catch (Exception ex)
            {
                proximaTentativaAbrirPorta = agora + 1f;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Falha ao abrir porta " +
                    porta.Id + ": " + ex.Message);
                return false;
            }
        }

        return false;
    }

    private static bool TentarDesvioLocal(
        Vector2 origem, Vector2 destino, Rigidbody2D corpo, float agora)
    {
        if (corpo == null)
            return false;

        Vector2 direcao = ObterDirecaoComDeslizamento(origem, destino);
        if (direcao.sqrMagnitude > 0.0001f)
        {
            float corredorLivre = TestarCorredorDeslizamento(
                origem, direcao, 0.70f);

            if (corredorLivre >= 0.20f)
            {
                corpo.velocity = direcao.normalized * Velocidade * 0.68f;
                monitoramentoMovimentoIniciado = true;
                ultimaPosicaoMonitorada = origem;
                inicioSemDeslocamento = agora;
                return true;
            }
        }

        // Último recurso: procura um pequeno passo seguro em qualquer direção
        // que não atravesse paredes. O A* continuará tentando uma rota completa.
        if (TentarEncontrarPontoDestravamento(origem, destino, out Vector2 pontoSeguro))
        {
            Vector2 deslocamento = pontoSeguro - origem;
            if (deslocamento.sqrMagnitude > 0.0001f &&
                PodeMoverEntre(origem, pontoSeguro))
            {
                corpo.velocity = deslocamento.normalized * Velocidade * 0.55f;
                monitoramentoMovimentoIniciado = true;
                ultimaPosicaoMonitorada = origem;
                inicioSemDeslocamento = agora;
                return true;
            }
        }

        return false;
    }

    private static bool TentarTrocarDestinoPatrulha(Vector2 origem)
    {
        if (seguidor?.NetTransform?.body == null || ShipStatus.Instance == null)
            return false;

        var salas = ShipStatus.Instance.AllRooms
            .Where(sala => sala != null && sala.roomArea != null)
            .Where(sala => sala.RoomId != ultimaSalaPatrulha)
            .OrderBy(_ => UnityEngine.Random.value)
            .ToList();

        // Se não houver outra sala, permite tentar novamente a sala anterior.
        if (salas.Count == 0)
        {
            salas = ShipStatus.Instance.AllRooms
                .Where(sala => sala != null && sala.roomArea != null)
                .OrderBy(_ => UnityEngine.Random.value)
                .ToList();
        }

        foreach (var sala in salas)
        {
            Bounds limites = sala.roomArea.bounds;
            Vector2 centroSala = limites.center;

            if (!sala.roomArea.OverlapPoint(centroSala) ||
                !TentarEncontrarPontoLivreNaSala(limites, centroSala, out Vector2 pontoLivre) ||
                Vector2.Distance(origem, pontoLivre) < DistanciaChegadaPatrulha + 0.5f)
                continue;

            List<Vector2> caminhoTeste = CalcularCaminhoAStar(origem, pontoLivre);
            if (caminhoTeste == null || caminhoTeste.Count == 0)
                continue;

            destinoPatrulha = pontoLivre;
            possuiDestinoPatrulha = true;
            ultimaSalaPatrulha = sala.RoomId;
            possuiUltimaSalaPatrulha = true;

            caminhoAtual = caminhoTeste;
            indiceCaminho = 0;
            destinoCaminhoAnterior = pontoLivre;
            proximoRecalculoCaminho =
                Time.realtimeSinceStartup + IntervaloRecalculoCaminho;
            falhasConsecutivasRota = 0;
            monitoramentoMovimentoIniciado = false;
            avisoMonitoramentoMovimento = false;

            EnviarDestinoAosClientesComMod(null, destinoPatrulha);

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Destino de patrulha sem rota. Trocando para a sala " +
                sala.RoomId + " com caminho A* válido: " + pontoLivre + ".");
            return true;
        }

        // Não deixa uma velocidade antiga continuar empurrando o NPC contra
        // o obstáculo enquanto aguarda a próxima tentativa de destino.
        PararCorpo(seguidor.NetTransform.body, "TentarTrocarDestinoPatrulha (linha 1640)");
        proximaTentativaDestinoPatrulha =
            Time.realtimeSinceStartup + 0.50f;

        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "[F7/Seguidor] Não encontrou outra sala com caminho A* válido; " +
            "tentará escolher um novo destino novamente.");
        return false;
    }

    /// <summary>
    /// Faz o NPC circular pelo mapa quando não há jogadores válidos para perseguir.
    /// Os destinos são sorteados dentro das salas e a movimentação usa o mesmo A*,
    /// as mesmas verificações de colisão e o mesmo detector de travamento da perseguição.
    /// </summary>
    private static void Patrulhar()
    {
        inteligencia.DefinirEstado(EstadoIAAssassino.Patrulhando);

        if (seguidor?.NetTransform?.body == null || ShipStatus.Instance == null)
            return;

        var corpo = seguidor.NetTransform.body;

        // Processa primeiro a saída do duto. O estado de transporte não pode
        // interceptar esta etapa e deixar o NPC preso dentro da ventilação.
        if (possuiDestinoPatrulha &&
            TentarUsarVentilacao(destinoPatrulha))
            return;

        // A patrulha também pode usar escadas, tirolesas e a plataforma do Airship.
        if (possuiDestinoPatrulha &&
            TentarUsarEscadaOuTirolesa(destinoPatrulha))
            return;

        // Se uma travessia iniciada durante a perseguição ainda estiver acontecendo,
        // deixa a animação vanilla terminar antes de voltar a andar.
        if (transporteEmAndamento)
        {
            if (seguidor.onLadder || seguidor.inMovingPlat || !seguidor.moveable)
            {
                PararCorpo(corpo, "Patrulhar (linha 1674)");
                return;
            }

            transporteEmAndamento = false;
        }

        Vector2 origem = seguidor.GetTruePosition();
        bool chegouAoDestino = possuiDestinoPatrulha &&
            Vector2.Distance(origem, destinoPatrulha) <= DistanciaChegadaPatrulha;

        // O destino só muda quando o NPC chega à sala escolhida.
        if (!possuiDestinoPatrulha || chegouAoDestino)
        {
            PararCorpo(corpo, "Patrulhar (linha 1688)");
            LimparCaminho();
            emManobraDestravamento = false;
            monitoramentoMovimentoIniciado = false;

            if (Time.realtimeSinceStartup < proximaTentativaDestinoPatrulha)
                return;

            if (!TentarEscolherDestinoPatrulha(origem, out Vector2 novoDestino, out SystemTypes salaEscolhida))
            {
                possuiDestinoPatrulha = false;
                proximaTentativaDestinoPatrulha = Time.realtimeSinceStartup + 1f;
                return;
            }

            destinoPatrulha = novoDestino;
            possuiDestinoPatrulha = true;
            ultimaSalaPatrulha = salaEscolhida;
            possuiUltimaSalaPatrulha = true;
            proximaTentativaDestinoPatrulha = Time.realtimeSinceStartup + 0.25f;

            EnviarDestinoAosClientesComMod(null, destinoPatrulha);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC patrulhando para o centro da sala " +
                salaEscolhida + ": " + destinoPatrulha + ".");
        }

        AtualizarDestinoReplicado(null, destinoPatrulha);
        MoverAte(destinoPatrulha);
    }

    /// <summary>
    /// Escolhe uma sala aleatória e usa seu centro geométrico como destino.
    /// Se o centro estiver ocupado por um objeto, procura o ponto livre mais
    /// próximo dentro da própria sala. O NPC não troca de destino no caminho.
    /// </summary>
    private static bool TentarEscolherDestinoPatrulha(
        Vector2 origem, out Vector2 destino, out SystemTypes salaEscolhida)
    {
        destino = origem;
        salaEscolhida = SystemTypes.Hallway;

        if (ShipStatus.Instance == null || seguidor == null)
            return false;

        var salas = ShipStatus.Instance.AllRooms
            .Where(sala => sala != null && sala.roomArea != null)
            .Where(sala => !possuiUltimaSalaPatrulha || sala.RoomId != ultimaSalaPatrulha)
            .OrderBy(_ => UnityEngine.Random.value)
            .ToArray();

        // Se o mapa só tiver uma sala utilizável, permite escolhê-la novamente.
        if (salas.Length == 0)
        {
            salas = ShipStatus.Instance.AllRooms
                .Where(sala => sala != null && sala.roomArea != null)
                .OrderBy(_ => UnityEngine.Random.value)
                .ToArray();
        }

        foreach (var sala in salas)
        {
            Bounds limites = sala.roomArea.bounds;
            Vector2 centroSala = limites.center;

            if (!sala.roomArea.OverlapPoint(centroSala))
                continue;

            if (!TentarEncontrarPontoLivreNaSala(limites, centroSala, out Vector2 pontoLivre))
                continue;

            if (Vector2.Distance(origem, pontoLivre) < DistanciaChegadaPatrulha + 0.5f)
                continue;

            destino = pontoLivre;
            salaEscolhida = sala.RoomId;
            return true;
        }

        return false;
    }

    private static Vector2 ObterDirecaoComDeslizamento(Vector2 origem, Vector2 destino)
    {
        Vector2 delta = destino - origem;
        if (delta.sqrMagnitude < 0.0001f)
            return Vector2.zero;

        Vector2 desejada = delta.normalized;
        if (PodePassarEntre(origem, destino))
        {
            direcaoDeslizamentoCache = Vector2.zero;
            proximaReavaliacaoDeslizamento = 0f;
            return desejada;
        }

        float agora = Time.realtimeSinceStartup;
        bool destinoSemelhante =
            Vector2.Distance(destino, destinoDeslizamentoCache) < 0.40f;

        // Reaproveita a direção apenas se o corredor continuar realmente livre.
        if (direcaoDeslizamentoCache.sqrMagnitude > 0.001f &&
            agora < proximaReavaliacaoDeslizamento &&
            destinoSemelhante &&
            TestarCorredorDeslizamento(origem, direcaoDeslizamentoCache, 0.70f) >= 0.55f)
            return direcaoDeslizamentoCache;

        var candidatas = new List<Vector2>();

        // Em vez de adivinhar o lado da parede por vários ângulos, usa a
        // normal retornada pela física para calcular a tangente de deslizamento.
        RaycastHit2D[] impactos = ObterColisoesNoDeslocamento(
            origem, desejada, delta.magnitude, Constants.ShipAndAllObjectsMask);

        foreach (RaycastHit2D impacto in impactos.OrderBy(hit => hit.distance))
        {
            Collider2D colisor = impacto.collider;
            if (colisor == null || colisor.isTrigger ||
                colisor.transform.IsChildOf(seguidor.transform))
                continue;

            Vector2 normal = impacto.normal;
            if (normal.sqrMagnitude < 0.0001f)
                continue;

            normal.Normalize();
            Vector2 tangente = desejada - Vector2.Dot(desejada, normal) * normal;

            if (tangente.sqrMagnitude < 0.0001f)
                tangente = new Vector2(-normal.y, normal.x);

            tangente.Normalize();
            candidatas.Add(tangente);
            candidatas.Add(-tangente);

            // Um pequeno componente em direção ao destino evita que o NPC
            // fique apenas raspando paralelamente à parede sem contorná-la.
            Vector2 inclinadaA = (tangente + desejada * 0.25f).normalized;
            Vector2 inclinadaB = (-tangente + desejada * 0.25f).normalized;
            candidatas.Add(inclinadaA);
            candidatas.Add(inclinadaB);

            // A primeira colisão normalmente é a parede que bloqueia a rota.
            // As demais normais podem ser de objetos próximos, então limitamos
            // a análise às duas primeiras superfícies relevantes.
            if (candidatas.Count >= 8)
                break;
        }

        // Fallback para casos em que o cast começa encostado/penetrando
        // levemente em um collider e a física não fornece uma normal útil.
        if (candidatas.Count == 0)
        {
            for (int angulo = 30; angulo <= 150; angulo += 30)
            {
                float radianos = angulo * Mathf.Deg2Rad;
                Vector2 candidata = new Vector2(
                    desejada.x * Mathf.Cos(radianos) - desejada.y * Mathf.Sin(radianos),
                    desejada.x * Mathf.Sin(radianos) + desejada.y * Mathf.Cos(radianos));
                candidatas.Add(candidata);
                candidatas.Add(-candidata);
            }
        }

        Vector2 melhorDirecao = Vector2.zero;
        float melhorPontuacao = float.NegativeInfinity;

        foreach (Vector2 candidataBruta in candidatas)
        {
            if (candidataBruta.sqrMagnitude < 0.0001f)
                continue;

            Vector2 candidata = candidataBruta.normalized;
            float progresso = Vector2.Dot(candidata, desejada);
            if (progresso < -0.20f)
                continue;

            float distanciaLivre = TestarCorredorDeslizamento(
                origem, candidata, 0.90f);
            if (distanciaLivre < 0.20f)
                continue;

            Vector2 pontoFinal = origem + candidata * distanciaLivre;
            float reducaoDistancia = delta.magnitude -
                Vector2.Distance(pontoFinal, destino);

            float continuidade = direcaoDeslizamentoCache.sqrMagnitude > 0.001f
                ? Vector2.Dot(candidata, direcaoDeslizamentoCache.normalized) * 0.12f
                : 0f;

            // Espaço livre é o fator principal. Progresso e continuidade
            // desempatarão caminhos que conseguem contornar a mesma parede.
            float pontuacao =
                distanciaLivre * 1.10f +
                reducaoDistancia * 0.35f +
                progresso * 0.10f +
                continuidade;

            if (pontuacao > melhorPontuacao)
            {
                melhorPontuacao = pontuacao;
                melhorDirecao = candidata;
            }
        }

        direcaoDeslizamentoCache = melhorDirecao;
        destinoDeslizamentoCache = destino;
        proximaReavaliacaoDeslizamento = agora + IntervaloReavaliacaoDeslizamento;
        return melhorDirecao;
    }

    // Retorna o comprimento realmente livre em uma direção, verificando
    // cada trecho com o collider completo do NPC. Interrompe antes da parede.
    private static float TestarCorredorDeslizamento(
        Vector2 origem, Vector2 direcao, float distanciaMaxima)
    {
        if (direcao.sqrMagnitude < 0.0001f)
            return 0f;

        Vector2 pontoAnterior = origem;
        float percorrida = 0f;
        const float passo = 0.20f;
        int quantidade = Mathf.CeilToInt(distanciaMaxima / passo);

        for (int i = 1; i <= quantidade; i++)
        {
            float distancia = Mathf.Min(i * passo, distanciaMaxima);
            Vector2 ponto = origem + direcao.normalized * distancia;

            if (!EstaDentroDosLimitesDoMapa(ponto) ||
                !PodeMoverEntre(pontoAnterior, ponto))
                break;

            percorrida = distancia;
            pontoAnterior = ponto;
        }

        return percorrida;
    }

    private static bool TentarEncontrarPontoDestravamento(
        Vector2 origem, Vector2 destino, out Vector2 melhorPonto)
    {
        melhorPonto = origem;
        float melhorPontuacao = float.NegativeInfinity;
        Vector2 direcaoDestino = (destino - origem).normalized;
        float[] distancias = { 0.65f, 0.45f, 0.30f, 0.20f };

        // Avalia todas as saídas seguras antes de escolher. Assim o NPC não
        // pega simplesmente o primeiro espaço livre: prefere o que abre
        // corredor, aproxima do destino e evita uma segunda quina.
        foreach (float distancia in distancias)
        {
            foreach (Vector2Int vetor in DirecoesCaminho)
            {
                Vector2 direcao = new Vector2(vetor.x, vetor.y).normalized;
                float progresso = Vector2.Dot(direcao, direcaoDestino);
                if (progresso < -0.15f)
                    continue;

                Vector2 candidato = origem + direcao * distancia;
                if (!EstaDentroDosLimitesDoMapa(candidato) ||
                    !PodeMoverEntre(origem, candidato))
                    continue;

                float corredorLivre = TestarCorredorDeslizamento(
                    origem, direcao, distancia);
                if (corredorLivre < distancia * 0.80f)
                    continue;

                float reducaoDistancia =
                    Vector2.Distance(origem, destino) -
                    Vector2.Distance(candidato, destino);

                float pontuacao =
                    progresso * 1.8f +
                    corredorLivre * 1.4f +
                    reducaoDistancia * 0.7f;

                if (pontuacao <= melhorPontuacao)
                    continue;

                melhorPontuacao = pontuacao;
                melhorPonto = candidato;
            }
        }

        return melhorPontuacao > float.NegativeInfinity;
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

        // O novo pathfinder usa uma grade própria e não depende dos caches,
        // pesos ou do sistema de deslizamento da implementação anterior.
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
            {
                limites.Encapsulate(sala.roomArea.bounds);
            }
        }

        if (!encontrouSala)
            return null;

        List<Vector2> caminhoMapeado = PathfinderSeguidorMortal.Calcular(
            inicio,
            destino,
            limites,
            TamanhoCelula,
            ponto => PodeMoverEntre(ponto, ponto) &&
                ExploracaoMapaF8.PodeUsarPontoNavegacao(ponto),
            (origem, fim) => PodePassarEntre(origem, fim) &&
                ExploracaoMapaF8.SegmentoDentroDoMapa(origem, fim),
            ponto => ExploracaoMapaF8.ObterPenalidadeMapa(ponto));

        if (caminhoMapeado != null && caminhoMapeado.Count > 0)
            return caminhoMapeado;

        // Se o snapshot estiver incompleto (por exemplo, um corredor não
        // registrado), tenta novamente sem a restrição das salas mapeadas.
        // A verificação física de colliders continua ativa em cada célula
        // e em cada segmento, então esse fallback não permite atravessar paredes.
        if (ExploracaoMapaF8.MapeamentoNavegacaoDisponivel())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[F7/Seguidor] Nenhuma rota encontrada dentro do mapeamento; " +
                "tentando uma rota física com pequenos desvios fora do snapshot.");

            return PathfinderSeguidorMortal.Calcular(
                inicio,
                destino,
                limites,
                TamanhoCelula,
                ponto => PodeMoverEntre(ponto, ponto),
                (origem, fim) => PodePassarEntre(origem, fim));
        }

        return null;
    }

        private static Collider2D ObterColliderFisicoNpc()
    {
        if (seguidor == null)
            return null;

        if (colliderFisicoNpc != null &&
            colliderFisicoNpc.enabled &&
            !colliderFisicoNpc.isTrigger)
            return colliderFisicoNpc;

        colliderFisicoNpc = seguidor.GetComponent<Collider2D>();
        if (colliderFisicoNpc == null ||
            !colliderFisicoNpc.enabled ||
            colliderFisicoNpc.isTrigger)
        {
            colliderFisicoNpc = null;
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

        if (colliderFisicoNpc != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Collider físico usado na navegação: " +
                colliderFisicoNpc.GetType().Name +
                ", tamanho=" + colliderFisicoNpc.bounds.size + ".");
        }
        else
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Nenhum collider físico não-trigger foi encontrado no prefab; usando o fallback de colisão.");
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
        return new Vector2(
            Mathf.Abs(escala.x) * FatorEscalaColisaoNavegacao,
            Mathf.Abs(escala.y) * FatorEscalaColisaoNavegacao);
    }

    // Margem adicional usada somente pelas consultas de ocupação da navegação.
    // Ela faz o A* e a validação de waypoints perceberem paredes um pouco antes,
    // dando espaço para o NPC contornar quinas sem alterar os colliders do mapa.
    // Os casts de deslocamento abaixo continuam usando a hitbox física normal,
    // então a colisão real ainda é verificada separadamente.
    private const float MargemAntecipacaoColisaoMapa = 0.08f;

    // Usa o formato real do collider do personagem, com uma pequena folga
    // para consultas de antecipação. Circle, Box e Capsule mantêm sua geometria.
    private static Collider2D[] ObterColisoesNaPosicao(Vector2 posicaoRaiz, int mascara)
    {
        Collider2D collider = ObterColliderFisicoNpc();
        if (collider == null)
            return Physics2D.OverlapCircleAll(
                posicaoRaiz, RaioColisaoFallback + MargemAntecipacaoColisaoMapa, mascara);

        Vector2 centro = ObterCentroColliderNaPosicao(collider, posicaoRaiz);
        Vector2 escala = ObterEscalaAbsoluta(collider.transform);
        float angulo = collider.transform.eulerAngles.z;

        if (collider is CircleCollider2D circulo)
        {
            float raio = circulo.radius * Mathf.Max(escala.x, escala.y) +
                MargemAntecipacaoColisaoMapa;
            return Physics2D.OverlapCircleAll(centro, raio, mascara);
        }

        if (collider is BoxCollider2D caixa)
        {
            Vector2 tamanho = Vector2.Scale(caixa.size, escala) +
                Vector2.one * (MargemAntecipacaoColisaoMapa * 2f);
            return Physics2D.OverlapBoxAll(centro, tamanho, angulo, mascara);
        }

        if (collider is CapsuleCollider2D capsula)
        {
            Vector2 tamanho = Vector2.Scale(capsula.size, escala) +
                Vector2.one * (MargemAntecipacaoColisaoMapa * 2f);
            return Physics2D.OverlapCapsuleAll(
                centro, tamanho, capsula.direction, angulo, mascara);
        }

        float raioFallback = collider.bounds.extents.magnitude *
            FatorEscalaColisaoNavegacao + MargemAntecipacaoColisaoMapa;
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

        float raioFallback = collider.bounds.extents.magnitude * FatorEscalaColisaoNavegacao;
        return Physics2D.CircleCastAll(
            centro, raioFallback, direcaoMovimento, distancia, mascara);
    }

    private static float ObterPenalidadeProximidadeParede(
        Vector2Int celula, Vector2 minimo, int largura, int altura,
        Dictionary<Vector2Int, bool> cacheCelulas,
        Dictionary<Vector2Int, float> cachePenalidades)
    {
        if (cachePenalidades.TryGetValue(celula, out float penalidadeCacheada))
            return penalidadeCacheada;

        Vector2 centro = CentroCelula(celula, minimo);

        // Penalidade barata baseada apenas na vizinhança já usada pelo A*.
        // CelulaLivre mantém cache, evitando OverlapCircleAll adicional por nó.
        // Células cercadas por mais obstáculos recebem custo maior; em corredores
        // estreitos o caminho continua permitido, pois isso nunca bloqueia a célula.
        int vizinhosBloqueados = 0;
        Vector2Int[] vizinhos =
        {
            new Vector2Int(1, 0), new Vector2Int(-1, 0),
            new Vector2Int(0, 1), new Vector2Int(0, -1),
            new Vector2Int(1, 1), new Vector2Int(1, -1),
            new Vector2Int(-1, 1), new Vector2Int(-1, -1)
        };

        foreach (Vector2Int deslocamento in vizinhos)
        {
            Vector2Int vizinho = celula + deslocamento;
            if (vizinho.x < 0 || vizinho.y < 0 ||
                vizinho.x >= largura || vizinho.y >= altura ||
                !CelulaLivre(vizinho, minimo, cacheCelulas))
                vizinhosBloqueados++;
        }

        // Peso moderado: favorece áreas com folga sem forçar o NPC a evitar
        // passagens estreitas ou a fazer desvios excessivos.
        float penalidade = vizinhosBloqueados * (PenalidadeProximidadeParede * 0.22f);
        cachePenalidades[celula] = penalidade;
        return penalidade;
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

            // O jogador-alvo não pode ser ignorado na física: ignorá-lo fazia
            // o cast também ignorar paredes/obstáculos que compartilham sua
            // hierarquia em alguns mapas. O NPC deve parar antes do collider.
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
                colisor.transform.IsChildOf(seguidor.transform) ||
                (alvoIgnoradoNavegacao != null &&
                 colisor.transform.IsChildOf(alvoIgnoradoNavegacao.transform)))
                continue;
            return false;
        }

        return PodePassarEntre(origem, destino);
    }

    private static float Heuristica(Vector2Int a, Vector2Int b)
    {
        int dx = a.x - b.x;
        int dy = a.y - b.y;

        // Distância euclidiana: heurística consistente para movimentos em 8 direções.
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
        if (mostrarCaminhoNpc && seguidor != null)
            GarantirRenderizadorCaminho();
        if (!mostrarCaminhoNpc)
            OcultarRenderizacaoCaminho();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F8/Seguidor] Visualização do caminho " +
            (mostrarCaminhoNpc ? "ativada." : "desativada."));
        return mostrarCaminhoNpc;
    }

    private static void GarantirRenderizadorCaminho()
    {
        if (renderizadorCaminhoNpc != null)
            return;

        GameObject objeto = new GameObject("YouTubeVoteBridge_NPC_Path");
        objeto.hideFlags = HideFlags.DontSave;
        renderizadorCaminhoNpc = objeto.AddComponent<LineRenderer>();
        renderizadorCaminhoNpc.useWorldSpace = true;
        renderizadorCaminhoNpc.loop = false;
        renderizadorCaminhoNpc.widthMultiplier = 0.055f;
        renderizadorCaminhoNpc.numCapVertices = 4;
        renderizadorCaminhoNpc.numCornerVertices = 4;
        renderizadorCaminhoNpc.startColor = new Color(0.1f, 0.9f, 1f, 1f);
        renderizadorCaminhoNpc.endColor = new Color(0.2f, 1f, 0.35f, 1f);
        renderizadorCaminhoNpc.sortingOrder = 5000;

        Shader shader = Shader.Find("Sprites/Default");
        if (shader != null)
        {
            materialCaminhoNpc = new Material(shader);
            renderizadorCaminhoNpc.material = materialCaminhoNpc;
        }
        renderizadorCaminhoNpc.enabled = false;
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
            GarantirRenderizadorCaminho();

            // Desenha exclusivamente os waypoints que MoverAte usa.
            // Nunca completa a linha diretamente até o jogador: isso mostrava
            // uma rota fictícia atravessando paredes quando o A* falhava.
            int primeiroIndice = Mathf.Clamp(indiceCaminho, 0, caminhoAtual.Count);
            int pontosRestantes = caminhoAtual.Count - primeiroIndice;
            if (pontosRestantes <= 0)
            {
                OcultarRenderizacaoCaminho();
                return;
            }

            renderizadorCaminhoNpc.positionCount = pontosRestantes + 1;

            Vector3 posicaoNpc = seguidor.transform.position;
            posicaoNpc.z -= 0.25f;
            renderizadorCaminhoNpc.SetPosition(0, posicaoNpc);

            for (int i = 0; i < pontosRestantes; i++)
            {
                Vector2 ponto = caminhoAtual[primeiroIndice + i];
                renderizadorCaminhoNpc.SetPosition(i + 1,
                    new Vector3(ponto.x, ponto.y, posicaoNpc.z));
            }

            renderizadorCaminhoNpc.enabled = pontosRestantes >= 1;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F8/Seguidor] Não foi possível desenhar o caminho real do NPC: " + ex.Message);
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
        direcaoDeslizamentoCache = Vector2.zero;
        destinoDeslizamentoCache = Vector2.zero;
        proximaReavaliacaoDeslizamento = 0f;
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

    /// <summary>
    /// Converte o proxy ISystemType para o wrapper IL2CPP concreto, seguindo
    /// a mesma estratégia usada pelo EHR (CastFast): reutiliza o ponteiro nativo.
    /// </summary>
    private static T ObterSistemaTipado<T>(SystemTypes tipo) where T : Il2CppObjectBase
    {
        if (sistemasTipadosCache.TryGetValue(tipo, out object cacheado) &&
            cacheado is T sistemaCacheado)
            return sistemaCacheado;

        if (ShipStatus.Instance?.Systems == null ||
            !ShipStatus.Instance.Systems.ContainsKey(tipo))
            return null;

        object sistemaOriginal = ShipStatus.Instance.Systems[tipo];
        if (sistemaOriginal is T sistemaDireto)
        {
            sistemasTipadosCache[tipo] = sistemaDireto;
            return sistemaDireto;
        }

        if (sistemaOriginal is not Il2CppObjectBase objetoIl2Cpp ||
            objetoIl2Cpp.Pointer == IntPtr.Zero)
        {
            if (sistemasSemEstadoAtivoDiagnosticados.Add(tipo))
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] O sistema " + tipo +
                    " não pôde ser convertido de ISystemType para Il2CppObjectBase. Tipo recebido=" +
                    (sistemaOriginal?.GetType().FullName ?? "null") + ".");
            return null;
        }

        try
        {
            // Os wrappers gerados pelo IL2CPP possuem construtor IntPtr.
            // A instância criada aponta para o mesmo objeto nativo do jogo.
            ConstructorInfo construtor = typeof(T).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(IntPtr) }, null);

            if (construtor == null)
                throw new MissingMethodException(
                    typeof(T).FullName, ".ctor(IntPtr)");

            T convertido = construtor.Invoke(new object[] { objetoIl2Cpp.Pointer }) as T;
            if (convertido != null)
                sistemasTipadosCache[tipo] = convertido;
            else if (sistemasSemEstadoAtivoDiagnosticados.Add(tipo))
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] A conversão de " + tipo + " para " +
                    typeof(T).Name + " retornou null.");

            return convertido;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao converter sistema " + tipo +
                " para " + typeof(T).Name + ": " + ex.Message);
            return null;
        }
    }

    private static bool SistemaEstaAtivo(SystemTypes tipo)
    {
        try
        {
            if (ShipStatus.Instance?.Systems == null ||
                !ShipStatus.Instance.Systems.ContainsKey(tipo))
                return false;

            int mapa = GameOptionsManager.Instance?.CurrentGameOptions?.MapId ?? -1;

            // Mesma seleção por mapa e pelos tipos concretos usada pelo EHR.
            // Não tentamos refletir IsActive no proxy ISystemType.
            switch (tipo)
            {
                case SystemTypes.Electrical:
                    if (mapa == 5) return false;
                    return ObterSistemaTipado<SwitchSystem>(tipo)?.IsActive ?? false;

                case SystemTypes.Reactor:
                    if (mapa == 2) return false;
                    if (mapa == 4)
                        return ObterSistemaTipado<HeliSabotageSystem>(tipo)?.IsActive ?? false;
                    return ObterSistemaTipado<ReactorSystemType>(tipo)?.IsActive ?? false;

                case SystemTypes.Laboratory:
                    if (mapa != 2) return false;
                    return ObterSistemaTipado<ReactorSystemType>(tipo)?.IsActive ?? false;

                case SystemTypes.LifeSupp:
                    if (mapa == 2 || mapa == 4 || mapa == 5) return false;
                    return ObterSistemaTipado<LifeSuppSystemType>(tipo)?.IsActive ?? false;

                case SystemTypes.Comms:
                    if (mapa == 1 || mapa == 5)
                        return ObterSistemaTipado<HqHudSystemType>(tipo)?.IsActive ?? false;
                    return ObterSistemaTipado<HudOverrideSystemType>(tipo)?.IsActive ?? false;

                case SystemTypes.HeliSabotage:
                    if (mapa != 4) return false;
                    return ObterSistemaTipado<HeliSabotageSystem>(tipo)?.IsActive ?? false;

                case SystemTypes.MushroomMixupSabotage:
                    if (mapa != 5) return false;
                    return ObterSistemaTipado<MushroomMixupSabotageSystem>(tipo)?.IsActive ?? false;

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao consultar estado da sabotagem " + tipo + ": " + ex.Message);
            return false;
        }
    }

    // Localiza o objeto físico do painel e escolhe um ponto livre imediatamente
    // à sua frente. Assim o NPC não tenta reparar apenas por estar dentro da sala.
    private static bool TentarObterDestinoPainelSala(
        SystemTypes sabotagem, Bounds limitesSala, out Vector2 destino)
    {
        destino = Vector2.zero;
        if (ShipStatus.Instance == null)
            return false;

        string[] termos = sabotagem switch
        {
            SystemTypes.Electrical => new[] { "switch", "panel" },
            SystemTypes.Reactor => new[] { "reactor", "scanner", "console", "panel" },
            SystemTypes.Laboratory => new[] { "stabilizer", "scanner", "console", "panel" },
            SystemTypes.LifeSupp => new[] { "oxygen", "o2", "keypad", "console", "panel" },
            SystemTypes.Comms => new[] { "comms", "communication", "console", "panel" },
            SystemTypes.HeliSabotage => new[] { "crash", "gap", "code", "console", "panel" },
            _ => new[] { "console", "panel" }
        };

        Transform[] transforms = ShipStatus.Instance.GetComponentsInChildren<Transform>(true);
        Transform melhorPainel = null;
        float menorDistancia = float.MaxValue;
        Vector2 centroSala = limitesSala.center;

        foreach (Transform item in transforms)
        {
            if (item == null || !limitesSala.Contains(item.position))
                continue;

            string nome = item.name ?? string.Empty;
            bool parecePainel = nome.IndexOf("console", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("panel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("switch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("scanner", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("stabilizer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("keypad", StringComparison.OrdinalIgnoreCase) >= 0;

            if (!parecePainel)
                continue;

            bool corresponde = termos.Any(termo =>
                nome.IndexOf(termo, StringComparison.OrdinalIgnoreCase) >= 0);

            if (!corresponde)
                continue;

            float distancia = Vector2.Distance(item.position, centroSala);
            if (distancia < menorDistancia)
            {
                menorDistancia = distancia;
                melhorPainel = item;
            }
        }

        if (melhorPainel == null)
            return false;

        Vector2 posicaoPainel = melhorPainel.position;
        Vector2 cima = ((Vector2)melhorPainel.up).normalized;
        Vector2 lado = ((Vector2)melhorPainel.right).normalized;
        if (cima.sqrMagnitude < 0.01f)
            cima = Vector2.up;
        if (lado.sqrMagnitude < 0.01f)
            lado = Vector2.right;

        Vector2[] direcoes =
        {
            cima, -cima, lado, -lado,
            (cima + lado).normalized, (cima - lado).normalized,
            (-cima + lado).normalized, (-cima - lado).normalized
        };

        float melhorDistanciaPainel = float.MaxValue;
        bool encontrou = false;

        foreach (Vector2 direcao in direcoes)
        {
            Vector2 candidato = posicaoPainel + direcao * 0.62f;
            if (!limitesSala.Contains(candidato))
                continue;

            if (!TentarEncontrarPontoLivreNaSala(
                limitesSala, candidato, out Vector2 pontoLivre))
                continue;

            float distanciaPainel = Vector2.Distance(posicaoPainel, pontoLivre);
            if (distanciaPainel < 0.30f || distanciaPainel > 1.15f ||
                distanciaPainel >= melhorDistanciaPainel)
                continue;

            melhorDistanciaPainel = distanciaPainel;
            destino = pontoLivre;
            encontrou = true;
        }

        if (!encontrou)
            return false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Seguidor] Painel " + sabotagem + " localizado em " +
            posicaoPainel + "; ponto de interação=" + destino + ".");
        return true;
    }

    // O2 exige concluir os dois teclados separadamente. A ordem dos IDs
    // segue o protocolo vanilla: Skeld (0 Admin, 1 O2), Mira (0 Greenhouse, 1 Hallway).
    private static bool TentarObterDestinoConsoleO2(int consoleId, out Vector2 destino)
    {
        destino = Vector2.zero;
        int mapa = GameOptionsManager.Instance?.CurrentGameOptions?.MapId ?? -1;

        string[] nomesSala = mapa == 1
            ? (consoleId == 0
                ? new[] { "Greenhouse" }
                : new[] { "Hallway" })
            : (consoleId == 0
                ? new[] { "Admin" }
                : new[] { "O2", "Oxygen" });

        foreach (string nome in nomesSala)
        {
            PlainShipRoom sala = ShipStatus.Instance.AllRooms.FirstOrDefault(r =>
                r != null && r.roomArea != null &&
                r.RoomId.ToString().IndexOf(nome, StringComparison.OrdinalIgnoreCase) >= 0);

            if (sala == null)
                continue;

            if (TentarObterDestinoPainelSala(
                SystemTypes.LifeSupp, sala.roomArea.bounds, out destino))
                return true;

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Sala " + nome +
                " encontrada para o teclado O2 " + consoleId +
                ", mas o painel não foi localizado.");
        }

        return false;
    }

    private static bool TentarObterDestinoSalaSabotagem(
        SystemTypes sabotagem, out Vector2 destino)
    {
        destino = Vector2.zero;
        if (ShipStatus.Instance == null)
            return false;

        int mapa = GameOptionsManager.Instance?.CurrentGameOptions?.MapId ?? -1;

        string[] nomesSala = sabotagem switch
        {
            SystemTypes.Reactor => new[] { "Reactor", "Reator" },
            SystemTypes.Laboratory => new[] { "Laboratory", "Lab" },
            SystemTypes.LifeSupp when mapa == 1 => new[] { "Greenhouse", "O2", "Office" },
            SystemTypes.LifeSupp => new[] { "O2", "Oxygen", "Admin" },
            SystemTypes.Comms => new[] { "Comms", "Communications", "Communication" },
            SystemTypes.HeliSabotage => new[] { "Cockpit", "GapRoom", "Engine" },
            _ => Array.Empty<string>()
        };

        foreach (string nome in nomesSala)
        {
            PlainShipRoom sala = ShipStatus.Instance.AllRooms.FirstOrDefault(r =>
                r != null && r.roomArea != null &&
                r.RoomId.ToString().IndexOf(nome, StringComparison.OrdinalIgnoreCase) >= 0);

            if (sala == null)
                continue;

            Bounds limites = sala.roomArea.bounds;
            if (TentarObterDestinoPainelSala(sabotagem, limites, out destino))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Repara reator, O2, comunicações e crash course usando os mesmos
    /// códigos de atualização dos sistemas nativos do jogo.
    /// </summary>
    private static bool TentarResolverOutrasSabotagens()
    {
        if (seguidor?.NetTransform?.body == null || ShipStatus.Instance == null)
            return false;

        int mapa = GameOptionsManager.Instance?.CurrentGameOptions?.MapId ?? -1;
        SystemTypes? ativa = null;

        SystemTypes[] ordem = mapa switch
        {
            2 => new[] { SystemTypes.Laboratory, SystemTypes.Comms },
            4 => new[] { SystemTypes.HeliSabotage, SystemTypes.Comms },
            5 => new[] { SystemTypes.Comms },
            _ => new[] { SystemTypes.Reactor, SystemTypes.LifeSupp, SystemTypes.Comms }
        };

        foreach (SystemTypes tipo in ordem)
        {
            if (SistemaEstaAtivo(tipo))
            {
                ativa = tipo;
                break;
            }
        }

        if (ativa == null)
        {
            if (resolvendoOutraSabotagem)
            {
                resolvendoOutraSabotagem = false;
                consoleO2Atual = 0;
                aguardandoSegundoJogadorReator = false;
                proximaTentativaReparo = 0f;
                aguardarPerseguicaoAte = Time.realtimeSinceStartup + PausaAposSabotagem;
                PararCorpo(seguidor.NetTransform.body, "TentarResolverOutrasSabotagens (linha 2952)");
                possuiDestinoPatrulha = false;
                LimparCaminho();
                EnviarDestinoAosClientesComMod(null);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Sabotagem " + sabotagemAtual +
                    " resolvida; NPC retomando suas atividades.");
            }

            return false;
        }

        if (!resolvendoOutraSabotagem || sabotagemAtual != ativa.Value)
        {
            bool encontrouDestino = ativa.Value == SystemTypes.LifeSupp
                ? TentarObterDestinoConsoleO2(0, out destinoOutraSabotagem)
                : TentarObterDestinoSalaSabotagem(ativa.Value, out destinoOutraSabotagem);

            if (!encontrouDestino)
            {
                if (!avisoSabotagemSemSala || ultimaSabotagemSemSala != ativa.Value)
                {
                    avisoSabotagemSemSala = true;
                    ultimaSabotagemSemSala = ativa.Value;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] Sabotagem " + ativa.Value +
                        " ativa, mas não foi possível localizar sua sala de reparo. NPC aguardando e mantendo prioridade na sabotagem.");
                }

                // A sabotagem ativa continua tendo prioridade mesmo se a sala
                // não puder ser identificada; não volta a perseguir jogadores.
                PararCorpo(seguidor.NetTransform.body, "TentarResolverOutrasSabotagens (linha 2984)");
                EnviarDestinoAosClientesComMod(null, seguidor.GetTruePosition());
                return true;
            }

            sabotagemAtual = ativa.Value;
            resolvendoOutraSabotagem = true;
            consoleO2Atual = 0;
            aguardandoSegundoJogadorReator = false;
            avisoSabotagemSemSala = false;
            ultimaSabotagemSemSala = SystemTypes.Hallway;
            possuiDestinoPatrulha = false;
            proximaTentativaReparo = 0f;
            LimparCaminho();
            EnviarDestinoAosClientesComMod(null, destinoOutraSabotagem);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Sabotagem " + sabotagemAtual +
                " detectada; NPC indo para " + destinoOutraSabotagem + ".");
        }

        AtualizarDestinoReplicado(null, destinoOutraSabotagem);

        // No reator/seismic, o NPC ocupa apenas o scanner 0. O scanner 1
        // precisa ser acionado por outro jogador real; enquanto isso, fica parado
        // e mantém a sabotagem como prioridade sem repetir RPCs.
        if ((sabotagemAtual == SystemTypes.Reactor ||
             sabotagemAtual == SystemTypes.Laboratory) &&
            aguardandoSegundoJogadorReator)
        {
            PararCorpo(seguidor.NetTransform.body, "TentarResolverOutrasSabotagens (linha 3011)");
            return true;
        }

        // Depois de enviar os dois teclados O2, não envia IDs adicionais
        // enquanto espera o estado vanilla confirmar que a sabotagem terminou.
        if (sabotagemAtual == SystemTypes.LifeSupp && consoleO2Atual >= 2)
        {
            PararCorpo(seguidor.NetTransform.body, "TentarResolverOutrasSabotagens (linha 3019)");
            return true;
        }

        if (Vector2.Distance(seguidor.GetTruePosition(), destinoOutraSabotagem) >
            DistanciaChegadaPatrulha)
        {
            MoverAte(destinoOutraSabotagem);
            return true;
        }

        PararCorpo(seguidor.NetTransform.body, "TentarResolverOutrasSabotagens (linha 3030)");

        float agora = Time.realtimeSinceStartup;
        if (agora < proximaTentativaReparo)
            return true;

        try
        {
            switch (sabotagemAtual)
            {
                case SystemTypes.Reactor:
                case SystemTypes.Laboratory:
                    // 0x40 = mão no scanner 0. Não envia o segundo scanner
                    // nem o sinal 0x10: outro jogador precisa completar o par.
                    ShipStatus.Instance.UpdateSystem(sabotagemAtual, seguidor, 0x40);
                    aguardandoSegundoJogadorReator = true;
                    proximaTentativaReparo = float.MaxValue;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Seguidor] NPC ocupou o scanner 0 de " + sabotagemAtual +
                        "; aguardando outro jogador no scanner 1.");
                    return true;

                case SystemTypes.LifeSupp:
                    // 0x40 | consoleId conclui apenas o teclado indicado.
                    // O NPC então atravessa o mapa até o segundo teclado.
                    ShipStatus.Instance.UpdateSystem(
                        SystemTypes.LifeSupp, seguidor, (byte)(0x40 | consoleO2Atual));

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Seguidor] NPC concluiu o teclado O2 " +
                        consoleO2Atual + "/1.");

                    consoleO2Atual++;
                    if (consoleO2Atual < 2)
                    {
                        if (TentarObterDestinoConsoleO2(
                            consoleO2Atual, out destinoOutraSabotagem))
                        {
                            LimparCaminho();
                            proximaTentativaReparo = agora + 0.5f;
                            EnviarDestinoAosClientesComMod(null, destinoOutraSabotagem);
                            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                                "[F7/Seguidor] Indo para o segundo teclado O2: " +
                                destinoOutraSabotagem + ".");
                        }
                        else
                        {
                            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                                "[F7/Seguidor] Não encontrou o segundo teclado O2. " +
                                "Mantendo a sabotagem como prioridade.");
                            consoleO2Atual = 2;
                        }
                    }
                    else
                    {
                        // Os dois teclados foram concluídos; aguarda o sistema
                        // vanilla atualizar e confirmar o fim da sabotagem.
                        proximaTentativaReparo = float.MaxValue;
                    }
                    return true;

                case SystemTypes.Comms:
                    // Mira HQ e The Fungle usam dois consoles de comunicação.
                    if (mapa == 1 || mapa == 5)
                    {
                        ShipStatus.Instance.UpdateSystem(SystemTypes.Comms, seguidor, 16);
                        ShipStatus.Instance.UpdateSystem(SystemTypes.Comms, seguidor, 17);
                    }
                    else
                    {
                        ShipStatus.Instance.UpdateSystem(SystemTypes.Comms, seguidor, 16);
                    }
                    break;

                case SystemTypes.HeliSabotage:
                    // O fluxo usado pelo EHR registra o painel do Crash Course
                    // e depois limpa o contador do sistema Reactor do Airship.
                    ShipStatus.Instance.UpdateSystem(SystemTypes.HeliSabotage, seguidor, 17);
                    ShipStatus.Instance.UpdateSystem(SystemTypes.Reactor, seguidor, 16);
                    break;
            }

            PararCorpo(seguidor.NetTransform.body, "TentarResolverOutrasSabotagens (linha 3112)");
            proximaTentativaReparo = agora + 1f;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC enviou interação de reparo para " +
                sabotagemAtual + ".");
        }
        catch (Exception ex)
        {
            proximaTentativaReparo = agora + 2f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao reparar " + sabotagemAtual +
                ": " + ex.Message);
        }

        return true;
    }

    // Calcula a visão como a de um tripulante real. CalculateLightRadius
    // já considera o multiplicador de visão e a sabotagem das luzes.
    private static float ObterAlcanceVisaoTripulante()
    {
        float agora = Time.realtimeSinceStartup;
        if (agora < proximaAtualizacaoAlcanceVisao)
            return alcanceVisaoTripulanteCacheado;

        proximaAtualizacaoAlcanceVisao = agora + 0.5f;

        if (ShipStatus.Instance == null)
        {
            alcanceVisaoTripulanteCacheado = 0f;
            return 0f;
        }

        try
        {
            NetworkedPlayerInfo dadosTripulante = null;

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || jogador.Data.IsDead)
                    continue;

                object roleData = LerMembro(jogador.Data, "Role");
                object roleType = LerMembro(roleData, "Role");
                object isImpostorValue = LerMembro(roleData, "IsImpostor");

                bool impostor = isImpostorValue is bool valor
                    ? valor
                    : roleType != null &&
                      roleType.ToString().IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!impostor)
                {
                    dadosTripulante = jogador.Data;
                    break;
                }
            }

            // Se todos os jogadores vivos forem impostores, usa o registro do NPC
            // como último recurso. O caminho normal sempre prefere dados de tripulante.
            dadosTripulante ??= seguidor?.Data;
            if (dadosTripulante == null)
            {
                alcanceVisaoTripulanteCacheado = 0f;
                return 0f;
            }

            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            MethodInfo calcular = ShipStatus.Instance.GetType()
                .GetMethods(flags)
                .FirstOrDefault(m =>
                {
                    if (m.Name != "CalculateLightRadius")
                        return false;

                    ParameterInfo[] parametros = m.GetParameters();
                    return parametros.Length == 1 &&
                           parametros[0].ParameterType.IsInstanceOfType(dadosTripulante);
                });

            if (calcular == null)
            {
                alcanceVisaoTripulanteCacheado = 0f;
                return 0f;
            }

            object resultado = calcular.Invoke(ShipStatus.Instance, new object[] { dadosTripulante });
            alcanceVisaoTripulanteCacheado = resultado == null
                ? 0f
                : Mathf.Max(0f, Convert.ToSingle(resultado));
            return alcanceVisaoTripulanteCacheado;
        }
        catch (Exception ex)
        {
            alcanceVisaoTripulanteCacheado = 0f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Falha ao calcular visão de tripulante: " + ex.Message);
            return 0f;
        }
    }

    private static bool PodeVerJogador(PlayerControl jogador, Vector2 origem)
    {
        // Percepção global: o NPC considera todos os jogadores vivos,
        // conectados e fora dos dutos, mesmo através de paredes.
        // A seleção do alvo é feita exclusivamente pela menor distância.
        return jogador != null &&
            jogador.Data != null &&
            !jogador.Data.IsDead &&
            !jogador.Data.Disconnected;
    }

    /// <summary>
    /// Faz o NPC resolver a sabotagem de luzes como um tripulante:
    /// vai até a Elétrica e corrige um interruptor por vez, com intervalo de 0,5s.
    /// </summary>
    private static bool TentarResolverSabotagemLuzes()
    {
        if (seguidor?.NetTransform?.body == null || ShipStatus.Instance == null)
            return false;

        SwitchSystem sistemaLuzes = null;
        try
        {
            sistemaLuzes = ObterSistemaTipado<SwitchSystem>(SystemTypes.Electrical);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Não foi possível consultar o sistema de luzes: " + ex.Message);
            return false;
        }

        bool luzesAtivas = sistemaLuzes != null &&
            SistemaEstaAtivo(SystemTypes.Electrical);

        if (!luzesAtivas)
        {
            if (resolvendoSabotagemLuzes)
            {
                resolvendoSabotagemLuzes = false;
                proximaAcaoInterruptor = 0f;
                aguardarPerseguicaoAte = Time.realtimeSinceStartup + PausaAposSabotagem;
                PararCorpo(seguidor.NetTransform.body, "TentarResolverSabotagemLuzes (linha 3247)");
                possuiDestinoPatrulha = false;
                LimparCaminho();
                EnviarDestinoAosClientesComMod(null);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Sabotagem de luzes resolvida; NPC retomando suas atividades.");
            }

            return false;
        }

        if (!resolvendoSabotagemLuzes)
        {
            PlainShipRoom salaEletrica = ShipStatus.Instance.AllRooms
                .FirstOrDefault(sala => sala != null &&
                    sala.roomArea != null &&
                    sala.RoomId == SystemTypes.Electrical);

            if (salaEletrica == null ||
                !TentarObterDestinoPainelSala(
                    SystemTypes.Electrical,
                    salaEletrica.roomArea.bounds,
                    out destinoEletrica))
            {
                if (!avisoSabotagemSemSala || ultimaSabotagemSemSala != SystemTypes.Electrical)
                {
                    avisoSabotagemSemSala = true;
                    ultimaSabotagemSemSala = SystemTypes.Electrical;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Seguidor] Luzes sabotadas, mas a sala/ponto de reparo da Elétrica não foi localizado. NPC aguardando e mantendo prioridade na sabotagem.");
                }

                // Não libera perseguição/patrulha enquanto as luzes estiverem sabotadas.
                PararCorpo(seguidor.NetTransform.body, "TentarResolverSabotagemLuzes (linha 3281)");
                return true;
            }

            resolvendoSabotagemLuzes = true;
            possuiDestinoPatrulha = false;
            proximaAcaoInterruptor = 0f;
            LimparCaminho();
            EnviarDestinoAosClientesComMod(null, destinoEletrica);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Luzes sabotadas; NPC indo para a Elétrica: " +
                destinoEletrica + ".");
        }

        AtualizarDestinoReplicado(null, destinoEletrica);

        Vector2 origem = seguidor.GetTruePosition();
        if (Vector2.Distance(origem, destinoEletrica) > DistanciaChegadaPatrulha)
        {
            MoverAte(destinoEletrica);
            return true;
        }

        PararCorpo(seguidor.NetTransform.body, "TentarResolverSabotagemLuzes (linha 3303)");

        float agora = Time.realtimeSinceStartup;
        if (agora < proximaAcaoInterruptor)
            return true;

        // O wrapper concreto é necessário também para ler os bits dos painéis;
        // o proxy ISystemType não expõe ActualSwitches/ExpectedSwitches.
        byte interruptoresDiferentes = (byte)(
            sistemaLuzes.ActualSwitches ^ sistemaLuzes.ExpectedSwitches);

        for (byte indice = 0; indice < 5; indice++)
        {
            byte mascara = (byte)(1 << indice);
            if ((interruptoresDiferentes & mascara) == 0)
                continue;

            try
            {
                ShipStatus.Instance.UpdateSystem(
                    SystemTypes.Electrical, seguidor, indice);

                PararCorpo(seguidor.NetTransform.body, "TentarResolverSabotagemLuzes (linha 3325)");
                proximaAcaoInterruptor = agora + IntervaloInterruptorLuzes;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] NPC acionou o interruptor " +
                    (indice + 1) + "/5 na Elétrica.");
            }
            catch (Exception ex)
            {
                proximaAcaoInterruptor = agora + 1f;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Falha ao acionar interruptor " +
                    (indice + 1) + ": " + ex.Message);
            }

            return true;
        }

        // O sistema ainda pode precisar de um ciclo para atualizar IsActive.
        proximaAcaoInterruptor = agora + IntervaloInterruptorLuzes;
        return true;
    }

    private static PlayerControl ObterAlvoAtual()
    {
        if (seguidor == null)
            return null;

        Vector2 origem = seguidor.GetTruePosition();
        jogadoresParaIA.Clear();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            jogadoresParaIA.Add(jogador);

        PlayerControl alvoAnterior = alvoAtual;
        alvoAtual = inteligencia.AtualizarAlvo(
            jogadoresParaIA,
            seguidor,
            origem,
            Time.realtimeSinceStartup,
            PodeSerAlvoDaIA,
            jogador => PodeVerJogador(jogador, origem));

        proximaBuscaAlvo = Time.realtimeSinceStartup + IntervaloTrocaAlvo;

        byte novoPlayerIdAlvo = alvoAtual != null
            ? alvoAtual.PlayerId
            : byte.MaxValue;

        byte playerIdAnterior = alvoAnterior != null
            ? alvoAnterior.PlayerId
            : byte.MaxValue;

        if (!alvoInicialNotificado || novoPlayerIdAlvo != ultimoAlvoNotificado ||
            novoPlayerIdAlvo != playerIdAnterior)
        {
            alvoInicialNotificado = true;
            ultimoAlvoNotificado = novoPlayerIdAlvo;
            EnviarDestinoAosClientesComMod(alvoAtual);
        }

        return alvoAtual;
    }

    private static bool PodeSerAlvoDaIA(PlayerControl jogador)
    {
        return jogador != null &&
            jogador != seguidor &&
            jogador.Data != null &&
            !jogador.Data.IsDead &&
            !jogador.Data.Disconnected &&
            !EstaNoDuto(jogador);
    }

    private static PlayerControl ObterJogadorVivoMaisProximo(
        Vector2 origem, byte ignorarPlayerId = byte.MaxValue)
    {
        PlayerControl melhor = null;
        float menorDistancia = float.MaxValue;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == seguidor ||
                jogador.PlayerId == ignorarPlayerId ||
                jogador.Data == null ||
                jogador.Data.IsDead ||
                jogador.Data.Disconnected ||
                EstaNoDuto(jogador) ||
                !PodeVerJogador(jogador, origem))
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

    private static float ObterCooldownKillNpc()
    {
        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes != null)
                return Mathf.Max(0.1f, opcoes.GetFloat(FloatOptionNames.KillCooldown));
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Seguidor] Não foi possível ler o Kill Cooldown das opções: " + ex.Message);
        }

        // Fallback conservador caso a opção não esteja disponível nesta versão.
        return 30f;
    }

    // Retorna o centro visual do personagem, em vez de GetTruePosition(),
    // que representa o ponto de apoio no chão e faz a perseguição mirar nos pés.
    private static Vector2 ObterCentroVisualJogador(PlayerControl jogador)
    {
        if (jogador == null)
            return Vector2.zero;

        try
        {
            SpriteRenderer[] renderizadores =
                jogador.GetComponentsInChildren<SpriteRenderer>(true);

            SpriteRenderer melhor = null;
            foreach (SpriteRenderer renderizador in renderizadores)
            {
                if (renderizador == null || !renderizador.enabled ||
                    !renderizador.gameObject.activeInHierarchy)
                    continue;

                if (melhor == null ||
                    renderizador.bounds.size.sqrMagnitude > melhor.bounds.size.sqrMagnitude)
                    melhor = renderizador;
            }

            if (melhor != null)
                return melhor.bounds.center;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[F7/Seguidor] Não foi possível obter o centro visual do jogador: " + ex.Message);
        }

        return jogador.GetTruePosition() + Vector2.up * DeslocamentoCentroVisualFallback;
    }

    // A navegação continua no plano do chão; apenas a aquisição e a hitbox
    // virtual usam o centro visual do sprite.
    private static Vector2 ObterPontoDePerseguicao(PlayerControl jogador, Vector2 centroVisual)
    {
        if (jogador == null)
            return centroVisual;

        Vector2 posicaoPe = jogador.GetTruePosition();
        float deslocamentoY = centroVisual.y - posicaoPe.y;
        return centroVisual - Vector2.up * deslocamentoY;
    }

    private static bool PodeAtacarJogador(PlayerControl jogador, Vector2 origem)
    {
        if (jogador == null || jogador.Data == null ||
            jogador.Data.IsDead || jogador.Data.Disconnected || EstaNoDuto(jogador))
            return false;

        Vector2 destino = ObterCentroVisualJogador(jogador);
        RaycastHit2D impacto = Physics2D.Linecast(origem, destino, Constants.ShipOnlyMask);
        return impacto.collider == null || impacto.collider.transform.IsChildOf(jogador.transform);
    }

    private static bool MatarAlvo(PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || seguidor == null || alvo == null ||
            alvo.Data == null || alvo.Data.IsDead || alvo.Data.Disconnected ||
            EstaNoDuto(alvo) || PlayerControl.LocalPlayer == null ||
            AmongUsClient.Instance == null ||
            Time.realtimeSinceStartup < proximoKillNpc)
            return false;

        try
        {
            Vector2 posicaoMorte = alvo.GetTruePosition();
            byte playerId = alvo.PlayerId;
            string nomeAlvo = alvo.Data.PlayerName;

            // Igual ao Tsunami: Exiled remove o jogador sem criar DeadBody.
            alvo.Exiled();

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                alvo.NetId,
                (byte)RpcCalls.Exiled,
                SendOption.Reliable,
                -1);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            if (alvo.Data == null || !alvo.Data.IsDead)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Seguidor] Exiled não marcou o alvo como morto; o NPC continuará perseguindo.");
                return false;
            }

            EventoCriarCorpoFalso.ExecutarComoNoisemakerParaJogadorNaPosicao(
                playerId, posicaoMorte);

            float cooldown = ObterCooldownKillNpc();
            proximoKillNpc = Time.realtimeSinceStartup + cooldown;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] NPC eliminou " + nomeAlvo + " (PlayerId=" + playerId +
                ") via Exiled, sem corpo original; corpo falso Noisemaker solicitado em " +
                posicaoMorte + ". Próximo kill em " + cooldown.ToString("0.##") + "s.");
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Seguidor] Falha ao eliminar o alvo como Noisemaker: " + ex);
            return false;
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
        proximaAtualizacaoTransformVanilla = 0f;
        avisoSyncTransformVanilla = false;
        netIdSeguidorCliente = 0;
        alvoReplicadoCliente = byte.MaxValue;
        destinoReplicadoCliente = Vector2.zero;
        destinoReplicadoRecebido = false;
        ultimoAlvoNotificado = byte.MaxValue;
        alvoInicialNotificado = false;
        proximaEtapa = 0f;
        proximaBuscaAlvo = 0f;
        proximaReplicacaoDestino = 0f;
        ultimoDestinoReplicado = Vector2.zero;
        ultimoAlvoReplicado = byte.MaxValue;
        destinoReplicadoInicializado = false;
        alvoIgnoradoNavegacao = null;
        alcanceVisaoTripulanteCacheado = 0f;
        proximaAtualizacaoAlcanceVisao = 0f;
        proximaTentativaAbrirPorta = 0f;
        alvoAtual = null;
        inteligencia.Resetar();
        jogadoresParaIA.Clear();
        possuiDestinoPatrulha = false;
        destinoPatrulha = Vector2.zero;
        proximaTentativaDestinoPatrulha = 0f;
        ultimaSalaPatrulha = SystemTypes.Hallway;
        possuiUltimaSalaPatrulha = false;
        proximoKillNpc = 0f;
        resolvendoSabotagemLuzes = false;
        destinoEletrica = Vector2.zero;
        proximaAcaoInterruptor = 0f;
        resolvendoOutraSabotagem = false;
        sabotagemAtual = SystemTypes.Hallway;
        destinoOutraSabotagem = Vector2.zero;
        consoleO2Atual = 0;
        aguardandoSegundoJogadorReator = false;
        proximaTentativaReparo = 0f;
        aguardarPerseguicaoAte = 0f;
        ultimaSabotagemSemSala = SystemTypes.Hallway;
        avisoSabotagemSemSala = false;
        sistemasSemEstadoAtivoDiagnosticados.Clear();
        sistemasTipadosCache.Clear();
        transporteEmAndamento = false;
        aguardandoSaidaVent = false;
        ventEntradaAtual = null;
        ventSaidaAtual = null;
        proximaTentativaVent = 0f;
        limiteTransporte = 0f;
        proximoTransporte = 0f;
        instanteSpawnNpc = 0f;
        proximoSnapToRede = 0f;
        sequenciaSnapToRede = 0;
        sequenciaSnapToRedeInicializada = false;
        monitoramentoMovimentoIniciado = false;
        inicioSemDeslocamento = 0f;
        avisoMonitoramentoMovimento = false;
        ultimaPosicaoMonitorada = Vector2.zero;
        proximaVerificacaoTravamento = 0f;
        falhasConsecutivasRota = 0;
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
                    PararCorpo(seguidor.NetTransform.body, "Resetar (linha 3555)");
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
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

    // Caminho A*: recalculado periodicamente e quando o alvo muda de posição.
    private const float TamanhoCelula = 0.35f;
    // Margem um pouco maior que o raio usado antes para o NPC não raspar nas paredes.
    // O mesmo valor é usado no A*, na suavização e na validação dos movimentos.
    private const float RaioColisao = 0.24f;
    private const float IntervaloRecalculoCaminho = 0.65f;
    private const float DistanciaRecalculoAlvo = 0.65f;
    private static readonly Vector2Int[] DirecoesCaminho =
    {
        new Vector2Int(1, 0), new Vector2Int(-1, 0),
        new Vector2Int(0, 1), new Vector2Int(0, -1),
        new Vector2Int(1, 1), new Vector2Int(1, -1),
        new Vector2Int(-1, 1), new Vector2Int(-1, -1)
    };
    private static List<Vector2> caminhoAtual = new();
    private static int indiceCaminho;
    private static Vector2 destinoCaminhoAnterior;
    private static float proximoRecalculoCaminho;

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
            1 => SystemTypes.Reactor,       // MIRA HQ
            2 => SystemTypes.Specimens,     // Polus
            4 => SystemTypes.Electrical,     // Airship
            5 => SystemTypes.Laboratory,    // The Fungle
            _ => SystemTypes.Hallway
        };

        if (mapa != 0 && mapa != 1 && mapa != 2 && mapa != 4 && mapa != 5)
            return false;

        foreach (var sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || sala.roomArea == null || sala.RoomId != salaDesejada)
                continue;

            centro = sala.roomArea.bounds.center;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Seguidor] Spawn definido no centro da sala " + salaDesejada +
                " (mapa " + mapa + "): " + centro + ".");
            return true;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "[F7/Seguidor] Sala " + salaDesejada + " não encontrada em AllRooms no mapa " + mapa + ".");
        return false;
    }

    private static bool rpcMoveDiagnosticoExecutado;

    private static void DiagnosticarRpcMove()
    {
        if (rpcMoveDiagnosticoExecutado || seguidor == null)
            return;

        rpcMoveDiagnosticoExecutado = true;
        var tipos = new[]
        {
            seguidor.MyPhysics != null ? seguidor.MyPhysics.GetType() : null,
            seguidor.NetTransform != null ? seguidor.NetTransform.GetType() : null
        };

        foreach (var tipo in tipos)
        {
            if (tipo == null)
                continue;

            var metodos = tipo.GetMethods(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic);

            bool encontrou = false;
            foreach (var metodo in metodos)
            {
                if (!string.Equals(metodo.Name, "RpcMove", StringComparison.OrdinalIgnoreCase))
                    continue;

                encontrou = true;
                string parametros = string.Join(", ", Array.ConvertAll(
                    metodo.GetParameters(), p => p.ParameterType.FullName));
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] RPCMOVE ENCONTRADO em " + tipo.FullName +
                    ": " + metodo.Name + "(" + parametros + ") -> " +
                    metodo.ReturnType.FullName + ".");
            }

            if (!encontrou)
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Seguidor] Nenhum método RpcMove encontrado em " + tipo.FullName + ".");
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

        // Verifica em runtime se esta versão expõe RpcMove no PlayerPhysics
        // ou no CustomNetworkTransform, sem presumir uma assinatura inexistente.
        DiagnosticarRpcMove();

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

        MoverAte(destino);
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
        Vector2 direcao = proximoPonto - origem;
        if (direcao.sqrMagnitude < 0.0001f)
        {
            seguidor.NetTransform.body.velocity = Vector2.zero;
            return;
        }

        seguidor.NetTransform.body.velocity = direcao.normalized * Velocidade;
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

                // Impede cortar diagonalmente pelo canto de duas paredes.
                if (direcao.x != 0 && direcao.y != 0)
                {
                    Vector2Int lateralX = atual + new Vector2Int(direcao.x, 0);
                    Vector2Int lateralY = atual + new Vector2Int(0, direcao.y);

                    if (!CelulaLivre(lateralX, minimo, cacheCelulasLivres) ||
                        !CelulaLivre(lateralY, minimo, cacheCelulasLivres))
                        continue;
                }

                Vector2 pontoAtual = CentroCelula(atual, minimo);
                Vector2 pontoVizinho = CentroCelula(vizinho, minimo);

                if (!PodePassarEntre(pontoAtual, pontoVizinho))
                    continue;

                float custoPasso = direcao.x != 0 && direcao.y != 0 ? 1.4142135f : 1f;
                float novoCusto = custos[atual] + custoPasso;

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

    private static bool CelulaLivre(
        Vector2Int celula, Vector2 minimo, Dictionary<Vector2Int, bool> cache)
    {
        if (cache.TryGetValue(celula, out bool livre))
            return livre;

        Vector2 centro = CentroCelula(celula, minimo);
        Collider2D[] colisoes = Physics2D.OverlapCircleAll(
            centro, RaioColisao, Constants.ShipAndAllObjectsMask);

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

        RaycastHit2D[] impactos = Physics2D.CircleCastAll(
            origem, RaioColisao, delta / distancia, distancia,
            Constants.ShipAndAllObjectsMask);

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
        Collider2D[] sobreposicoes = Physics2D.OverlapCircleAll(destino, RaioColisao, mascara);

        foreach (Collider2D colisor in sobreposicoes)
        {
            if (colisor == null || colisor.isTrigger ||
                colisor.transform.IsChildOf(seguidor.transform))
                continue;
            return false;
        }

        Vector2 delta = destino - origem;
        float distancia = delta.magnitude;
        if (distancia <= 0.001f)
            return true;

        RaycastHit2D[] impactos = Physics2D.CircleCastAll(
            origem, RaioColisao, delta / distancia, distancia, mascara);

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

    private static float Heuristica(Vector2Int a, Vector2Int b)
    {
        int dx = Mathf.Abs(a.x - b.x);
        int dy = Mathf.Abs(a.y - b.y);
        return Mathf.Max(dx, dy) + 0.4142135f * Mathf.Min(dx, dy);
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
                if (PodeMoverEntre(caminho[indice], caminho[teste]))
                {
                    maisDistante = teste;
                    break;
                }
            }

            indice = maisDistante;
        }

        return suavizado;
    }

    private static void LimparCaminho()
    {
        caminhoAtual.Clear();
        indiceCaminho = 0;
        destinoCaminhoAnterior = Vector2.zero;
        proximoRecalculoCaminho = 0f;
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
        ativo = false;
        armado = false;
        aguardandoFimRoleReveal = false;
        etapaInicializacao = 0;
        rpcMoveDiagnosticoExecutado = false;
        proximaEtapa = 0f;
        proximaBuscaAlvo = 0f;
        alvoAtual = null;
        transporteEmAndamento = false;
        limiteTransporte = 0f;
        proximoTransporte = 0f;
        LimparCaminho();

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

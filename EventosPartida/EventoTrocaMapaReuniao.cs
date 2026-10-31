using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AmongUs.InnerNet.GameDataMessages;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Hazel;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using InnerNet;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace YouTubeVoteBridge;

internal sealed class EventoTrocaMapaReuniao : EventoVotacao
{
    private sealed class EstadoTarefas
    {
        public PlayerControl Jogador;
        public int Total;
        public int Concluidas;
    }

    private static bool armado;
    private static bool ativo;
    private static bool emAndamento;
    private static ShipStatus ultimoShipStatus;
    private static int mapaAtual = -1;
    private static bool suprimirRestauracaoLocal;
    private static float fimSupressaoLocal;
    private static int atribuicoesEsperadas;
    private static readonly HashSet<byte> jogadoresComTarefasRecebidas = new();
    private static float ultimaTrocaConcluida = -100f;

    public override string Nome => "Troca de mapa depois da ejeção";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        armado = true;
        if (LobbyTagManager.PartidaIniciada())
            ativo = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Troca de mapa] Evento armado: o mapa será trocado depois da tela de ejeção.");
    }

    internal static bool EmAndamento => emAndamento;

    internal static void IniciarPartida()
    {
        if (armado)
        {
            ativo = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Troca de mapa] Evento ativado para esta partida.");
        }
    }

    internal static bool DeveSuprimirRestauracao
    {
        get
        {
            if (suprimirRestauracaoLocal && Time.realtimeSinceStartup > fimSupressaoLocal)
                LimparSupressaoLocal();

            return emAndamento || suprimirRestauracaoLocal;
        }
    }

    internal static void AoShipStatusIniciado(ShipStatus ship)
    {
        if (ship == null)
            return;

        int idDetectado = ObterIdMapa(ship);
        if (idDetectado >= 0)
            mapaAtual = idDetectado;

        if (ultimoShipStatus == null)
        {
            ultimoShipStatus = ship;
            return;
        }

        if (ultimoShipStatus == ship)
            return;

        ultimoShipStatus = ship;

        // Todos os clientes com o mod detectam a chegada do novo ShipStatus.
        // Isso impede que a restauração antiga por tipo de tarefa sobrescreva
        // a restauração por quantidade que o host está fazendo.
        suprimirRestauracaoLocal = true;
        fimSupressaoLocal = Time.realtimeSinceStartup + 6f;
        atribuicoesEsperadas = ContarJogadoresConectados();
        jogadoresComTarefasRecebidas.Clear();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Troca de mapa] Novo ShipStatus detectado; supressão da restauração antiga ativada para {atribuicoesEsperadas} atribuições.");
    }

    internal static void RegistrarAtribuicaoRecebida(NetworkedPlayerInfo dados)
    {
        if (!suprimirRestauracaoLocal || dados == null)
            return;

        PlayerControl jogador = dados.Object;
        if (jogador != null)
            jogadoresComTarefasRecebidas.Add(jogador.PlayerId);

        if (atribuicoesEsperadas > 0 &&
            jogadoresComTarefasRecebidas.Count >= atribuicoesEsperadas)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Troca de mapa] Todas as listas de tarefas novas chegaram; a supressão temporária continuará ativa para impedir restaurações atrasadas.");
        }
    }

    private static int ContarJogadoresConectados()
    {
        int total = 0;
        if (PlayerControl.AllPlayerControls == null)
            return total;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null && !jogador.Data.Disconnected)
                total++;
        }

        return total;
    }

    private static void LimparSupressaoLocal()
    {
        suprimirRestauracaoLocal = false;
        fimSupressaoLocal = 0f;
        atribuicoesEsperadas = 0;
        jogadoresComTarefasRecebidas.Clear();
    }

    // Chamado quando a tela de ejeção termina. A reunião já foi encerrada,
    // então não precisamos interceptar StartMeeting/RpcStartMeeting nem retomá-los.
    internal static void AoTerminarEjecao()
    {
        if (!ativo || emAndamento || !YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null || AmongUsClient.Instance == null)
            return;

        if (Time.realtimeSinceStartup - ultimaTrocaConcluida < 3f)
            return;

        emAndamento = true;
        try
        {
            AmongUsClient.Instance.StartCoroutine(
                TrocarMapaDepoisDaEjecao().WrapToIl2Cpp());
        }
        catch (Exception ex)
        {
            emAndamento = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Troca de mapa] Não foi possível iniciar a troca após a ejeção: " + ex);
        }
    }

    private static IEnumerator TrocarMapaDepoisDaEjecao()
    {
        ShipStatus mapaAntigo = ShipStatus.Instance;
        int idAntigo = mapaAtual >= 0 ? mapaAtual : ObterIdMapa(mapaAntigo);
        int idNovo = EscolherNovoMapa(idAntigo);

        if (idNovo < 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Não há outro mapa disponível.");
            FinalizarTroca();
            yield break;
        }

        List<EstadoTarefas> estados = CapturarEstadoTarefas();
        AsyncOperationHandle<GameObject> operacao = default;
        bool falhaInstanciacao = false;

        try
        {
            operacao = AmongUsClient.Instance.ShipPrefabs[idNovo].InstantiateAsync(null, false);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[F7/Troca de mapa] Falha ao iniciar a instanciação do mapa {idNovo}: {ex}");
            falhaInstanciacao = true;
        }

        if (falhaInstanciacao)
        {
            FinalizarTroca();
            yield break;
        }

        yield return operacao;

        if (operacao.Status != AsyncOperationStatus.Succeeded || operacao.Result == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[F7/Troca de mapa] Não foi possível carregar o mapa {idNovo}.");
            FinalizarTroca();
            yield break;
        }

        ShipStatus mapaNovo = operacao.Result.GetComponent<ShipStatus>();
        if (mapaNovo == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[F7/Troca de mapa] O prefab {idNovo} não contém ShipStatus.");
            UnityEngine.Object.Destroy(operacao.Result);
            FinalizarTroca();
            yield break;
        }

        try
        {
            // Mesmo mecanismo usado pelo Hydra: BatchSpawn serializa e envia
            // a criação do ShipStatus pela rede. O mapa antigo só é removido
            // depois que o novo spawn é enfileirado no mesmo lote.
            BatchedMessage lote = new BatchedMessage();
            lote.QueueSpawn(mapaNovo, -2, SpawnFlags.None);

            if (mapaAntigo != null && mapaAntigo != mapaNovo)
                lote.QueueDespawn(mapaAntigo);

            lote.FinishBatch();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Troca de mapa] Falha ao enviar o novo mapa: " + ex);
            FinalizarTroca();
            yield break;
        }

        // Aguarda todos os clientes materializarem o ShipStatus recém-criado.
        yield return null;
        yield return null;
        yield return new WaitForSeconds(0.35f);

        if (ShipStatus.Instance != null)
            mapaNovo = ShipStatus.Instance;

        mapaAtual = idNovo;
        ultimoShipStatus = mapaNovo;
        AtualizarConfiguracaoMapa(idNovo);

        // As tarefas são atribuídas no mapa novo antes de reposicionar os jogadores.
        AtribuirTarefasDoNovoMapa(mapaNovo, estados);
        yield return new WaitForSeconds(0.45f);
        RestaurarQuantidadeDeTarefasConcluidas(estados);

        try { GameData.Instance?.RecomputeTaskCounts(); }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Falha ao recalcular tarefas: " + ex.Message);
        }

        // O host calcula os pontos de spawn do novo ShipStatus e envia um SnapTo
        // confiável para cada jogador. Assim, os clientes recebem as posições
        // autoritativas em vez de dependerem do transform local antigo.
        yield return new WaitForSeconds(0.2f);
        ReposicionarJogadoresNaRede(mapaNovo);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Troca de mapa] Mapa alterado de {idAntigo} para {idNovo}; tarefas restauradas e jogadores reposicionados pela rede.");

        FinalizarTroca();
    }

    private static void ReposicionarJogadoresNaRede(ShipStatus mapa)
    {
        if (mapa == null || PlayerControl.AllPlayerControls == null ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;

        int quantidadeJogadores = ContarJogadoresConectados();
        int reposicionados = 0;
        Vector2 posicaoBotao;

        if (!TentarObterPosicaoBotaoEmergencia(mapa, out posicaoBotao))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Não encontrei o botão de emergência no novo mapa; reposicionamento cancelado para não enviar jogadores a coordenadas incorretas.");
            return;
        }

        // Experimento solicitado: todos recebem SnapTo para o mesmo ponto
        // do botão. A colisão do próprio jogo poderá separar os personagens.
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected ||
                jogador.NetTransform == null)
                continue;

            try
            {
                jogador.NetTransform.RpcSnapTo(posicaoBotao);
                reposicionados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[F7/Troca de mapa] Não foi possível reposicionar PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Troca de mapa] Experimento de colisão: {reposicionados}/{quantidadeJogadores} jogador(es) enviados ao botão de emergência em ({posicaoBotao.x:F2}, {posicaoBotao.y:F2}).");
    }

    private static bool TentarObterPosicaoBotaoEmergencia(ShipStatus mapa, out Vector2 posicao)
    {
        posicao = Vector2.zero;
        if (mapa == null)
            return false;

        try
        {
            Transform[] transforms = mapa.GetComponentsInChildren<Transform>(true);
            foreach (Transform item in transforms)
            {
                if (item == null)
                    continue;

                string nome = (item.name ?? "").ToLowerInvariant();
                bool pareceBotao =
                    (nome.Contains("emergency") || nome.Contains("meeting")) &&
                    (nome.Contains("button") || nome.Contains("console") || nome.Contains("table"));

                if (!pareceBotao)
                    continue;

                Vector3 p = item.position;
                posicao = new Vector2(p.x, p.y);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[F7/Troca de mapa] Âncora de emergência encontrada: {item.name} em ({p.x:F2}, {p.y:F2}).");
                return true;
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Erro ao procurar botão de emergência: " + ex.GetBaseException().Message);
        }

        return false;
    }

    private static Vector2 ObterPosicaoSpawn(
        ShipStatus mapa, PlayerControl jogador, int quantidadeJogadores)
    {
        // GetSpawnLocation é consultado por reflexão para tolerar pequenas
        // diferenças de assinatura entre versões do Among Us.
        MethodInfo metodo = AccessTools.Method(mapa.GetType(), "GetSpawnLocation") ??
                            AccessTools.Method(typeof(ShipStatus), "GetSpawnLocation");

        if (metodo != null)
        {
            ParameterInfo[] parametros = metodo.GetParameters();
            object[] argumentos = new object[parametros.Length];

            for (int i = 0; i < parametros.Length; i++)
            {
                Type tipo = parametros[i].ParameterType;
                if (tipo == typeof(byte))
                    argumentos[i] = jogador.PlayerId;
                else if (tipo == typeof(int))
                    argumentos[i] = i == 0 ? (int)jogador.PlayerId : quantidadeJogadores;
                else if (tipo == typeof(bool))
                    argumentos[i] = jogador.Data.IsDead;
                else if (tipo == typeof(PlayerControl))
                    argumentos[i] = jogador;
                else if (tipo == typeof(NetworkedPlayerInfo))
                    argumentos[i] = jogador.Data;
                else if (tipo == typeof(Vector2))
                    argumentos[i] = Vector2.zero;
                else if (tipo == typeof(Vector3))
                    argumentos[i] = Vector3.zero;
                else if (tipo.IsEnum)
                    argumentos[i] = Activator.CreateInstance(tipo);
                else
                    argumentos[i] = tipo.IsValueType ? Activator.CreateInstance(tipo) : null;
            }

            object resultado = metodo.Invoke(mapa, argumentos);
            if (resultado is Vector2 v2)
                return v2;
            if (resultado is Vector3 v3)
                return new Vector2(v3.x, v3.y);
        }

        // Não envia coordenadas antigas se a API de spawn não for encontrada.
        throw new MissingMethodException(
            "ShipStatus.GetSpawnLocation não foi encontrado ou não retornou Vector2/Vector3.");
    }

    private static void FinalizarTroca()
    {
        emAndamento = false;
        ultimaTrocaConcluida = Time.realtimeSinceStartup;
    }

    private static int EscolherNovoMapa(int mapaAntigo)
    {
        int total = 0;
        try { total = AmongUsClient.Instance.ShipPrefabs.Count; }
        catch { }

        List<int> candidatos = new();
        for (int i = 0; i < total; i++)
        {
            if (i == mapaAntigo)
                continue;

            try
            {
                if (AmongUsClient.Instance.ShipPrefabs[i] != null)
                    candidatos.Add(i);
            }
            catch { }
        }

        if (candidatos.Count == 0)
            return -1;

        return candidatos[UnityEngine.Random.Range(0, candidatos.Count)];
    }

    private static int ObterIdMapa(ShipStatus ship)
    {
        // Como no Hydra, quando existe ShipStatus o SpawnId identifica o mapa
        // efetivamente materializado. MapId das opções pode estar desatualizado.
        if (ship != null)
        {
            try
            {
                object spawnId = LerMembro(ship, "SpawnId", "spawnId");
                string nomeSpawn = spawnId?.ToString()?.ToLowerInvariant() ?? "";

                if (nomeSpawn.Contains("skeld")) return 0;
                if (nomeSpawn.Contains("mira")) return 1;
                if (nomeSpawn.Contains("polus")) return 2;
                if (nomeSpawn.Contains("dleks")) return 3;
                if (nomeSpawn.Contains("airship")) return 4;
                if (nomeSpawn.Contains("fungle")) return 5;

                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[F7/Troca de mapa] SpawnId não reconhecido: {spawnId ?? "(null)"}.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Troca de mapa] Não foi possível ler ShipStatus.SpawnId: " + ex.GetBaseException().Message);
            }
        }

        // Opções são fallback apenas quando o SpawnId não está disponível.
        try
        {
            object logicOptions = LerMembro(GameManager.Instance, "LogicOptions", "logicOptions");
            object idLogic = LerMembro(logicOptions, "MapId", "mapId");
            if (idLogic != null)
            {
                int id = Convert.ToInt32(idLogic);
                if (id >= 0 && id <= 5)
                    return id;
            }
        }
        catch { }

        try
        {
            object opcoes = LerMembro(GameOptionsManager.Instance,
                "CurrentGameOptions", "currentGameOptions");
            object idOpcoes = LerMembro(opcoes, "MapId", "mapId");
            if (idOpcoes != null)
            {
                int id = Convert.ToInt32(idOpcoes);
                if (id >= 0 && id <= 5)
                    return id;
            }
        }
        catch { }

        return mapaAtual;
    }

    private static void AtualizarConfiguracaoMapa(int idMapa)
    {
        if (idMapa < 0 || idMapa > 5)
            return;

        int atualizados = 0;
        object logicOptions = LerMembro(GameManager.Instance, "LogicOptions", "logicOptions");
        object gameOptions = LerMembro(GameOptionsManager.Instance,
            "CurrentGameOptions", "currentGameOptions");

        if (TentarDefinirMapId(logicOptions, idMapa))
            atualizados++;
        if (TentarDefinirMapId(gameOptions, idMapa))
            atualizados++;

        try
        {
            MethodInfo setDirty = logicOptions?.GetType().GetMethod(
                "SetDirty", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);
            setDirty?.Invoke(logicOptions, null);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Não foi possível marcar as opções do jogo como alteradas: " + ex.GetBaseException().Message);
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Troca de mapa] MapId das configurações atualizado para {idMapa} ({atualizados}/2 objetos).");
    }

    private static bool TentarDefinirMapId(object opcoes, int idMapa)
    {
        if (opcoes == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            PropertyInfo propriedade = opcoes.GetType().GetProperty("MapId", flags) ??
                                       opcoes.GetType().GetProperty("mapId", flags);
            if (propriedade != null && propriedade.CanWrite)
            {
                propriedade.SetValue(opcoes, ConverterMapId(idMapa, propriedade.PropertyType));
                return true;
            }

            FieldInfo campo = opcoes.GetType().GetField("MapId", flags) ??
                              opcoes.GetType().GetField("mapId", flags);
            if (campo != null)
            {
                campo.SetValue(opcoes, ConverterMapId(idMapa, campo.FieldType));
                return true;
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Falha ao atualizar MapId: " + ex.GetBaseException().Message);
        }

        return false;
    }

    private static object ConverterMapId(int idMapa, Type tipo)
    {
        Type tipoBase = Nullable.GetUnderlyingType(tipo) ?? tipo;
        if (tipoBase.IsEnum)
            return Enum.ToObject(tipoBase, idMapa);
        return Convert.ChangeType(idMapa, tipoBase);
    }

    private static List<EstadoTarefas> CapturarEstadoTarefas()
    {
        List<EstadoTarefas> estados = new();
        if (PlayerControl.AllPlayerControls == null)
            return estados;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            List<object> tarefasDados = Enumerar(LerMembro(jogador.Data, "Tasks", "tasks"));
            List<object> tarefasLocais = Enumerar(LerMembro(jogador, "myTasks", "MyTasks", "tasks", "Tasks"));

            List<object> tarefasUsadas = tarefasDados.Count > 0 ? tarefasDados : tarefasLocais;
            int concluidasDados = tarefasDados.Count(t => LerBool(t, "Complete", "complete"));
            int concluidasLocais = tarefasLocais.Count(t => LerBool(t, "IsComplete", "isComplete", "Complete", "complete"));

            estados.Add(new EstadoTarefas
            {
                Jogador = jogador,
                Total = tarefasUsadas.Count,
                Concluidas = Math.Min(tarefasUsadas.Count, Math.Max(concluidasDados, concluidasLocais))
            });
        }

        return estados;
    }

    private static void AtribuirTarefasDoNovoMapa(
        ShipStatus mapa,
        List<EstadoTarefas> estados)
    {
        if (mapa == null || estados == null)
            return;

        List<byte> catalogo = new();
        AdicionarIndices(mapa.CommonTasks, catalogo);
        AdicionarIndices(mapa.ShortTasks, catalogo);
        AdicionarIndices(mapa.LongTasks, catalogo);
        catalogo = catalogo.Distinct().ToList();

        if (catalogo.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] O novo mapa não possui catálogo de tarefas.");
            return;
        }

        foreach (EstadoTarefas estado in estados)
        {
            PlayerControl jogador = estado.Jogador;
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            List<byte> disponiveis = new(catalogo);
            Embaralhar(disponiveis);

            int quantidade = Math.Min(estado.Total, disponiveis.Count);
            Il2CppStructArray<byte> novosIds = new(quantidade);
            for (int i = 0; i < quantidade; i++)
                novosIds[i] = disponiveis[i];

            try
            {
                jogador.Data.RpcSetTasks(novosIds);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[F7/Troca de mapa] PlayerId={jogador.PlayerId}: {quantidade} tarefa(s) atribuída(s), {Math.Min(estado.Concluidas, quantidade)} concluída(s) a restaurar.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[F7/Troca de mapa] Falha ao atribuir tarefas para PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
            }
        }
    }

    private static void RestaurarQuantidadeDeTarefasConcluidas(List<EstadoTarefas> estados)
    {
        if (estados == null || GameData.Instance == null)
            return;

        foreach (EstadoTarefas estado in estados)
        {
            PlayerControl jogador = estado.Jogador;
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            List<object> tarefas = Enumerar(LerMembro(jogador.Data, "Tasks", "tasks"));
            int alvo = Math.Min(estado.Concluidas, tarefas.Count);
            int restauradas = 0;

            foreach (object tarefa in tarefas)
            {
                if (restauradas >= alvo)
                    break;

                if (tarefa == null || LerBool(tarefa, "Complete", "complete"))
                    continue;

                object idObj = LerMembro(tarefa, "Id", "id", "TaskId", "taskId");
                if (idObj == null)
                    continue;

                try
                {
                    uint taskId = Convert.ToUInt32(idObj);
                    GameData.Instance.CompleteTask(jogador, taskId);
                    restauradas++;
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[F7/Troca de mapa] Falha ao restaurar tarefa concluída de PlayerId={jogador.PlayerId}: {ex.GetBaseException().Message}");
                }
            }

            if (restauradas < alvo)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[F7/Troca de mapa] PlayerId={jogador.PlayerId}: só foi possível restaurar {restauradas}/{alvo} conclusões.");
            }
        }
    }

    private static void AdicionarIndices(
        Il2CppReferenceArray<NormalPlayerTask> tarefas,
        List<byte> destino)
    {
        if (tarefas == null)
            return;

        foreach (NormalPlayerTask tarefa in tarefas)
        {
            if (tarefa == null)
                continue;

            int indice = tarefa.Index;
            if (indice >= 0 && indice <= byte.MaxValue)
                destino.Add((byte)indice);
        }
    }

    private static void Embaralhar<T>(List<T> lista)
    {
        for (int i = lista.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (lista[i], lista[j]) = (lista[j], lista[i]);
        }
    }

    private static List<object> Enumerar(object colecao)
    {
        List<object> itens = new();
        if (colecao == null)
            return itens;

        try
        {
            Type tipo = colecao.GetType();
            object countObj = LerMembro(colecao, "Count", "count", "Length", "length");
            if (countObj == null)
                return itens;

            int count = Convert.ToInt32(countObj);
            PropertyInfo item = tipo.GetProperty(
                "Item",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            MethodInfo getter = item?.GetGetMethod(true);
            if (getter == null)
                return itens;

            for (int i = 0; i < count; i++)
            {
                try { itens.Add(getter.Invoke(colecao, new object[] { i })); }
                catch { break; }
            }
        }
        catch { }

        return itens;
    }

    private static object LerMembro(object objeto, params string[] nomes)
    {
        if (objeto == null)
            return null;

        foreach (string nome in nomes)
        {
            try
            {
                PropertyInfo propriedade = AccessTools.Property(objeto.GetType(), nome);
                if (propriedade != null)
                    return propriedade.GetValue(objeto);

                FieldInfo campo = AccessTools.Field(objeto.GetType(), nome);
                if (campo != null)
                    return campo.GetValue(objeto);
            }
            catch { }
        }

        return null;
    }

    private static bool LerBool(object objeto, params string[] nomes)
    {
        object valor = LerMembro(objeto, nomes);
        try { return valor != null && Convert.ToBoolean(valor); }
        catch { return false; }
    }

    internal static void Resetar()
    {
        armado = false;
        ativo = false;
        emAndamento = false;
        ultimoShipStatus = null;
        mapaAtual = -1;
        ultimaTrocaConcluida = -100f;
        LimparSupressaoLocal();
    }

    // WrapUp acontece no encerramento da sequência de ejeção.
    // O Postfix agenda a troca depois que a tela já terminou.
    [HarmonyPatch(typeof(ExileController), nameof(ExileController.WrapUp))]
    private static class TrocaMapaDepoisDaEjecaoPatch
    {
        private static void Postfix()
        {
            AoTerminarEjecao();
        }
    }

    [HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
    private static class TrocaMapaResetarAoFimPatch
    {
        private static void Postfix()
        {
            Resetar();
        }
    }
}

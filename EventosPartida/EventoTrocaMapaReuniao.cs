using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AmongUs.GameOptions;
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
    private static bool executandoMetodoOriginal;
    private static ShipStatus ultimoShipStatus;
    private static int mapaAtual = -1;
    private static bool suprimirRestauracaoLocal;
    private static float fimSupressaoLocal;
    private static int atribuicoesEsperadas;
    private static readonly HashSet<byte> jogadoresComTarefasRecebidas = new();
    private static float ultimaTrocaConcluida = -100f;

    public override string Nome => "Troca de mapa antes de cada reunião";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        armado = true;
        if (LobbyTagManager.PartidaIniciada())
            ativo = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Troca de mapa] Evento armado: o mapa será trocado antes de cada reunião.");
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

    internal static bool InterceptarInicioReuniao(
        PlayerControl instancia,
        MethodBase metodoOriginal,
        object[] argumentos)
    {
        if (executandoMetodoOriginal || !ativo || !YouTubeVoteBridgePlugin.EhHost())
            return true;

        if (MeetingHud.Instance != null)
            return true;

        // StartMeeting e RpcStartMeeting podem participar da mesma chamada.
        // Enquanto uma troca está em andamento, não deixamos uma segunda
        // chamada abrir a reunião antes de o mapa novo estar pronto.
        if (emAndamento)
            return false;

        // Algumas versões passam pelos dois métodos em sequência. A janela
        // evita que a mesma reunião provoque duas trocas.
        if (Time.realtimeSinceStartup - ultimaTrocaConcluida < 2.5f)
            return true;

        if (ShipStatus.Instance == null || AmongUsClient.Instance == null)
            return true;

        emAndamento = true;

        try
        {
            AmongUsClient.Instance.StartCoroutine(
                TrocarMapaEContinuarReuniao(instancia, metodoOriginal, argumentos).WrapToIl2Cpp());
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Troca de mapa] Solicitação de reunião pausada até a troca do mapa terminar.");
            return false;
        }
        catch (Exception ex)
        {
            emAndamento = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Troca de mapa] Não foi possível iniciar a troca: " + ex);
            return true;
        }
    }

    private static IEnumerator TrocarMapaEContinuarReuniao(
        PlayerControl instancia,
        MethodBase metodoOriginal,
        object[] argumentos)
    {
        ShipStatus mapaAntigo = ShipStatus.Instance;
        int idAntigo = mapaAtual >= 0 ? mapaAtual : ObterIdMapa(mapaAntigo);
        int idNovo = EscolherNovoMapa(idAntigo);

        if (idNovo < 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Não foi encontrado outro mapa disponível. A reunião será aberta no mapa atual.");
            ContinuarReuniao(instancia, metodoOriginal, argumentos);
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
            ContinuarReuniao(instancia, metodoOriginal, argumentos);
            yield break;
        }

        yield return operacao;

        if (operacao.Status != AsyncOperationStatus.Succeeded || operacao.Result == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[F7/Troca de mapa] Addressables não conseguiu carregar o mapa {idNovo}. A reunião continuará no mapa anterior.");
            ContinuarReuniao(instancia, metodoOriginal, argumentos);
            yield break;
        }

        ShipStatus mapaNovo = operacao.Result.GetComponent<ShipStatus>();
        if (mapaNovo == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[F7/Troca de mapa] O prefab {idNovo} não contém ShipStatus. A reunião continuará no mapa anterior.");
            UnityEngine.Object.Destroy(operacao.Result);
            ContinuarReuniao(instancia, metodoOriginal, argumentos);
            yield break;
        }

        bool falhaSpawn = false;
        try
        {
            // Mesmo fluxo do Hydra: o prefab é instanciado por Addressables,
            // o novo ShipStatus é preparado no pacote e o mapa antigo é despawnado
            // imediatamente antes de enviar o Spawn pela rede.
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                writer.StartMessage(InnerNet.Tags.GameData);
                writer.Write(AmongUsClient.Instance.GameId);

                SpawnGameDataMessage spawn = AmongUsClient.Instance.CreateSpawnMessage(
                    mapaNovo,
                    -2,
                    SpawnFlags.None);

                spawn.Serialize(writer);
                writer.EndMessage();

                mapaAntigo?.Despawn();
                AmongUsClient.Instance.SendOrDisconnect(writer);
            }
            finally
            {
                writer.Recycle();
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Troca de mapa] Falha ao despawnar/enviar o novo mapa: " + ex);
            falhaSpawn = true;
        }

        if (falhaSpawn)
        {
            ContinuarReuniao(instancia, metodoOriginal, argumentos);
            yield break;
        }

        // Aguarda o novo ShipStatus ser ativado e assumir o lugar do antigo.
        yield return null;
        yield return null;

        if (ShipStatus.Instance != null)
            mapaNovo = ShipStatus.Instance;

        mapaAtual = idNovo;
        ultimoShipStatus = mapaNovo;

        AtribuirTarefasDoNovoMapa(mapaNovo, estados);

        // Dá tempo para os TaskInfo novos serem materializados antes de enviar
        // as conclusões de volta pela GameData.
        yield return new WaitForSeconds(0.45f);
        RestaurarQuantidadeDeTarefasConcluidas(estados);

        try
        {
            GameData.Instance?.RecomputeTaskCounts();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Troca de mapa] Não foi possível recalcular a barra de tarefas: " + ex.Message);
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Troca de mapa] Mapa alterado de {idAntigo} para {idNovo}; tarefas redistribuídas e conclusões restauradas.");

        ultimaTrocaConcluida = Time.realtimeSinceStartup;
        ContinuarReuniao(instancia, metodoOriginal, argumentos);
    }

    private static void ContinuarReuniao(
        PlayerControl instancia,
        MethodBase metodoOriginal,
        object[] argumentos)
    {
        emAndamento = false;
        ultimaTrocaConcluida = Time.realtimeSinceStartup;

        if (instancia == null || metodoOriginal == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Troca de mapa] Não foi possível retomar a reunião: método ou jogador nulo.");
            return;
        }

        executandoMetodoOriginal = true;
        try
        {
            metodoOriginal.Invoke(instancia, argumentos);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Troca de mapa] Erro ao retomar a chamada original da reunião: " +
                ex.GetBaseException());
        }
        finally
        {
            executandoMetodoOriginal = false;
        }
    }

    private static int EscolherNovoMapa(int mapaAntigo)
    {
        int total = 0;
        try { total = AmongUsClient.Instance.ShipPrefabs.Length; }
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
        if (ship == null)
            return -1;

        string nome = ship.GetType().Name.ToLowerInvariant();

        if (nome.Contains("dleks")) return 3;
        if (nome.Contains("mira")) return 1;
        if (nome.Contains("polus")) return 2;
        if (nome.Contains("airship")) return 4;
        if (nome.Contains("fungle")) return 5;
        if (nome.Contains("skeld")) return 0;

        return mapaAtual;
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

    private static void Resetar()
    {
        armado = false;
        ativo = false;
        emAndamento = false;
        executandoMetodoOriginal = false;
        ultimoShipStatus = null;
        mapaAtual = -1;
        ultimaTrocaConcluida = -100f;
        LimparSupressaoLocal();
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
    private static class TrocaMapaAntesDeStartMeetingPatch
    {
        private static bool Prefix(
            PlayerControl __instance,
            MethodBase __originalMethod,
            object[] __args)
        {
            return InterceptarInicioReuniao(__instance, __originalMethod, __args);
        }
    }

    [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcStartMeeting))]
    private static class TrocaMapaAntesDeRpcStartMeetingPatch
    {
        private static bool Prefix(
            PlayerControl __instance,
            MethodBase __originalMethod,
            object[] __args)
        {
            return InterceptarInicioReuniao(__instance, __originalMethod, __args);
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

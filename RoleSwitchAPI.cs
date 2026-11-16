using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Troca funções somente quando solicitada explicitamente pelo host durante a partida.
/// A seleção inicial permanece inteiramente vanilla para preservar a quantidade
/// de impostores e as opções de função da sala.
/// </summary>
internal static class RoleSwitchAPI
{
    private sealed class TrocaPendente
    {
        public PlayerControl Jogador;
        public RoleTypes Funcao;

        public TrocaPendente(PlayerControl jogador, RoleTypes funcao)
        {
            Jogador = jogador;
            Funcao = funcao;
        }
    }

    private static bool loteEmAndamento;
    private static float inicioLoteRealtime;
    private static int identificadorLote;
    private const float TIMEOUT_LOTE_SEGUNDOS = 10f;

    // RoleMap inspirado no EHR: (seerId, targetId) => função que aquele
    // observador deve receber para o jogador-alvo.
    private static readonly Dictionary<(byte SeerId, byte TargetId), RoleTypes> RoleMap = new();
    private static readonly HashSet<byte> JogadoresDoMapa = new();
    private static bool mapaInicializado;

    private sealed class RoleCall
    {
        public PlayerControl Target;
        public RoleTypes Role;
        public int ClientId;

        public RoleCall(PlayerControl target, RoleTypes role, int clientId)
        {
            Target = target;
            Role = role;
            ClientId = clientId;
        }
    }

    internal static bool AplicarFuncaoGlobal(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || jogador.Data == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return false;

        try
        {
            GarantirRoleMapInicializado();
            AtualizarRoleMapCompleto(jogador, funcao);
            RpcSetRoleGlobal(jogador, funcao, atualizarMapa: false);
            AtualizarRegistroLocal(jogador);
            SistemaNomesLocais.Atualizar();
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"AplicarFuncaoGlobal falhou para PlayerId={jogador.PlayerId}: {ex}");
            return false;
        }
    }

    internal static bool TrocarFuncao(PlayerControl jogador, string nomeFuncao)
    {
        if (!Enum.TryParse(typeof(RoleTypes), nomeFuncao, true, out object valor))
        {
            LogAviso($"RoleTypes não contém a função '{nomeFuncao}'.");
            return false;
        }

        return TrocarFuncao(jogador, (RoleTypes)valor);
    }

    internal static bool TrocarFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null)
            return false;

        return TrocarFuncoes(new[]
        {
            new KeyValuePair<PlayerControl, RoleTypes>(jogador, funcao)
        });
    }

    /// <summary>
    /// API usada pelo controlador F8 e por recursos do mod para trocar funções
    /// explicitamente durante a partida.
    /// </summary>
    internal static bool TrocarFuncoes(
        IEnumerable<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes)
    {
        List<TrocaPendente> trocas = new();
        HashSet<byte> idsAdicionados = new();

        if (atribuicoes != null)
        {
            foreach (KeyValuePair<PlayerControl, RoleTypes> item in atribuicoes)
            {
                PlayerControl jogador = item.Key;
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                {
                    LogAviso("Uma atribuição foi ignorada porque o jogador é inválido ou está desconectado.");
                    continue;
                }

                if (!idsAdicionados.Add(jogador.PlayerId))
                    continue;

                trocas.Add(new TrocaPendente(jogador, item.Value));
            }
        }

        // Somente operações explícitas durante a partida chegam aqui.
        // A seleção inicial é executada integralmente pelo jogo.
        return ExecutarAtribuicoes(trocas);
    }

    private static bool ExecutarAtribuicoes(List<TrocaPendente> trocas)
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        PlayerControl local = PlayerControl.LocalPlayer;

        if (cliente == null || !cliente.AmHost)
        {
            LogAviso("A atribuição de funções exige que o cliente seja o host.");
            return false;
        }

        if (RoleManager.Instance == null || local == null)
        {
            LogErro("RoleManager.Instance ou PlayerControl.LocalPlayer está nulo.");
            return false;
        }

        if (loteEmAndamento)
        {
            LogAviso("Já existe uma atribuição de funções em andamento.");
            return false;
        }

        trocas = trocas?
            .Where(x => x != null && x.Jogador != null &&
                        x.Jogador.Data != null && !x.Jogador.Data.Disconnected)
            .GroupBy(x => x.Jogador.PlayerId)
            .Select(g => g.Last())
            .ToList() ?? new List<TrocaPendente>();

        if (trocas.Count == 0)
        {
            LogAviso("Nenhuma atribuição válida foi recebida.");
            return false;
        }

        loteEmAndamento = true;
        inicioLoteRealtime = Time.realtimeSinceStartup;
        int loteAtual = ++identificadorLote;
        YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = true;

        try
        {
            // Troca global no estilo EHR: aplica localmente e transmite o RPC
            // vanilla SetRole com canOverrideRole=true para todos os clientes.
            GarantirRoleMapInicializado();

            foreach (TrocaPendente troca in trocas)
                AplicarFuncaoComRoleMap(troca.Jogador, troca.Funcao);

            AtualizarHudDaFuncaoLocal(trocas);
            SistemaNomesLocais.Atualizar();
            LogInfo($"Lote {loteAtual} enviado pelo fluxo global do EHR com {trocas.Count} atribuição(ões). Nenhum jogador será revivido ou rebaixado para Crewmate durante a troca.");
            MonoBehaviourExtensions.StartCoroutine(
                local, FinalizarLoteAposTroca(loteAtual));
            return true;
        }
        catch (Exception ex)
        {
            if (loteAtual == identificadorLote)
            {
                loteEmAndamento = false;
                YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;
            }

            LogErro("Não foi possível iniciar a distribuição das funções: " + ex);
            return false;
        }
    }

    private static System.Collections.IEnumerator FinalizarLoteAposTroca(int loteAtual)
    {
        // Dá tempo para CoSetRole e o RPC vanilla serem processados sem alterar
        // novamente as funções ou o estado de vida dos jogadores.
        yield return new WaitForSecondsRealtime(0.75f);

        if (loteAtual != identificadorLote)
            yield break;

        loteEmAndamento = false;
        YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;
        LogInfo($"Lote {loteAtual} finalizado. Funções enviadas; estado de vida original preservado.");
    }

    /// <summary>
    /// Chamado pelo Update do jogo. Recupera a API se a coroutine de restauração
    /// não avançar ou for interrompida sem executar o finally.
    /// </summary>
    internal static void ProcessarEstado()
    {
        if (!loteEmAndamento)
            return;

        float decorrido = Time.realtimeSinceStartup - inicioLoteRealtime;
        if (decorrido < TIMEOUT_LOTE_SEGUNDOS)
            return;

        int loteTravado = identificadorLote;
        identificadorLote++;
        loteEmAndamento = false;
        YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;

        LogErro($"Lote {loteTravado} excedeu {TIMEOUT_LOTE_SEGUNDOS:0} segundos sem finalizar. Estado liberado pelo watchdog após {decorrido:0.0}s; a rotina antiga não poderá alterar o estado de um próximo lote.");
    }

    /// <summary>
    /// Equivalente ao RpcSetRoleGlobal do EHR para funções globais.
    /// Aplica no host e transmite SetRole bruto para todos os clientes.
    /// </summary>
    private static void RpcSetRoleGlobal(PlayerControl jogador, RoleTypes funcao, bool atualizarMapa = false)
    {
        if (jogador == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
            return;

        try
        {
            if (atualizarMapa)
            {
                GarantirRoleMapInicializado();
                AtualizarRoleMapCompleto(jogador, funcao);
            }

            // Usa o método vanilla completo. Ele aplica a função localmente
            // e envia o RPC SetRole no formato esperado pelo próprio jogo.
            // Evita duplicar manualmente CoSetRole + StartRpcImmediately.
            jogador.RpcSetRole(funcao, true);

            // Registra a função solicitada imediatamente, sem depender do
            // momento em que Data.Role é atualizado pela coroutine vanilla.
            AtualizarRegistroLocal(jogador, funcao);

            LogInfo($"RpcSetRoleGlobal vanilla: PlayerId={jogador.PlayerId}, Role={funcao}, canOverrideRole=true.");
        }
        catch (Exception ex)
        {
            LogErro($"RpcSetRoleGlobal falhou para PlayerId={jogador.PlayerId}: {ex}");
            throw;
        }
    }

    /// <summary>
    /// Equivalente ao RpcSetRoleDesync do EHR: envia SetRole bruto somente
    /// ao cliente indicado. Útil para funções diferentes por observador.
    /// </summary>
    internal static bool RpcSetRoleDesync(PlayerControl jogador, RoleTypes funcao, int clientId)
    {
        if (jogador == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost || clientId < 0)
            return false;

        try
        {
            PlayerControl observador = EncontrarJogadorPorClientId(clientId);
            if (observador != null)
                RoleMap[(observador.PlayerId, jogador.PlayerId)] = funcao;

            if (AmongUsClient.Instance.ClientId == clientId)
            {
                // Mesmo comportamento do EHR: se o destinatário é o próprio
                // host, aplica a função localmente sem enviar RPC para si mesmo.
                if (AmongUsClient.Instance.AmClient)
                    jogador.StartCoroutine(jogador.CoSetRole(funcao, true));
                AtualizarRegistroLocal(jogador);
                LogInfo($"RpcSetRoleDesync local EHR-style: PlayerId={jogador.PlayerId}, cliente={clientId}, Role={funcao}.");
                return true;
            }

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                clientId);
            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            LogInfo($"RpcSetRoleDesync EHR-style: PlayerId={jogador.PlayerId}, cliente={clientId}, Role={funcao}, canOverrideRole=true.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro($"RpcSetRoleDesync falhou: {ex}");
            return false;
        }
    }


    /// <summary>
    /// Inicializa a matriz de funções observadas usando o estado vanilla atual.
    /// A inicialização é tardia: ocorre na primeira troca, quando as funções
    /// já foram distribuídas pelo jogo.
    /// </summary>
    private static void GarantirRoleMapInicializado()
    {
        if (PlayerControl.AllPlayerControls == null)
            return;

        List<PlayerControl> jogadores = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;
            jogadores.Add(jogador);
        }

        HashSet<byte> idsAtuais = jogadores.Select(p => p.PlayerId).ToHashSet();
        if (mapaInicializado && idsAtuais.SetEquals(JogadoresDoMapa))
            return;

        RoleMap.Clear();
        JogadoresDoMapa.Clear();

        foreach (PlayerControl jogador in jogadores)
            JogadoresDoMapa.Add(jogador.PlayerId);

        foreach (PlayerControl seer in jogadores)
        {
            foreach (PlayerControl target in jogadores)
                RoleMap[(seer.PlayerId, target.PlayerId)] = target.Data.RoleType;
        }

        mapaInicializado = true;
        LogInfo($"RoleMap inicializado a partir das funções vanilla atuais: {jogadores.Count} jogadores, {RoleMap.Count} relações observador/alvo.");
    }

    /// <summary>
    /// Atualiza todas as visões de um alvo. Usado quando a operação deve ser
    /// global, como a conversão do Oportunista.
    /// </summary>
    private static void AtualizarRoleMapCompleto(PlayerControl target, RoleTypes role)
    {
        if (target == null || !mapaInicializado)
            return;

        foreach (byte seerId in JogadoresDoMapa)
            RoleMap[(seerId, target.PlayerId)] = role;
    }

    /// <summary>
    /// Equivalente simplificado ao caminho normal->normal do RpcChangeRoleBasis.
    /// Preserva entradas que já diferem da visão-base do próprio alvo e envia
    /// correções individuais depois do SetRole global, quando necessário.
    /// </summary>
    private static void AplicarFuncaoComRoleMap(PlayerControl target, RoleTypes novaFuncao)
    {
        if (target == null || target.Data == null)
            return;

        GarantirRoleMapInicializado();

        // Neste mod, as trocas solicitadas são globais: todos os clientes
        // devem enxergar a mesma função. O caminho desync do EHR só é
        // necessário quando existe uma função diferente por observador.
        AtualizarRoleMapCompleto(target, novaFuncao);
        RpcSetRoleGlobal(target, novaFuncao);
        AtualizarRegistroLocal(target, novaFuncao);

        LogInfo($"Troca global aplicada pelo RpcSetRole vanilla: PlayerId={target.PlayerId}, Role={novaFuncao}.");
    }

    private static System.Collections.IEnumerator EnviarCorrecoesAposRoleGlobal(
        List<RoleCall> correcoes, RoleTypes funcaoGlobal)
    {
        yield return new WaitForSecondsRealtime(0.2f);

        foreach (RoleCall correcao in correcoes)
        {
            if (correcao == null || correcao.Target == null ||
                correcao.Role == funcaoGlobal)
                continue;

            RpcSetRoleDesync(
                correcao.Target, correcao.Role, correcao.ClientId);
        }
    }

    private static List<PlayerControl> ObterJogadoresDoMapa()
    {
        List<PlayerControl> resultado = new();
        if (PlayerControl.AllPlayerControls == null)
            return resultado;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null &&
                !jogador.Data.Disconnected && JogadoresDoMapa.Contains(jogador.PlayerId))
                resultado.Add(jogador);
        }

        return resultado;
    }

    private static PlayerControl EncontrarJogadorPorClientId(int clientId)
    {
        foreach (PlayerControl jogador in ObterJogadoresDoMapa())
            if (jogador.OwnerId == clientId)
                return jogador;
        return null;
    }

    private static void AtualizarRegistroLocal(
        PlayerControl jogador, RoleTypes? funcaoForcada = null)
    {
        if (jogador == null || jogador.Data == null)
            return;

        RoleTypes funcaoLocal = funcaoForcada ??
            (jogador.Data.Role != null ? jogador.Data.Role.Role : jogador.Data.RoleType);

        PlayerControl local = PlayerControl.LocalPlayer;
        if (!funcaoForcada.HasValue && local != null &&
            RoleMap.TryGetValue((local.PlayerId, jogador.PlayerId), out RoleTypes roleMapLocal))
            funcaoLocal = roleMapLocal;

        RegistroFuncoesJogadores.Atribuicao atual =
            RegistroFuncoesJogadores.Obter(jogador.PlayerId);
        RegistroFuncoesJogadores.Registrar(
            jogador, funcaoLocal, atual?.FuncaoPersonalizada);
    }

    internal static void LimparMapa()
    {
        RoleMap.Clear();
        JogadoresDoMapa.Clear();
        mapaInicializado = false;
        LogInfo("RoleMap limpo ao encerrar a partida.");
    }

    private static void AtualizarHudDaFuncaoLocal(List<TrocaPendente> trocas)
    {
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null || HudManager.Instance?.ReportButton == null)
            return;

        TrocaPendente propria = trocas.FirstOrDefault(x =>
            x.Jogador != null && x.Jogador.PlayerId == local.PlayerId);

        if (propria == null)
            return;

        try
        {
            HudManager.Instance.ReportButton.gameObject.SetActive(
                !RoleManager.IsGhostRole(propria.Funcao));
        }
        catch (Exception ex)
        {
            LogAviso("Não foi possível atualizar o botão de reporte após a troca: " + ex.Message);
        }
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleSwitchAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[RoleSwitchAPI] " + mensagem);
}

/// <summary>
/// Diagnóstico passivo de RpcSetRole. Não intercepta nem altera o sorteio vanilla.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class RoleSwitchRpcSetRolePatch
{
    private static void Postfix(PlayerControl __instance, RoleTypes roleType, bool canOverrideRole)
    {
        string roleAtual = "(Data.Role nulo)";
        try { roleAtual = __instance?.Data?.Role?.Role.ToString() ?? "(Role nulo)"; }
        catch (Exception ex) { roleAtual = "<erro:" + ex.GetType().Name + ">"; }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[RoleRevealDiag] RpcSetRole playerId={__instance?.PlayerId} roleSolicitada={roleType} roleAtual={roleAtual} canOverride={canOverrideRole}");
    }
}

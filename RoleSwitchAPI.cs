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
        public bool EstavaVivo;

        public TrocaPendente(PlayerControl jogador, RoleTypes funcao)
        {
            Jogador = jogador;
            Funcao = funcao;
            EstavaVivo = jogador != null && jogador.Data != null && !jogador.Data.IsDead;
        }
    }

    private static bool loteEmAndamento;
    private static float inicioLoteRealtime;
    private static int identificadorLote;
    private const float TIMEOUT_LOTE_SEGUNDOS = 10f;

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
            foreach (TrocaPendente troca in trocas)
                RpcSetRoleGlobal(troca.Jogador, troca.Funcao);

            AtualizarHudDaFuncaoLocal(trocas);
            LogInfo($"Lote {loteAtual} iniciado com {trocas.Count} atribuição(ões). Iniciando restauração de estado vivo.");
            MonoBehaviourExtensions.StartCoroutine(
                local, RestaurarJogadoresVivosAposTroca(trocas, loteAtual));
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

    private static System.Collections.IEnumerator RestaurarJogadoresVivosAposTroca(
        List<TrocaPendente> trocas, int loteAtual)
    {
        try
        {
            yield return new WaitForSecondsRealtime(0.75f);

            if (loteAtual != identificadorLote)
                yield break;

            AmongUsClient cliente = AmongUsClient.Instance;
            if (cliente == null || !cliente.AmHost || GameData.Instance == null)
                yield break;

            foreach (TrocaPendente troca in trocas)
            {
                if (troca == null || !troca.EstavaVivo || troca.Jogador == null ||
                    troca.Jogador.Data == null || troca.Jogador.Data.Disconnected)
                    continue;

                NetworkedPlayerInfo dados = GameData.Instance.GetPlayerById(troca.Jogador.PlayerId);
                if (dados == null)
                    continue;

                // A troca de função pode levar 0,75s para concluir. Nesse intervalo,
                // o jogador pode morrer legitimamente. Nunca sobrescreva uma morte
                // que já esteja registrada no PlayerControl ou no GameData.
                if (troca.Jogador.Data.IsDead || dados.IsDead)
                {
                    LogInfo(
                        $"Restauração de estado vivo ignorada: PlayerId={troca.Jogador.PlayerId} morreu durante a troca de função.");
                    yield return new WaitForSecondsRealtime(0.08f);
                    if (loteAtual != identificadorLote)
                        yield break;
                    continue;
                }

                // A alteração direta de NetworkedPlayerInfo.IsDead só corrige
                // o estado local do host; não executa o fluxo nativo de revive
                // nos clientes vanilla. Reaplicamos o estado local e reenviamos
                // a função base para provocar a atualização vanilla em todos.
                dados.IsDead = false;
                troca.Jogador.Revive();
                dados.IsDead = false;
                dados.SetDirtyBit(uint.MaxValue);
                cliente.SendAllStreamedObjects();

                // O EHR usa a mesma estratégia para clientes vanilla:
                // marcar vivo e reaplicar Crewmate antes de restaurar a função
                // definitiva. Isso evita que SetRole seja processado enquanto
                // o cliente ainda conserva o estado de morto.
                RpcSetRoleGlobal(troca.Jogador, RoleTypes.Crewmate);

                LogInfo(
                    $"Estado vivo aplicado localmente e sincronização vanilla iniciada: PlayerId={troca.Jogador.PlayerId}.");

                yield return new WaitForSecondsRealtime(0.18f);
                if (loteAtual != identificadorLote)
                    yield break;

                NetworkedPlayerInfo dadosAtualizados =
                    GameData.Instance?.GetPlayerById(troca.Jogador.PlayerId);

                // Não reenvia a função se o jogador morreu legitimamente
                // durante a janela de sincronização.
                if (dadosAtualizados == null || dadosAtualizados.IsDead ||
                    troca.Jogador.Data == null || troca.Jogador.Data.IsDead)
                {
                    LogInfo(
                        $"Sincronização da função definitiva cancelada: PlayerId={troca.Jogador.PlayerId} morreu durante o processo.");
                    yield return new WaitForSecondsRealtime(0.08f);
                    continue;
                }

                if (troca.Funcao != RoleTypes.Crewmate)
                    RpcSetRoleGlobal(troca.Jogador, troca.Funcao);

                LogInfo(
                    $"Função definitiva reenviada após sincronização de revive: PlayerId={troca.Jogador.PlayerId}, Role={troca.Funcao}.");

                yield return new WaitForSecondsRealtime(0.12f);
                if (loteAtual != identificadorLote)
                    yield break;
            }
        }
        finally
        {
            if (loteAtual == identificadorLote)
            {
                loteEmAndamento = false;
                YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes = false;
                LogInfo($"Lote {loteAtual} finalizado. Novas trocas estão liberadas.");
            }
        }
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
    private static void RpcSetRoleGlobal(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
            return;

        try
        {
            // Mesmo fluxo do EHR RpcSetRoleGlobal:
            // 1) aplica a função diretamente na instância local do host;
            // 2) transmite o RPC vanilla SetRole para todos os clientes.
            // SetRole() é usado pelo próprio EHR; não iniciamos CoSetRole aqui.
            jogador.StartCoroutine(jogador.CoSetRole(funcao, true));

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable);
            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            LogInfo($"RpcSetRoleGlobal EHR-style: PlayerId={jogador.PlayerId}, Role={funcao}, canOverrideRole=true.");
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
            if (AmongUsClient.Instance.ClientId == clientId)
            {
                // Mesmo comportamento do EHR: se o destinatário é o próprio
                // host, aplica a função localmente sem enviar RPC para si mesmo.
                jogador.StartCoroutine(jogador.CoSetRole(funcao, true));
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

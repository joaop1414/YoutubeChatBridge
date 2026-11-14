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
    /// API pública interna para eventos/testes que precisam trocar funções
    /// depois do início da partida.
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

        // Trocas solicitadas durante a partida usam canOverrideRole=true.
        // A atribuição inicial vanilla segue um caminho separado e usa false.
        return ExecutarAtribuicoes(trocas, "troca durante a partida", true);
    }

    private static bool ExecutarAtribuicoes(List<TrocaPendente> trocas, string origem, bool canOverrideRole)
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

        try
        {
            // EHR-style RpcSetRoleGlobal: aplica a função localmente e envia
            // SetRole vanilla em broadcast com canOverrideRole=true.
            // A atribuição inicial continua no sender separado com false.
            if (origem == "troca durante a partida")
            {
                foreach (TrocaPendente troca in trocas)
                    RpcSetRoleGlobal(troca.Jogador, troca.Funcao);

                AtualizarHudDaFuncaoLocal(trocas);
                MonoBehaviourExtensions.StartCoroutine(
                    local, RestaurarJogadoresVivosAposTroca(trocas));
                loteEmAndamento = false;
                return true;
            }

            foreach (TrocaPendente troca in trocas)
            {
                troca.Jogador.StartCoroutine(
                    troca.Jogador.CoSetRole(troca.Funcao, false));
            }

            MonoBehaviourExtensions.StartCoroutine(local, EnviarAtribuicoesCoroutine(trocas, origem, false));
            return true;
        }
        catch (Exception ex)
        {
            loteEmAndamento = false;

            LogErro("Não foi possível iniciar a distribuição das funções: " + ex);
            return false;
        }
    }

    private static System.Collections.IEnumerator RestaurarJogadoresVivosAposTroca(
        List<TrocaPendente> trocas)
    {
        yield return new WaitForSecondsRealtime(0.75f);

        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost || GameData.Instance == null)
            yield break;

        foreach (TrocaPendente troca in trocas)
        {
            if (troca == null || !troca.EstavaVivo || troca.Jogador == null ||
                troca.Jogador.Data == null || troca.Jogador.Data.Disconnected)
                continue;

            NetworkedPlayerInfo dados = GameData.Instance.GetPlayerById(troca.Jogador.PlayerId);
            if (dados == null || !dados.IsDead)
                continue;

            dados.IsDead = false;
            dados.SetDirtyBit(1u << troca.Jogador.PlayerId);
            cliente.SendAllStreamedObjects();

            LogInfo($"Estado vivo restaurado pela rede após troca de função: PlayerId={troca.Jogador.PlayerId}.");
            yield return new WaitForSecondsRealtime(0.08f);
        }
    }

    private static System.Collections.IEnumerator EnviarAtribuicoesCoroutine(
        List<TrocaPendente> trocas, string origem, bool canOverrideRole)
    {
        int clientesEnviados = 0;

        try
        {
            AmongUsClient cliente = AmongUsClient.Instance;
            HashSet<int> clientesVisitados = new();

            foreach (PlayerControl observador in PlayerControl.AllPlayerControls)
            {
                if (observador == null || observador.Data == null ||
                    observador.Data.Disconnected || observador.OwnerId < 0 ||
                    observador.OwnerId == cliente.ClientId ||
                    !clientesVisitados.Add(observador.OwnerId))
                    continue;

                RoleSwitchCustomRpcSender sender = RoleSwitchCustomRpcSender.Create(
                    "RoleSwitchAPI / SetRole", SendOption.Reliable);

                foreach (TrocaPendente troca in trocas)
                {
                    // Envia também o SetRole do próprio jogador para este cliente.
                    // O CoSetRole executado no host altera somente o estado local do host;
                    // cada cliente remoto precisa aplicar sua própria função ao receber o RPC.
                    sender.RpcSetRole(troca.Jogador, troca.Funcao, observador.OwnerId, canOverrideRole);
                }

                sender.SendMessage();
                clientesEnviados++;
            }

            AtualizarHudDaFuncaoLocal(trocas);

            foreach (TrocaPendente troca in trocas)
            {
                LogInfo($"Função distribuída: PlayerId={troca.Jogador.PlayerId}, OwnerId={troca.Jogador.OwnerId}, função={troca.Funcao}.");
            }

            LogInfo($"Atribuição concluída ({origem}): {trocas.Count} jogador(es), {clientesEnviados} cliente(s) remoto(s), CustomRpcSender, canOverrideRole={canOverrideRole}.");
        }
        finally
        {
            loteEmAndamento = false;

        }

        yield break;
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
            // Equivalente à extensão SetRole do EHR: inicia CoSetRole localmente.
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
                jogador.StartCoroutine(jogador.CoSetRole(funcao, true));
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
/// Sender direcionado inspirado no CustomRpcSender do EHR.
/// </summary>
internal sealed class RoleSwitchCustomRpcSender
{
    private readonly SendOption opcao;
    private readonly List<MessageWriter> doneStreams = new();
    private MessageWriter writer;
    private int clienteAlvo = -2;
    private bool mensagemAberta;
    private bool rpcAberto;
    private bool finalizado;

    private RoleSwitchCustomRpcSender(SendOption opcao)
    {
        this.opcao = opcao;
        writer = MessageWriter.Get(opcao);
    }

    internal static RoleSwitchCustomRpcSender Create(
        string nome = "RoleSwitchAPI", SendOption opcao = SendOption.Reliable)
    {
        return new RoleSwitchCustomRpcSender(opcao);
    }

    internal RoleSwitchCustomRpcSender AutoStartRpc(
        uint netId, RpcCalls chamada, int targetClientId)
    {
        if (finalizado)
            throw new InvalidOperationException("O CustomRpcSender já foi finalizado.");

        if (rpcAberto)
            throw new InvalidOperationException("O RPC anterior ainda não foi encerrado.");

        if (mensagemAberta && writer.Length > 500)
            FecharMensagemEContinuar();

        if (!mensagemAberta || clienteAlvo != targetClientId)
        {
            if (mensagemAberta)
                FecharMensagem();

            clienteAlvo = targetClientId;
            writer.StartMessage(6);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(targetClientId);
            mensagemAberta = true;
        }

        writer.StartMessage(2);
        writer.WritePacked(netId);
        writer.Write((byte)chamada);
        rpcAberto = true;
        return this;
    }

    internal RoleSwitchCustomRpcSender Write(ushort valor)
    {
        GarantirRpcAberto();
        writer.Write(valor);
        return this;
    }

    internal RoleSwitchCustomRpcSender Write(bool valor)
    {
        GarantirRpcAberto();
        writer.Write(valor);
        return this;
    }

    internal RoleSwitchCustomRpcSender EndRpc()
    {
        GarantirRpcAberto();
        writer.EndMessage();
        rpcAberto = false;
        return this;
    }

    internal bool RpcSetRole(PlayerControl jogador, RoleTypes funcao, int targetClientId, bool canOverrideRole = true)
    {
        if (jogador == null)
            return false;

        AutoStartRpc(jogador.NetId, RpcCalls.SetRole, targetClientId)
            .Write((ushort)funcao)
            .Write(canOverrideRole)
            .EndRpc();

        return true;
    }

    internal void SendMessage()
    {
        if (finalizado)
            return;

        if (rpcAberto)
            throw new InvalidOperationException("Não é possível enviar com um RPC aberto.");

        if (mensagemAberta)
            FecharMensagem();

        foreach (MessageWriter completed in doneStreams)
        {
            AmongUsClient.Instance.SendOrDisconnect(completed);
            completed.Recycle();
        }
        doneStreams.Clear();

        if (writer.Length > 0)
            AmongUsClient.Instance.SendOrDisconnect(writer);

        writer.Recycle();
        finalizado = true;
    }

    private void FecharMensagemEContinuar()
    {
        FecharMensagem();
        doneStreams.Add(writer);
        writer = MessageWriter.Get(opcao);
        writer.StartMessage(6);
        writer.Write(AmongUsClient.Instance.GameId);
        writer.WritePacked(clienteAlvo);
        mensagemAberta = true;
    }

    private void FecharMensagem()
    {
        if (!mensagemAberta || rpcAberto)
            return;

        writer.EndMessage();
        mensagemAberta = false;
    }

    private void GarantirRpcAberto()
    {
        if (!rpcAberto)
            throw new InvalidOperationException("Não existe um RPC aberto para escrita.");
    }
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

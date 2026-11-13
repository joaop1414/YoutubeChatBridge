using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using BepInEx.Unity.IL2CPP.Utils;
using BepInEx.Unity.IL2CPP.Utils.Collections;

namespace YouTubeVoteBridge;

/// <summary>
/// Captura a atribuição vanilla feita por RoleManager.SelectRoles e substitui
/// somente o transporte de RpcSetRole. O RoleManager continua decidindo quais
/// funções serão atribuídas, respeitando as opções e probabilidades do jogo.
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

    private static bool capturandoSelecaoVanilla;
    private static bool loteEmAndamento;
    private static readonly Dictionary<byte, TrocaPendente> funcoesCapturadas = new();

    /// <summary>
    /// Chamado imediatamente antes de RoleManager.SelectRoles.
    /// </summary>
    internal static void IniciarCapturaSelecaoVanilla()
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost)
            return;

        funcoesCapturadas.Clear();
        capturandoSelecaoVanilla = true;
        LogInfo("Captura iniciada: o sorteador vanilla continuará escolhendo as funções.");
    }

    /// <summary>
    /// Intercepta somente RpcSetRole disparado durante SelectRoles.
    /// Retorna true quando a chamada foi capturada e deve ser bloqueada.
    /// </summary>
    internal static bool TentarCapturarRpcSetRole(PlayerControl jogador, RoleTypes funcao)
    {
        if (!capturandoSelecaoVanilla || jogador == null ||
            jogador.Data == null || jogador.Data.Disconnected)
            return false;

        funcoesCapturadas[jogador.PlayerId] = new TrocaPendente(jogador, funcao);
        LogInfo($"Função vanilla capturada: PlayerId={jogador.PlayerId}, função={funcao}.");
        return true;
    }

    /// <summary>
    /// Chamado depois de RoleManager.SelectRoles. Aplica localmente e distribui
    /// os papéis escolhidos pelo vanilla com canOverrideRole=false, como no EHR.
    /// </summary>
    internal static void FinalizarCapturaSelecaoVanilla()
    {
        if (!capturandoSelecaoVanilla)
            return;

        capturandoSelecaoVanilla = false;

        List<TrocaPendente> trocas = funcoesCapturadas.Values
            .Where(x => x != null && x.Jogador != null)
            .ToList();

        funcoesCapturadas.Clear();

        if (trocas.Count == 0)
        {
            LogAviso("SelectRoles terminou sem nenhuma chamada RpcSetRole capturada. A atribuição vanilla não foi substituída.");
            return;
        }

        LogInfo($"Seleção vanilla concluída: {trocas.Count} função(ões) capturada(s). Iniciando distribuição.");
        ExecutarAtribuicoes(trocas, "seleção vanilla", false);
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

        return ExecutarAtribuicoes(trocas, "solicitação manual");
    }

    private static bool ExecutarAtribuicoes(List<TrocaPendente> trocas, string origem, bool canOverrideRole = true)
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
            foreach (TrocaPendente troca in trocas)
            {
                troca.Jogador.StartCoroutine(
                    troca.Jogador.CoSetRole(troca.Funcao, canOverrideRole));
            }

            MonoBehaviourExtensions.StartCoroutine(local, EnviarAtribuicoesCoroutine(trocas, origem, canOverrideRole));
            return true;
        }
        catch (Exception ex)
        {
            loteEmAndamento = false;
            LogErro("Não foi possível iniciar a distribuição das funções: " + ex);
            return false;
        }
    }

    private static System.Collections.IEnumerator EnviarAtribuicoesCoroutine(
        List<TrocaPendente> trocas, string origem, bool canOverrideRole)
    {
        yield return null;

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
                    if (troca.Jogador.PlayerId == observador.PlayerId)
                        continue;

                    sender.RpcSetRole(troca.Jogador, troca.Funcao, observador.OwnerId, canOverrideRole);
                }

                TrocaPendente papelProprio = trocas.FirstOrDefault(
                    x => x.Jogador.PlayerId == observador.PlayerId);

                if (papelProprio != null)
                    sender.RpcSetRole(papelProprio.Jogador, papelProprio.Funcao, observador.OwnerId, canOverrideRole);

                sender.SendMessage();
                clientesEnviados++;
                yield return new UnityEngine.WaitForSeconds(0.1f);
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

    internal static bool CapturandoSelecaoVanilla => capturandoSelecaoVanilla;

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
/// Abre e fecha a captura ao redor do sorteador vanilla. Não substituímos
/// RoleManager.SelectRoles nem LogicRoleSelectionNormal.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class RoleSwitchVanillaSelectionPatch
{
    private static void Prefix()
    {
        RoleSwitchAPI.IniciarCapturaSelecaoVanilla();
    }

    private static void Postfix()
    {
        RoleSwitchAPI.FinalizarCapturaSelecaoVanilla();
    }
}

/// <summary>
/// Durante SelectRoles, captura e bloqueia o transporte automático do RpcSetRole.
/// Depois, envia os papéis por RPC direcionado. Fora desse momento, o método
/// original continua funcionando normalmente.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
[HarmonyPriority(Priority.High)]
internal static class RoleSwitchRpcSetRolePatch
{
    private static bool Prefix(
        PlayerControl __instance,
        RoleTypes roleType,
        bool canOverrideRole)
    {
        if (RoleSwitchAPI.TentarCapturarRpcSetRole(__instance, roleType))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[RoleSwitchAPI] RpcSetRole vanilla bloqueado durante Role Reveal: playerId={__instance?.PlayerId}, role={roleType}, canOverrideOriginal={canOverrideRole}.");
            return false;
        }

        return true;
    }

    private static void Postfix(PlayerControl __instance, RoleTypes roleType, bool canOverrideRole)
    {
        string roleAtual = "(Data.Role nulo)";
        try { roleAtual = __instance?.Data?.Role?.Role.ToString() ?? "(Role nulo)"; }
        catch (Exception ex) { roleAtual = "<erro:" + ex.GetType().Name + ">"; }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[RoleRevealDiag] RpcSetRole POSTFIX playerId={__instance?.PlayerId} roleSolicitada={roleType} roleAtual={roleAtual} canOverride={canOverrideRole}");
    }
}

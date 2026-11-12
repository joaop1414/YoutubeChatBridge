using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;

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
    /// Chamado depois de RoleManager.SelectRoles. Distribui os papéis que o
    /// próprio jogo escolheu, usando canOverrideRole=true em todos os clientes.
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
        ExecutarAtribuicoes(trocas, "seleção vanilla");
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

    private static bool ExecutarAtribuicoes(List<TrocaPendente> trocas, string origem)
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost)
        {
            LogAviso("A atribuição de funções exige que o cliente seja o host.");
            return false;
        }

        if (RoleManager.Instance == null)
        {
            LogErro("RoleManager.Instance está nulo.");
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
        List<MessageWriter> writersCriados = new();

        try
        {
            // Atualiza primeiro a visão autoritativa do host. Usamos true para
            // que CoSetRole também possa substituir uma função já marcada como atribuída.
            foreach (TrocaPendente troca in trocas)
            {
                troca.Jogador.StartCoroutine(
                    troca.Jogador.CoSetRole(troca.Funcao, true));
            }

            // Um pacote GameDataTo por cliente remoto. A ordem é essencial:
            // primeiro todos os outros jogadores e, por último, o próprio cliente.
            // O SetRole próprio é o que permite ao cliente montar corretamente
            // sua tela de introdução/equipe.
            HashSet<int> clientesEnviados = new();

            foreach (PlayerControl observador in PlayerControl.AllPlayerControls)
            {
                if (observador == null || observador.Data == null ||
                    observador.Data.Disconnected || observador.OwnerId < 0 ||
                    observador.OwnerId == cliente.ClientId ||
                    !clientesEnviados.Add(observador.OwnerId))
                    continue;

                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                writersCriados.Add(writer);

                writer.StartMessage(6); // GameDataTo
                writer.Write(cliente.GameId);
                writer.WritePacked(observador.OwnerId);

                // Papéis dos demais jogadores.
                foreach (TrocaPendente troca in trocas)
                {
                    if (troca.Jogador.PlayerId == observador.PlayerId)
                        continue;

                    EscreverRpcSetRole(writer, troca);
                }

                // Papel próprio sempre por último.
                TrocaPendente papelProprio = trocas.FirstOrDefault(
                    x => x.Jogador.PlayerId == observador.PlayerId);

                if (papelProprio != null)
                    EscreverRpcSetRole(writer, papelProprio);

                writer.EndMessage(); // GameDataTo
            }

            // Os pacotes já estão completos. Enviamos um por cliente.
            // Cada cliente recebe sua sequência individual, sem broadcast
            // de papéis que possam ser diferentes entre observadores.
            foreach (MessageWriter writer in writersCriados)
            {
                cliente.SendOrDisconnect(writer);
                writer.Recycle();
            }
            writersCriados.Clear();

            AtualizarHudDaFuncaoLocal(trocas);

            foreach (TrocaPendente troca in trocas)
            {
                LogInfo($"Função distribuída: PlayerId={troca.Jogador.PlayerId}, OwnerId={troca.Jogador.OwnerId}, função={troca.Funcao}.");
            }

            LogInfo($"Atribuição concluída ({origem}): {trocas.Count} jogador(es), {clientesEnviados.Count} cliente(s) remoto(s), canOverrideRole=true.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro("Falha no pipeline de atribuição de funções: " + ex);
            return false;
        }
        finally
        {
            foreach (MessageWriter writer in writersCriados)
            {
                if (writer == null)
                    continue;

                try { writer.Recycle(); }
                catch { }
            }

            loteEmAndamento = false;
        }
    }

    private static void EscreverRpcSetRole(MessageWriter writer, TrocaPendente troca)
    {
        writer.StartMessage(2); // RPC
        writer.WritePacked(troca.Jogador.NetId);
        writer.Write((byte)RpcCalls.SetRole);
        writer.Write((ushort)troca.Funcao);
        writer.Write(true); // canOverrideRole
        writer.EndMessage();
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
/// Durante SelectRoles, captura as chamadas do jogo em vez de transmitir
/// os RPCs vanilla. Fora desse momento, RpcSetRole continua funcionando normal.
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
        return !RoleSwitchAPI.TentarCapturarRpcSetRole(__instance, roleType);
    }
}

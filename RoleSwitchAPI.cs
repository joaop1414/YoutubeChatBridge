using System;
using System.Collections.Generic;
using System.Linq;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// Atribuição de funções vanilla usando o roteamento por observador do EHR:
/// - atualiza a cópia do host sem RPC vanilla concorrente;
/// - envia para cada cliente remoto as funções dos OUTROS jogadores;
/// - envia a função própria de cada jogador remoto por RPC direcionado
///   diretamente ao OwnerId dele, como o RpcSetRoleDesync do EHR.
/// </summary>
internal static class RoleSwitchAPI
{
    private static bool bloqueandoRpcSetRole;
    private static bool loteEmAndamento;

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
    /// Troca funções vanilla para todos os observadores.
    /// Cada cliente recebe as funções dos outros jogadores em um GameDataTo;
    /// a função do próprio jogador é enviada separadamente ao cliente dono.
    /// </summary>
    internal static bool TrocarFuncoes(
        IEnumerable<KeyValuePair<PlayerControl, RoleTypes>> atribuicoes)
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        if (cliente == null || !cliente.AmHost)
        {
            LogAviso("Atribuição em lote exige que o cliente seja o host.");
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

        if (trocas.Count == 0)
        {
            LogAviso("Nenhuma atribuição válida foi recebida.");
            return false;
        }

        Dictionary<int, MessageWriter> senders = new();
        HashSet<int> clientesObservadores = new();

        loteEmAndamento = true;
        bloqueandoRpcSetRole = true;

        try
        {
            // Um GameDataTo por cliente observador. O EHR organiza os
            // senders por jogador-alvo e roteia cada RPC para o OwnerId dele.
            foreach (PlayerControl observador in PlayerControl.AllPlayerControls)
            {
                if (observador == null || observador.Data == null ||
                    observador.Data.Disconnected || observador.OwnerId < 0 ||
                    observador.OwnerId == cliente.ClientId)
                    continue;

                if (!clientesObservadores.Add(observador.OwnerId))
                    continue;

                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(6); // GameDataTo
                writer.Write(cliente.GameId);
                writer.WritePacked(observador.OwnerId);
                senders.Add(observador.OwnerId, writer);
            }

            // Atualiza a visão local do host sobre os jogadores remotos,
            // sem disparar um RpcSetRole vanilla adicional.
            foreach (TrocaPendente troca in trocas)
            {
                if (troca.Jogador.OwnerId == cliente.ClientId)
                    continue;

                troca.Jogador.StartCoroutine(
                    troca.Jogador.CoSetRole(troca.Funcao, false));
            }

            // Como no SendRpcForNormal do EHR: para cada cliente observador,
            // enviamos a função de cada jogador EXCETO a função própria dele.
            foreach (KeyValuePair<int, MessageWriter> item in senders)
            {
                int ownerObservador = item.Key;
                MessageWriter writer = item.Value;

                PlayerControl jogadorObservador = PlayerControl.AllPlayerControls
                    .ToArray()
                    .FirstOrDefault(p => p != null && p.OwnerId == ownerObservador);

                foreach (TrocaPendente troca in trocas)
                {
                    if (jogadorObservador != null &&
                        troca.Jogador.PlayerId == jogadorObservador.PlayerId)
                        continue;

                    writer.StartMessage(2); // RPC
                    writer.WritePacked(troca.Jogador.NetId);
                    writer.Write((byte)RpcCalls.SetRole);
                    writer.Write((ushort)troca.Funcao);
                    writer.Write(true); // canOverrideRole
                    writer.EndMessage();
                }

                writer.EndMessage(); // GameDataTo
            }

            // O EHR trata o papel próprio separadamente: SetRole direcionado
            // ao OwnerId do jogador, em vez de incluí-lo no pacote dos outros.
            foreach (TrocaPendente troca in trocas)
            {
                int ownerId = troca.Jogador.OwnerId;
                if (ownerId < 0 || ownerId == cliente.ClientId)
                    continue;

                MessageWriter selfWriter = cliente.StartRpcImmediately(
                    troca.Jogador.NetId,
                    (byte)RpcCalls.SetRole,
                    SendOption.Reliable,
                    ownerId);

                selfWriter.Write((ushort)troca.Funcao);
                selfWriter.Write(true); // canOverrideRole
                cliente.FinishRpcImmediately(selfWriter);
            }

            // O host é dono do próprio PlayerControl: aplica a função local
            // depois da fase protegida, como no estágio SetRoleSelf do EHR.
            bloqueandoRpcSetRole = false;

            foreach (TrocaPendente troca in trocas)
            {
                if (troca.Jogador.OwnerId == cliente.ClientId)
                {
                    troca.Jogador.StartCoroutine(
                        troca.Jogador.CoSetRole(troca.Funcao, true));
                }
            }

            // Envia os pacotes de observadores após preparar todos os RPCs.
            foreach (KeyValuePair<int, MessageWriter> item in senders)
            {
                MessageWriter writer = item.Value;
                cliente.SendOrDisconnect(writer);
                writer.Recycle();
                senders[item.Key] = null;
            }

            AtualizarHudDaFuncaoLocal(trocas);

            foreach (TrocaPendente troca in trocas)
            {
                LogInfo($"Função preparada: PlayerId={troca.Jogador.PlayerId}, OwnerId={troca.Jogador.OwnerId}, função={troca.Funcao}.");
            }

            LogInfo($"Atribuição concluída: {trocas.Count} jogador(es), {clientesObservadores.Count} cliente(s) remoto(s), com RPC próprio direcionado.");
            return true;
        }
        catch (Exception ex)
        {
            LogErro("Falha no pipeline de atribuição de funções: " + ex);
            return false;
        }
        finally
        {
            bloqueandoRpcSetRole = false;
            loteEmAndamento = false;

            foreach (MessageWriter writer in senders.Values)
            {
                if (writer == null)
                    continue;

                try { writer.Recycle(); }
                catch { }
            }
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

    internal static bool RpcSetRoleEstaBloqueado() => bloqueandoRpcSetRole;

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleSwitchAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[RoleSwitchAPI] " + mensagem);
}

/// <summary>
/// Bloqueia apenas chamadas vanilla concorrentes enquanto os pacotes do lote
/// estão sendo preparados. Não força canOverrideRole globalmente.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class RoleSwitchRpcSetRolePatch
{
    private static bool Prefix()
    {
        return !RoleSwitchAPI.RpcSetRoleEstaBloqueado();
    }
}

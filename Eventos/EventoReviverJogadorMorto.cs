using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using Hazel;

namespace YouTubeVoteBridge;

internal sealed class EventoReviverJogadorMorto : EventoVotacao
{
    private static readonly System.Random Sorteador = new();
    private static readonly Dictionary<byte, RoleTypes> FuncoesAntesDaMorte = new();

    public override string Nome => "Reviver um jogador morto";

    // Guarda a função enquanto o jogador ainda está vivo, antes que o jogo
    // substitua a função original pela variante Ghost.
    public static void RegistrarFuncoesVivas()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null || jogador.Data.Disconnected || jogador.Data.IsDead ||
                jogador.Data.Role == null)
                continue;
            try { FuncoesAntesDaMorte[jogador.PlayerId] = jogador.Data.Role.Role; }
            catch { }
        }
    }

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        RegistrarFuncoesVivas();

        List<PlayerControl> mortos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.Data != null && jogador.Data.IsDead && !jogador.Data.Disconnected)
                mortos.Add(jogador);

        if (mortos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Reviver jogador cancelado: não há jogadores mortos elegíveis.");
            return;
        }

        PlayerControl alvo = mortos[Sorteador.Next(mortos.Count)];
        RoleTypes funcaoOriginal = ObterFuncaoOriginal(alvo);
        try
        {
            // Algumas versões expõem RpcRevive; noutras, Revive é local.
            // Em ambos os casos forçamos o estado vivo e enviamos PlayerData
            // atualizado a cada cliente, inclusive vanilla.
            bool reviveExecutado = InvocarMetodoSemParametros(alvo, "RpcRevive");
            if (!reviveExecutado)
                reviveExecutado = InvocarMetodoSemParametros(alvo, "Revive");
            if (!reviveExecutado)
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não encontrei método de ressurreição nativo; aplicando o estado vivo e sincronizando PlayerData diretamente.");

            alvo.Data.IsDead = false;
            alvo.moveable = true;
            try { alvo.Visible = true; } catch { }

            // Repõe a função que o jogador tinha antes da morte (não a função Ghost).
            alvo.RpcSetRole(funcaoOriginal, true);
            SincronizarEstadoComClientes(alvo);
            FuncoesAntesDaMorte.Remove(alvo.PlayerId);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Ressurreição solicitada para " + (alvo.Data?.PlayerName ?? "jogador") +
                "; função restaurada para " + funcaoOriginal + " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao reviver jogador morto: " + (ex.InnerException ?? ex));
        }
    }


    private static void SincronizarEstadoComClientes(PlayerControl alvo)
    {
        if (AmongUsClient.Instance == null || alvo?.Data == null || !YouTubeVoteBridgePlugin.EhHost())
            return;

        try
        {
            Type tipoCliente = AmongUsClient.Instance.GetType();
            object colecao =
                tipoCliente.GetProperty("allClients", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(AmongUsClient.Instance) ??
                tipoCliente.GetField("allClients", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(AmongUsClient.Instance);

            if (colecao == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Não foi possível encontrar a coleção de clientes para sincronizar a ressurreição vanilla.");
                return;
            }

            // A coleção pode ser um wrapper IL2CPP, que nem sempre implementa
            // System.Collections.IEnumerable diretamente.
            MethodInfo obterEnumerador = colecao.GetType().GetMethod("GetEnumerator",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            object enumerador = obterEnumerador?.Invoke(colecao, null);
            if (enumerador == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "A coleção de clientes não disponibilizou um enumerador.");
                return;
            }
            Type tipoEnumerador = enumerador.GetType();
            MethodInfo avancar = tipoEnumerador.GetMethod("MoveNext");
            PropertyInfo atual = tipoEnumerador.GetProperty("Current");
            if (avancar == null || atual == null)
                return;

            while ((bool)avancar.Invoke(enumerador, null))
            {
                object cliente = atual.GetValue(enumerador);
                if (cliente == null) continue;
                Type tipo = cliente.GetType();
                object idObjeto =
                    tipo.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(cliente) ??
                    tipo.GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(cliente);
                if (idObjeto == null || !int.TryParse(idObjeto.ToString(), out int clientId) ||
                    clientId == AmongUsClient.Instance.ClientId)
                    continue;

                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    writer.StartMessage(6);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(clientId);
                    writer.StartMessage(1);
                    writer.WritePacked(alvo.Data.NetId);
                    alvo.Data.Serialize(writer, true);
                    writer.EndMessage();
                    writer.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                }
                finally
                {
                    writer.Recycle();
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao sincronizar ressurreição com clientes vanilla: " + ex);
        }
    }

    private static RoleTypes ObterFuncaoOriginal(PlayerControl jogador)
    {
        if (FuncoesAntesDaMorte.TryGetValue(jogador.PlayerId, out RoleTypes funcao))
            return funcao;

        // Reserva para jogadores mortos antes de o mod começar a registrar funções.
        try
        {
            if (jogador.Data?.Role != null && jogador.Data.Role.IsImpostor)
                return RoleTypes.Impostor;
        }
        catch { }
        return RoleTypes.Crewmate;
    }

    private static bool InvocarMetodoSemParametros(PlayerControl jogador, string nome)
    {
        MethodInfo metodo = jogador.GetType().GetMethod(
            nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, Type.EmptyTypes, null);
        if (metodo == null) return false;
        metodo.Invoke(jogador, null);
        return true;
    }
}
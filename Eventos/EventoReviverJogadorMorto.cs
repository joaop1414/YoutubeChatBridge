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
            // EHR guarda funções customizadas e estado interno em PlayerState.
            // Seu /revive chama exatamente este método: reutilize-o por reflexão
            // se EHR estiver instalado, para restaurar também roles/addons próprios.
            if (TentarReviverPeloEhr(alvo))
            {
                // O EHR restaura primeiro seu PlayerState/custom role. Depois
                // agendamos a RoleTypes vanilla que foi capturada enquanto vivo,
                // para corrigir o caso em que o jogador fica com Ghost após reviver.
                AgendarRestauracaoFuncao(alvo, funcaoOriginal);
                FuncoesAntesDaMorte.Remove(alvo.PlayerId);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "RpcRevive do EHR executado; restauração da função base agendada para " +
                    funcaoOriginal + " (PlayerId=" + alvo.PlayerId + ").");
                return;
            }

            // Fallback sem EHR: reviver pelo jogo base e restaurar a RoleTypes
            // que foi registrada antes da morte.
            bool reviveExecutado = InvocarMetodoSemParametros(alvo, "RpcRevive");
            if (!reviveExecutado)
                reviveExecutado = InvocarMetodoSemParametros(alvo, "Revive");

            if (!reviveExecutado)
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Método de revive do jogo base não encontrado; tentando fallback de estado vivo.");

            alvo.Data.IsDead = false;
            alvo.moveable = true;
            try { alvo.Visible = true; } catch { }
            // Sincroniza primeiro o estado vivo; o Role RPC bruto vai por último.
            SincronizarEstadoComClientes(alvo);
            EnviarFuncaoOriginalBruta(alvo, funcaoOriginal);
            FuncoesAntesDaMorte.Remove(alvo.PlayerId);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Ressurreição solicitada para " + (alvo.Data?.PlayerName ?? "jogador") +
                "; função base restaurada para " + funcaoOriginal + " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao reviver jogador morto: " + (ex.InnerException ?? ex));
        }
    }


    private sealed class FuncaoPendente
    {
        public PlayerControl Jogador;
        public RoleTypes Funcao;
        public float ExecutarEm;
    }

    private static readonly List<FuncaoPendente> RestauracoesPendentes = new();

    private static void AgendarRestauracaoFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        RestauracoesPendentes.RemoveAll(x => x.Jogador == jogador);
        RestauracoesPendentes.Add(new FuncaoPendente
        {
            Jogador = jogador,
            Funcao = funcao,
            ExecutarEm = UnityEngine.Time.realtimeSinceStartup + 0.75f
        });
    }

    public static void AtualizarRestauracoesPendentes()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || RestauracoesPendentes.Count == 0)
            return;

        float agora = UnityEngine.Time.realtimeSinceStartup;
        for (int i = RestauracoesPendentes.Count - 1; i >= 0; i--)
        {
            FuncaoPendente pendente = RestauracoesPendentes[i];
            if (agora < pendente.ExecutarEm)
                continue;

            RestauracoesPendentes.RemoveAt(i);
            if (pendente.Jogador == null || pendente.Jogador.Data == null ||
                pendente.Jogador.Data.Disconnected)
                continue;

            EnviarFuncaoOriginalBruta(pendente.Jogador, pendente.Funcao);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[Reviver] Função base reaplicada após o RpcRevive: PlayerId=" +
                pendente.Jogador.PlayerId + ", RoleTypes=" + pendente.Funcao + ".");
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


    private static bool TentarReviverPeloEhr(PlayerControl alvo)
    {
        try
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type tipo = assembly.GetType("EHR.ExtendedPlayerControl", false);
                if (tipo == null) continue;

                foreach (MethodInfo metodo in tipo.GetMethods(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (metodo.Name != "RpcRevive") continue;
                    ParameterInfo[] parametros = metodo.GetParameters();
                    if (parametros.Length != 1 ||
                        !parametros[0].ParameterType.IsAssignableFrom(alvo.GetType()))
                        continue;

                    metodo.Invoke(null, new object[] { alvo });
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Não foi possível invocar o RpcRevive do EHR: " + (ex.InnerException ?? ex));
        }
        return false;
    }


    private static void EnviarFuncaoOriginalBruta(PlayerControl alvo, RoleTypes funcaoOriginal)
    {
        if (alvo == null || alvo.Data == null || AmongUsClient.Instance == null ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;

        try
        {
            // Mesmo formato usado pelo EHR em RpcSetRoleGlobal: ushort + bool.
            // Não chamamos SetRole diretamente porque esse método é uma extensão
            // do EHR e não existe na API vanilla referenciada por este projeto.
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                alvo.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable);
            writer.Write((ushort)funcaoOriginal);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Role RPC bruto enviado: PlayerId=" + alvo.PlayerId +
                ", função=" + funcaoOriginal + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao enviar Role RPC bruto para PlayerId=" + alvo.PlayerId + ": " + ex);
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
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoOportunista : EventoVotacao
{
    internal const string MARCADOR = "<size=0>YTVB_OPPORTUNISTA</size>";
    private static bool ativo;
    private static byte oportunistaPlayerId = byte.MaxValue;
    private static bool fimPendente;
    private static bool liberarRpcFinal;
    private static byte oportunistaFinalId = byte.MaxValue;

    public override string Nome => "Um jogador aleatório será Oportunista";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || ativo)
            return;

        ListarCandidatos(out var candidatos);
        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Não havia jogadores vivos e conectados para sortear.");
            return;
        }

        PlayerControl escolhido = candidatos[System.Random.Shared.Next(candidatos.Count)];
        ativo = true;
        oportunistaPlayerId = escolhido.PlayerId;

        // Garante que a função original esteja registrada antes de adicionar
        // Oportunista como atributo secundário.
        RegistroFuncoesJogadores.Registrar(escolhido, escolhido.Data.RoleType);
        RegistroFuncoesJogadores.DefinirOportunista(escolhido.PlayerId, true);

        // O marcador permite que os demais clientes com o mod reconheçam o atributo.
        MarcarNomeGlobal(escolhido);
        SistemaNomesLocais.Atualizar();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Oportunista] " + escolhido.Data.PlayerName +
            " (PlayerId=" + escolhido.PlayerId +
            ") recebeu o atributo Oportunista. A função original foi preservada.");
    }

    private static void ListarCandidatos(out System.Collections.Generic.List<PlayerControl> candidatos)
    {
        candidatos = new System.Collections.Generic.List<PlayerControl>();
        if (PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer)
                continue;

            candidatos.Add(jogador);
        }
    }

    internal static void Resetar()
    {
        ativo = false;
        oportunistaPlayerId = byte.MaxValue;
        fimPendente = false;
        liberarRpcFinal = false;
        oportunistaFinalId = byte.MaxValue;
    }

    /// <summary>
    /// Intercepta o primeiro RpcEndGame para converter o Oportunista no fantasma
    /// do time vencedor, sincronizar o revive e só então liberar o encerramento.
    /// </summary>
    internal static bool InterceptarFimDeJogo(GameManager manager, GameOverReason motivo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return true;

        // A proteção do Anjo precisa prevalecer também quando este Prefix for
        // executado antes do Prefix de EventoAnjoTraidorEndGamePatch.
        if ((int)motivo == 0 &&
            EventoAnjoTraidor.DeveManterPartidaAbertaComoAnjo())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Oportunista] Preparação da vitória adiada: o Anjo Traidor ainda mantém a partida aberta.");
            return false;
        }

        // Segunda chamada: a preparação já terminou, deixa o jogo encerrar.
        if (liberarRpcFinal)
        {
            liberarRpcFinal = false;
            fimPendente = false;
            return true;
        }

        if (fimPendente)
            return false;

        PlayerControl oportunista = EncontrarOportunista();
        if (oportunista == null)
            return true;

        RoleTypes roleFantasma;
        if ((int)motivo == 0 || motivo == GameOverReason.CrewmatesByTask)
            roleFantasma = RoleTypes.CrewmateGhost;
        else if ((int)motivo == 3 || (int)motivo == 4)
            roleFantasma = RoleTypes.ImpostorGhost;
        else
            return true; // empate ou motivo sem um time convencional vencedor

        if (HudManager.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] HudManager indisponível; encerramento seguirá sem conversão/revive.");
            return true;
        }

        fimPendente = true;
        oportunistaFinalId = oportunista.PlayerId;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Oportunista] Vitória {motivo}; preparando {roleFantasma} e revive para PlayerId={oportunistaFinalId}.");
        MonoBehaviourExtensions.StartCoroutine(PlayerControl.LocalPlayer, PrepararOportunistaEEncerrar(manager, motivo, oportunistaFinalId, roleFantasma));
        return false;
    }

    private static System.Collections.IEnumerator PrepararOportunistaEEncerrar(
        GameManager manager, GameOverReason motivo, byte playerId, RoleTypes roleFantasma)
    {
        // Pequena janela para impedir que a primeira tela de vitória seja criada
        // antes da mudança de função e dos RPCs de revive chegarem aos clientes.
        yield return new WaitForSecondsRealtime(0.35f);

        PlayerControl jogador = EncontrarOportunista(playerId, exigirMarcador: false);
        if (jogador != null)
        {
            try
            {
                jogador.RpcSetRole(roleFantasma, true);
                EnviarReviveSincronizado(jogador);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"[F7/Oportunista] Role {roleFantasma} e revive sincronizado para PlayerId={playerId}.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Oportunista] Falha ao sincronizar fantasma/revive: " + ex);
            }
        }
        else
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Jogador não encontrado no momento do revive.");
        }

        // EndGameManager.Start adicionará o Oportunista à lista nativa já montada.
        liberarRpcFinal = true;
        fimPendente = false;
        manager.RpcEndGame(motivo, false);
    }


    private const byte RpcReviveOportunista = 251;

    /// <summary>
    /// Revive no host, envia um RPC local aos clientes com o mod e publica
    /// NetworkedPlayerInfo atualizado para que clientes vanilla recebam IsDead=false.
    /// </summary>
    private static void EnviarReviveSincronizado(PlayerControl jogador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || jogador == null ||
            jogador.Data == null || AmongUsClient.Instance == null)
            return;

        AmongUsClient cliente = AmongUsClient.Instance;

        // O método nativo atualiza a instância local; ele sozinho não é um RPC
        // de revive para os demais clientes.
        jogador.Revive();
        jogador.Data.IsDead = false;
        jogador.Data.SetDirtyBit(uint.MaxValue);

        // Clientes que possuem o mod executam o revive localmente para atualizar
        // também os componentes visuais do PlayerControl.
        if (PlayerControl.LocalPlayer != null)
        {
            foreach (int clientId in YouTubeVoteBridgePlugin.ClientesComModSnapshot())
            {
                if (clientId < 0 || clientId == cliente.ClientId)
                    continue;

                try
                {
                    MessageWriter rpc = cliente.StartRpcImmediately(
                        PlayerControl.LocalPlayer.NetId,
                        RpcReviveOportunista,
                        SendOption.Reliable,
                        clientId);
                    rpc.Write(jogador.PlayerId);
                    cliente.FinishRpcImmediately(rpc);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Oportunista] Falha ao enviar RPC de revive para ClientId=" +
                        clientId + ": " + ex.Message);
                }
            }
        }

        // Atualiza o estado de morte no GameData para todos os clientes,
        // inclusive os vanilla que não conhecem o RPC personalizado acima.
        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5); // GameData
            writer.Write(cliente.GameId);
            writer.StartMessage(1); // Data / NetworkedPlayerInfo
            writer.WritePacked(jogador.Data.NetId);
            jogador.Data.Serialize(writer, true);
            writer.EndMessage();
            writer.EndMessage();
            cliente.SendOrDisconnect(writer);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Oportunista] Estado IsDead=false publicado no GameData para PlayerId=" +
                jogador.PlayerId + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Oportunista] Falha ao publicar o estado de revive no GameData: " + ex);
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
    }

    internal static void ReceberRevive(MessageReader reader)
    {
        if (YouTubeVoteBridgePlugin.EhHost() || reader == null)
            return;

        try
        {
            byte playerId = reader.ReadByte();
            if (PlayerControl.AllPlayerControls == null)
                return;

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.PlayerId != playerId || !jogador.Data.IsDead)
                    continue;

                jogador.Revive();
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Oportunista] RPC de revive aplicado localmente para PlayerId=" +
                    playerId + ".");
                break;
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Erro ao processar RPC de revive: " + ex.Message);
        }
    }

    private static PlayerControl EncontrarOportunista(byte playerId = byte.MaxValue, bool exigirMarcador = true)
    {
        if (PlayerControl.AllPlayerControls == null) return null;
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
        {
            if (p == null || p.Data == null || p.Data.Disconnected) continue;
            if (playerId != byte.MaxValue && p.PlayerId != playerId) continue;
            if (exigirMarcador && !EhOportunista(p)) continue;
            return p;
        }
        return null;
    }

    internal static void ReaplicarAposDistribuicao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !ativo ||
            oportunistaPlayerId == byte.MaxValue)
            return;

        PlayerControl jogador = EncontrarOportunista(oportunistaPlayerId, exigirMarcador: false);
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Não foi possível reaplicar o atributo após a distribuição: jogador não encontrado.");
            return;
        }

        // Preserva eventual função personalizada que já tenha sido registrada.
        RegistroFuncoesJogadores.Atribuicao atual =
            RegistroFuncoesJogadores.Obter(oportunistaPlayerId);
        if (atual?.Personalizada != true)
            RegistroFuncoesJogadores.Registrar(jogador, jogador.Data.RoleType);

        RegistroFuncoesJogadores.DefinirOportunista(oportunistaPlayerId, true);

        // Recoloca o marcador depois que o jogo restaurar os nomes no início.
        MarcarNomeGlobal(jogador);
        SistemaNomesLocais.Atualizar();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Oportunista] Atributo reaplicado após a distribuição: PlayerId=" +
            oportunistaPlayerId + ", função base=" + jogador.Data.RoleType + ".");
    }

    internal static bool EhOportunista(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return false;

        // O PlayerId é mantido em memória durante a partida. O registro de
        // funções é limpo no CoStartGame e o jogo pode restaurar o nome original,
        // então o atributo não pode depender somente do marcador invisível.
        if (ativo && oportunistaPlayerId != byte.MaxValue &&
            jogador.PlayerId == oportunistaPlayerId)
            return true;

        if (RegistroFuncoesJogadores.Obter(jogador.PlayerId)?.Oportunista == true)
            return true;

        return !string.IsNullOrEmpty(jogador.Data.PlayerName) &&
            jogador.Data.PlayerName.Contains(MARCADOR, StringComparison.Ordinal);
    }

    internal static string RemoverMarcador(string nome)
    {
        return string.IsNullOrEmpty(nome) ? nome :
            nome.Replace(MARCADOR, string.Empty, StringComparison.Ordinal);
    }

    private static void MarcarNomeGlobal(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null ||
            AmongUsClient.Instance == null || !YouTubeVoteBridgePlugin.EhHost())
            return;

        string nomeAtual = jogador.Data.PlayerName ?? string.Empty;
        if (nomeAtual.Contains(MARCADOR, StringComparison.Ordinal))
            return;

        EnviarNomeGlobal(jogador, nomeAtual + MARCADOR);
    }

    internal static void EnviarNomeGlobal(PlayerControl jogador, string nome)
    {
        if (jogador == null || jogador.Data == null ||
            AmongUsClient.Instance == null || string.IsNullOrEmpty(nome))
            return;

        MessageWriter writer = null;
        try
        {
            // Mesmo envelope GameData/Rpc SetName usado pelo próprio mod e pelo EHR.
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.StartMessage(2);
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();
            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);

            // Atualiza também o host, que não recebe de volta o próprio broadcast.
            jogador.SetName(nome);
            jogador.Data.PlayerName = nome;
            if (jogador.cosmetics?.nameText != null)
                jogador.cosmetics.nameText.text = nome;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Falha ao sincronizar o marcador: " + ex);
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
    }

    internal static void LimparMarcadores()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.AllPlayerControls == null)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                string.IsNullOrEmpty(jogador.Data.PlayerName) ||
                !jogador.Data.PlayerName.Contains(MARCADOR, StringComparison.Ordinal))
                continue;

            EnviarNomeGlobal(jogador, RemoverMarcador(jogador.Data.PlayerName));
        }
    }

    private static object LerMembro(object alvo, params string[] nomes)
    {
        if (alvo == null) return null;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type tipo = alvo.GetType();

        foreach (string nome in nomes)
        {
            try
            {
                FieldInfo campo = tipo.GetField(nome, flags);
                if (campo != null) return campo.GetValue(alvo);

                PropertyInfo propriedade = tipo.GetProperty(nome, flags);
                if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
                    return propriedade.GetValue(alvo);
            }
            catch { }
        }

        return null;
    }

    private static object LerMembroEstatico(Type tipo, string nome)
    {
        if (tipo == null) return null;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            FieldInfo campo = tipo.GetField(nome, flags);
            if (campo != null) return campo.GetValue(null);

            PropertyInfo propriedade = tipo.GetProperty(nome, flags);
            if (propriedade != null && propriedade.GetIndexParameters().Length == 0)
                return propriedade.GetValue(null);
        }
        catch { }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<object> Enumerar(object colecao)
    {
        if (colecao is IEnumerable itens)
        {
            foreach (object item in itens)
                yield return item;
            yield break;
        }

        // Fallback para listas IL2CPP que expõem GetEnumerator, mas não
        // implementam System.Collections.IEnumerable no wrapper gerenciado.
        object enumerador = null;
        try
        {
            MethodInfo obter = colecao?.GetType().GetMethod(
                "GetEnumerator", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            enumerador = obter?.Invoke(colecao, null);
            if (enumerador == null)
                yield break;

            MethodInfo mover = enumerador.GetType().GetMethod("MoveNext");
            PropertyInfo atual = enumerador.GetType().GetProperty("Current");
            while (mover != null && atual != null &&
                Convert.ToBoolean(mover.Invoke(enumerador, null)))
                yield return atual.GetValue(enumerador);
        }
        finally
        {
            if (enumerador is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private static bool JaEstaNaLista(object vencedores, byte playerId, string nome)
    {
        foreach (object item in Enumerar(vencedores))
        {
            object id = LerMembro(item, "PlayerId", "playerId", "PlayerID");
            if (id != null)
            {
                try
                {
                    if (Convert.ToByte(id) == playerId)
                        return true;
                }
                catch { }
            }

            object nomeItem = LerMembro(item, "PlayerName", "playerName", "Name", "name");
            if (nomeItem != null &&
                string.Equals(RemoverMarcador(nomeItem.ToString()), RemoverMarcador(nome), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    internal static void AdicionarOportunistaAosVencedores()
    {
        try
        {
            Type tempData = AccessTools.TypeByName("TempData");
            Type winningData = AccessTools.TypeByName("WinningPlayerData");
            object vencedores = LerMembroEstatico(tempData, "winners");

            if (vencedores == null || winningData == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Oportunista] Não foi possível localizar TempData.winners/WinningPlayerData.");
                return;
            }

            // Se a lista está vazia, o resultado não tem um time vencedor (ex.: empate).
            object quantidade = LerMembro(vencedores, "Count", "count");
            if (quantidade != null && Convert.ToInt32(quantidade) <= 0)
                return;

            if (PlayerControl.AllPlayerControls == null)
                return;

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.IsDead || jogador.Data.Disconnected ||
                    !EhOportunista(jogador))
                    continue;

                string nome = jogador.Data.PlayerName ?? string.Empty;
                if (JaEstaNaLista(vencedores, jogador.PlayerId, nome))
                    continue;

                ConstructorInfo construtor = winningData
                    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(c =>
                    {
                        ParameterInfo[] parametros = c.GetParameters();
                        return parametros.Length == 1 &&
                            parametros[0].ParameterType.IsAssignableFrom(jogador.Data.GetType());
                    });

                if (construtor == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Oportunista] Construtor de WinningPlayerData(NetworkedPlayerInfo) não encontrado.");
                    return;
                }

                object entrada = construtor.Invoke(new object[] { jogador.Data });
                MethodInfo adicionar = vencedores.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] parametros = m.GetParameters();
                        return m.Name == "Add" && parametros.Length == 1 &&
                            parametros[0].ParameterType.IsAssignableFrom(winningData);
                    });

                if (adicionar == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Oportunista] Método Add não encontrado em TempData.winners.");
                    return;
                }

                adicionar.Invoke(vencedores, new[] { entrada });
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Oportunista] Jogador vivo adicionado à lista de vencedores: PlayerId=" +
                    jogador.PlayerId + ".");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Não foi possível incluir o jogador na tela de vitória: " + ex);
        }
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
internal static class EventoOportunistaPrepararVitoriaPatch
{
    private static bool Prefix(GameManager __instance, GameOverReason __0)
    {
        return EventoOportunista.InterceptarFimDeJogo(__instance, __0);
    }
}

[HarmonyPatch(typeof(EndGameManager), nameof(EndGameManager.Start))]
internal static class EventoOportunistaVencedoresPatch
{
    private static void Prefix()
    {
        // TempData.winners já contém os vencedores do time; acrescentamos apenas
        // o Oportunista vivo, sem substituir nem alterar o motivo da vitória.
        EventoOportunista.AdicionarOportunistaAosVencedores();
    }

    private static void Postfix()
    {
        // O marcador só precisa existir até a lista de vencedores ser montada.
        EventoOportunista.LimparMarcadores();
        SistemaNomesLocais.LimparMarcadoresDeFuncao();
        EventoOportunista.Resetar();
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
internal static class EventoOportunistaLimparInicioPatch
{
    private static void Prefix()
    {
        EventoOportunista.LimparMarcadores();
        SistemaNomesLocais.LimparMarcadoresDeFuncao();
        EventoOportunista.Resetar();
    }
}

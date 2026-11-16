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
        if (oportunistaPlayerId != byte.MaxValue &&
            RegistroFuncoesJogadores.Obter(oportunistaPlayerId) != null)
            RegistroFuncoesJogadores.DefinirOportunista(oportunistaPlayerId, false);

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

        // Segunda chamada: a preparação terminou. Libera exatamente o motivo
        // original que iniciou este fluxo, sem substituí-lo por outra vitória.
        if (liberarRpcFinal)
        {
            liberarRpcFinal = false;
            fimPendente = false;
            return true;
        }

        // Impede que mais de uma condição de vitória inicie a preparação.
        if (fimPendente)
            return false;

        PlayerControl oportunista = EncontrarOportunista();
        if (oportunista == null)
            return true;

        if (!TentarObterFantasmaOposto(motivo, out RoleTypes roleFantasma))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Oportunista] Fim de jogo sem vencedor convencional (" + motivo +
                "); mantendo a função atual.");
            return true;
        }

        if (manager == null || PlayerControl.LocalPlayer == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] GameManager ou jogador local indisponível; não foi possível preparar a vitória.");
            return true;
        }

        fimPendente = true;
        oportunistaFinalId = oportunista.PlayerId;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Oportunista] Fim de jogo interceptado. Motivo original=" + motivo +
            "; vencedor=" + (roleFantasma == RoleTypes.ImpostorGhost ? "Tripulantes" : "Impostores") +
            "; função oposta=" + roleFantasma +
            "; PlayerId=" + oportunistaFinalId + ".");

        MonoBehaviourExtensions.StartCoroutine(
            PlayerControl.LocalPlayer,
            PrepararOportunistaEEncerrar(manager, motivo, oportunistaFinalId, roleFantasma));

        // O primeiro encerramento é bloqueado. A coroutine aplica função e revive
        // na rede antes de chamar RpcEndGame novamente com o mesmo motivo.
        return false;
    }

    private static bool TentarObterFantasmaOposto(
        GameOverReason motivo, out RoleTypes roleFantasma)
    {
        string nome = motivo.ToString();

        // O Oportunista pertence ao time oposto ao vencedor:
        // vitória dos tripulantes -> ImpostorGhost;
        // vitória dos impostores -> CrewmateGhost.
        // O enum vanilla usa HumansByVote/HumansByTask e
        // HumansDisconnect, não necessariamente Crewmates.
        if (nome.IndexOf("Human", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nome.IndexOf("Crewmate", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            roleFantasma = RoleTypes.ImpostorGhost;
            return true;
        }

        if (nome.IndexOf("Impostors", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nome.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            roleFantasma = RoleTypes.CrewmateGhost;
            return true;
        }

        roleFantasma = default;
        return false;
    }

    private static IEnumerator PrepararOportunistaEEncerrar(
        GameManager manager, GameOverReason motivo, byte playerId, RoleTypes roleFantasma)
    {
        // Deixa o primeiro RpcEndGame terminar de ser interceptado antes de
        // iniciar a sequência de sincronização.
        yield return new WaitForSecondsRealtime(0.15f);

        PlayerControl jogador = EncontrarOportunista(playerId, exigirMarcador: false);
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] O jogador não foi encontrado para a preparação final. " +
                "O encerramento original será liberado sem a conversão.");
        }
        else
        {
            // Etapa 1: aplica a função fantasma globalmente, como na troca de
            // função vanilla/EHR, antes de restaurar o estado vivo.
            try
            {
                if (!RoleSwitchAPI.AplicarFuncaoGlobal(jogador, roleFantasma))
                    throw new InvalidOperationException("RoleSwitchAPI não conseguiu aplicar a função fantasma.");
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Oportunista] Função fantasma enviada pelo fluxo EHR: PlayerId=" +
                    playerId + ", Role=" + roleFantasma + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Oportunista] Falha ao enviar a função fantasma: " + ex);
            }

            // Aguarda a função chegar aos clientes antes de publicar IsDead=false.
            yield return new WaitForSecondsRealtime(0.15f);

            // Etapa 2: revive localmente e replica o NetworkedPlayerInfo usando
            // o mesmo fluxo de estado de rede usado pelo NPC Assassino:
            // IsDead=false -> SetDirtyBit -> SendAllStreamedObjects.
            try
            {
                EnviarReviveSincronizado(jogador);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Oportunista] Revive de rede concluído para PlayerId=" + playerId + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Oportunista] Falha ao sincronizar o revive: " + ex);
            }

            // Dá tempo para os clientes aplicarem o estado vivo e atualizarem
            // os componentes visuais antes de criar a tela de vitória.
            yield return new WaitForSecondsRealtime(0.35f);
        }

        if (manager == null)
            manager = GameManager.Instance;

        if (manager == null)
        {
            fimPendente = false;
            liberarRpcFinal = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Oportunista] GameManager indisponível; não foi possível liberar o encerramento original.");
            yield break;
        }

        // A chamada recursiva passa pelo Harmony novamente. A flag libera apenas
        // esta chamada e mantém o GameOverReason que o jogo havia escolhido.
        liberarRpcFinal = true;
        fimPendente = false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Oportunista] Conversão e revive preparados. Liberando RpcEndGame " +
            "com o motivo original: " + motivo + ".");

        manager.RpcEndGame(motivo, false);
    }

    private const byte RpcReviveOportunista = 249;

    /// <summary>
    /// Revive no host, envia um RPC local aos clientes com o mod e publica
    /// NetworkedPlayerInfo atualizado para que clientes vanilla recebam IsDead=false.
    /// </summary>
    private static void EnviarReviveSincronizado(PlayerControl jogador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            jogador == null ||
            jogador.Data == null ||
            AmongUsClient.Instance == null)
            return;

        AmongUsClient cliente = AmongUsClient.Instance;

        // Atualiza a instância do jogador no host.
        jogador.Revive();
        jogador.Data.IsDead = false;

        // Mesmo padrão de sincronização de vida usado pelo NPC Assassino:
        // marca o campo de morte como alterado e força o envio dos objetos
        // transmitidos pelo GameData para todos os clientes, inclusive vanilla.
        jogador.Data.SetDirtyBit(uint.MaxValue);
        cliente.SendAllStreamedObjects();

        // O fluxo vanilla acima replica NetworkedPlayerInfo. Para os clientes
        // com o mod, também chamamos Revive() localmente para atualizar
        // PlayerControl e os componentes visuais caso IsDead já tenha chegado.
        if (PlayerControl.LocalPlayer == null)
            return;

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
                    "[F7/Oportunista] Falha ao enviar RPC de atualização visual do revive para ClientId=" +
                    clientId + ": " + ex.Message);
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
                    jogador.PlayerId != playerId || jogador.Data.Disconnected)
                    continue;

                // Não verificamos IsDead aqui: o GameData pode ter recebido
                // IsDead=false antes deste RPC, mas o PlayerControl ainda pode
                // conservar a aparência/animação de morto. Revive() é reaplicado
                // para sincronizar também a representação visual.
                jogador.Revive();
                jogador.Data.IsDead = false;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Oportunista] Revive visual aplicado no cliente para PlayerId=" +
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
                // O Oportunista pode ter acabado de ser convertido em fantasma.
                // Mesmo que IsDead ainda esteja sincronizando, ele deve entrar nos vencedores.
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || !EhOportunista(jogador))
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
[HarmonyPriority(Priority.First)]
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

// O estado do Oportunista é preservado durante a transição para a partida.
// A limpeza ocorre após a montagem da lista de vencedores ou em OnGameEnd.

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class EventoOportunistaReviveRpcPatch
{
    private const byte RpcReviveOportunista = 249;

    [HarmonyPrefix]
    private static bool Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] byte callId,
        [HarmonyArgument(1)] MessageReader reader)
    {
        if (callId != RpcReviveOportunista)
            return true;

        // O RPC é enviado apenas aos clientes com o mod. Ele não deve chegar
        // ao fluxo vanilla de HandleRpc nem colidir com o RPC de nome do NPC (251).
        if (!YouTubeVoteBridgePlugin.EhHost())
            EventoOportunista.ReceberRevive(reader);

        return false;
    }
}

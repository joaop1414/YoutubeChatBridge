using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Hazel;

namespace YouTubeVoteBridge;

internal sealed class EventoOportunista : EventoVotacao
{
    internal const string MARCADOR = "<size=0>YTVB_OPPORTUNISTA</size>";

    public override string Nome => "Um jogador aleatório será Oportunista";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        ListarCandidatos(out var candidatos);
        if (candidatos.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Oportunista] Não havia jogadores vivos e conectados para sortear.");
            return;
        }

        PlayerControl escolhido = candidatos[System.Random.Shared.Next(candidatos.Count)];
        RegistroFuncoesJogadores.DefinirOportunista(escolhido.PlayerId, true);
        MarcarNomeGlobal(escolhido);

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

    internal static bool EhOportunista(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return false;

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

    private static void EnviarNomeGlobal(PlayerControl jogador, string nome)
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
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGame))]
internal static class EventoOportunistaLimparInicioPatch
{
    private static void Prefix()
    {
        EventoOportunista.LimparMarcadores();
    }
}

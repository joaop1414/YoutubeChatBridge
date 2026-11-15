using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;
using BepInEx.Unity.IL2CPP.Utils;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// O Anjo Traidor começa como Phantom impostor. Ao tentar desaparecer,
/// morre de verdade. Todos recebem ImpostorGhost, exceto o próprio jogador,
/// que recebe GuardianAngel somente no seu cliente.
/// </summary>
internal sealed class EventoAnjoTraidor : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static bool tentandoSelecionar;
    private static bool transformado;
    private static bool habilidadeEncerrada;
    private static byte playerIdSelecionado = byte.MaxValue;
    private static int clientIdSelecionado = -1;
    private static Action restaurarCooldownProtectLocal;

    // Os nomes são enviados somente ao cliente dono do Phantom escolhido.
    // Assim, nem os demais jogadores nem o GameData do host recebem a cor vermelha.
    private static readonly Dictionary<byte, string> nomesOriginais = new();

    public override string Nome => "Anjo Traidor";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        RestaurarNomesLocais();
        armado = true;
        ativo = false;
        tentandoSelecionar = false;
        transformado = false;
        habilidadeEncerrada = false;
        playerIdSelecionado = byte.MaxValue;
        clientIdSelecionado = -1;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Anjo Traidor] Evento armado. Aguardando a seleção vanilla de funções.");

        // Permite testar o evento caso ele seja acionado depois da revelação.
        if (ShipStatus.Instance != null && LobbyTagManager.PartidaIniciada())
            SelecionarPhantom();
    }

    /// <summary>
    /// Depois que o jogo termina a distribuição, encontra o Phantom que veio
    /// da lista vanilla. A atribuição forçada, quando necessária, acontece
    /// antes em LogicRoleSelectionNormal.AssignRolesFromList.
    /// </summary>
    internal static void SelecionarPhantom()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !armado || ativo || tentandoSelecionar)
            return;

        tentandoSelecionar = true;

        try
        {
            PlayerControl escolhido = null;

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (!JogadorValido(jogador))
                    continue;

                if (jogador.Data.RoleType == RoleTypes.Phantom)
                {
                    escolhido = jogador;
                    break;
                }
            }

            if (escolhido == null)
            {
                armado = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] A lista vanilla não produziu nenhum Phantom impostor. " +
                    "Nenhum tripulante será convertido depois da distribuição; evento cancelado.");
                return;
            }

            RegistrarPhantomSelecionado(escolhido);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Phantom vanilla confirmado: " +
                escolhido.Data.PlayerName + " (PlayerId=" + escolhido.PlayerId + ").");
        }
        catch (Exception ex)
        {
            armado = false;
            ativo = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao localizar o Phantom após a distribuição: " + ex);
        }
    }

    /// <summary>
    /// Chamado pelo Prefix de AssignRolesFromList quando o pool atual é o dos
    /// impostores e ainda não existe Phantom na lista. Replica a estratégia do
    /// Hydra: remove um jogador e uma função do mesmo pool, atribui Phantom
    /// diretamente e incrementa rolesAssigned para manter a contagem vanilla.
    /// </summary>
    internal static void TentarSubstituirImpostorNaLista(
        ref Il2CppSystem.Collections.Generic.List<NetworkedPlayerInfo> players,
        ref Il2CppSystem.Collections.Generic.List<RoleTypes> roleList,
        ref int rolesAssigned)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !armado || ativo ||
            players == null || roleList == null)
            return;

        try
        {
            bool poolDeImpostores = false;
            bool phantomJaNaLista = false;

            for (int i = 0; i < roleList.Count; i++)
            {
                RoleTypes role = roleList[i];

                if (role == RoleTypes.Phantom)
                    phantomJaNaLista = true;

                if (RoleManager.IsImpostorRole(role) && !RoleManager.IsGhostRole(role))
                    poolDeImpostores = true;
            }

            // AssignRolesFromList é chamado para diferentes facções.
            // Só mexemos no pool que contém funções de impostor.
            if (!poolDeImpostores || phantomJaNaLista)
                return;

            // Não substitui uma função se um Phantom já tiver sido atribuído
            // em uma chamada anterior do seletor.
            foreach (PlayerControl existente in PlayerControl.AllPlayerControls)
            {
                if (JogadorValido(existente) &&
                    existente.Data.RoleType == RoleTypes.Phantom)
                {
                    RegistrarPhantomSelecionado(existente);
                    return;
                }
            }

            List<int> indicesJogadoresValidos = new();

            for (int i = 0; i < players.Count; i++)
            {
                NetworkedPlayerInfo dados = players[i];
                if (dados == null || dados.Disconnected || dados.IsDead)
                    continue;

                PlayerControl controle = ObterJogador(dados.PlayerId);
                if (JogadorValido(controle))
                    indicesJogadoresValidos.Add(i);
            }

            if (indicesJogadoresValidos.Count == 0)
                return;

            // Prefere substituir o papel básico Impostor. Se não houver,
            // substitui outra função impostora não-fantasma do mesmo pool.
            int indiceFuncao = -1;
            for (int i = 0; i < roleList.Count; i++)
            {
                if (roleList[i] == RoleTypes.Impostor)
                {
                    indiceFuncao = i;
                    break;
                }
            }

            if (indiceFuncao < 0)
            {
                for (int i = 0; i < roleList.Count; i++)
                {
                    RoleTypes role = roleList[i];
                    if (RoleManager.IsImpostorRole(role) &&
                        !RoleManager.IsGhostRole(role) &&
                        role != RoleTypes.Phantom)
                    {
                        indiceFuncao = i;
                        break;
                    }
                }
            }

            if (indiceFuncao < 0)
                return;

            int indiceJogador = indicesJogadoresValidos[
                System.Random.Shared.Next(indicesJogadoresValidos.Count)];

            NetworkedPlayerInfo dadosEscolhidos = players[indiceJogador];
            PlayerControl escolhido = ObterJogador(dadosEscolhidos.PlayerId);
            RoleTypes funcaoSubstituida = roleList[indiceFuncao];

            if (!JogadorValido(escolhido))
                return;

            // Remove exatamente uma vaga de jogador e uma vaga de função.
            // Assim Phantom substitui uma atribuição impostora existente,
            // sem criar um impostor extra e sem tocar na lista de tripulantes.
            players.RemoveAt(indiceJogador);
            roleList.RemoveAt(indiceFuncao);

            // O Hydra contorna um bug da cutscene quando a função fantasma
            // seria a última função atribuída: primeiro envia a função base.
            if (RoleManager.IsGhostRole(RoleTypes.Phantom) && players.Count == 0)
                escolhido.RpcSetRole(RoleTypes.Impostor);

            escolhido.RpcSetRole(RoleTypes.Phantom);
            rolesAssigned++;

            RegistrarPhantomSelecionado(escolhido);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Hydra-style AssignRolesFromList: " +
                "uma vaga impostora foi substituída por Phantom. Jogador=" +
                escolhido.Data.PlayerName + ", PlayerId=" + escolhido.PlayerId +
                ", função substituída=" + funcaoSubstituida + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao substituir função na lista de impostores: " + ex);
        }
    }

    private static void RegistrarPhantomSelecionado(PlayerControl escolhido)
    {
        if (escolhido == null || escolhido.Data == null)
            return;

        playerIdSelecionado = escolhido.PlayerId;
        clientIdSelecionado = escolhido.OwnerId;
        ativo = true;
        tentandoSelecionar = true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Anjo Traidor] Jogador escolhido: " + escolhido.Data.PlayerName +
            " (PlayerId=" + playerIdSelecionado +
            ", ClientId=" + clientIdSelecionado + ").");
    }

    private static bool JogadorValido(PlayerControl jogador)
    {
        return jogador != null &&
            jogador.Data != null &&
            !jogador.Data.Disconnected &&
            !jogador.Data.IsDead &&
            !jogador.notRealPlayer;
    }

    internal static bool EhPhantomSelecionado(PlayerControl jogador)
    {
        return ativo &&
            !transformado &&
            jogador != null &&
            jogador.PlayerId == playerIdSelecionado;
    }

    /// <summary>
    /// Chamado no host quando o Phantom tenta usar Vanish.
    /// Cancela a invisibilidade, mata o jogador e aplica funções diferentes por cliente.
    /// </summary>
    internal static bool AoTentarDesaparecer(PlayerControl jogador)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !EhPhantomSelecionado(jogador) ||
            jogador.Data == null ||
            jogador.Data.IsDead ||
            jogador.Data.Disconnected ||
            MeetingHud.Instance != null ||
            transformado)
            return false;

        transformado = true;

        try
        {
            CapturarNomesDosImpostores();

            // Primeiro executa a morte real e envia o papel de fantasma
            // impostor para todos os clientes.
            jogador.RpcMurderPlayer(jogador, true);

            // Corrige o estado autoritativo local quando o Phantom é o host.
            jogador.Data.IsDead = true;
            NetworkedPlayerInfo dadosHost = GameData.Instance?.GetPlayerById(jogador.PlayerId);
            if (dadosHost != null)
            {
                dadosHost.IsDead = true;
                dadosHost.SetDirtyBit(uint.MaxValue);
            }
            try { AmongUsClient.Instance?.SendAllStreamedObjects(); }
            catch (Exception exSync)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Falha ao propagar estado morto do Phantom: " + exSync.Message);
            }

            jogador.RpcSetRole(RoleTypes.ImpostorGhost, true);

            // O papel privado é enviado depois de a morte e o papel global
            // terem tido tempo para serem processados pelos clientes.
            MonoBehaviourExtensions.StartCoroutine(jogador, ConcluirTransformacaoAposMorte(jogador));

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Morte enviada para PlayerId=" + playerIdSelecionado +
                ". ImpostorGhost enviado globalmente; GuardianAngel será enviado " +
                "ao cliente dono após 0,25 segundo.");
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao iniciar a conversão do Phantom em Anjo da Guarda: " + ex);
            return true;
        }
    }

    private static IEnumerator ConcluirTransformacaoAposMorte(PlayerControl jogador)
    {
        yield return new WaitForSecondsRealtime(0.25f);

        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !ativo ||
            !transformado ||
            habilidadeEncerrada ||
            jogador == null ||
            jogador.Data == null ||
            jogador.Data.Disconnected ||
            jogador.PlayerId != playerIdSelecionado)
            yield break;

        try
        {
            bool enviado = RoleSwitchAPI.RpcSetRoleDesync(
                jogador, RoleTypes.GuardianAngel, clientIdSelecionado);

            if (!enviado)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Não foi possível enviar GuardianAngel ao cliente dono. " +
                    "O jogador continuará como ImpostorGhost.");
                yield break;
            }

            if (!EnviarConfiguracaoProtectAoAnjo(jogador))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Não foi possível sincronizar ProtectCooldown com KillCooldown para o cliente do Anjo.");
            }

            AplicarNomesLocais();

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] RPC individual concluído após 0,25 segundo: " +
                "PlayerId=" + playerIdSelecionado +
                " recebeu GuardianAngel no cliente dono; demais clientes continuam com ImpostorGhost.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao enviar GuardianAngel após a morte: " + ex);
        }
    }

    internal static bool DeveBloquearEscudo(PlayerControl anjo)
    {
        return ativo &&
            habilidadeEncerrada &&
            anjo != null &&
            anjo.PlayerId == playerIdSelecionado;
    }

    /// <summary>
    /// Aprova o Protect somente no cliente dono do Anjo Traidor para que o
    /// próprio GuardianAngel aplique o cooldown vanilla. Depois disso, envia
    /// dois MurderPlayer: primeiro ao dono do Anjo e depois globalmente.
    /// </summary>
    internal static bool AoTentarProteger(PlayerControl anjo, PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !ativo || anjo == null)
            return false;

        // Durante o evento, nenhum outro GuardianAngel pode executar Protect.
        if (anjo.PlayerId != playerIdSelecionado)
            return true;

        if (!transformado || habilidadeEncerrada ||
            MeetingHud.Instance != null ||
            ShipStatus.Instance == null)
            return true;

        if (alvo == null ||
            alvo.Data == null ||
            alvo.Data.IsDead ||
            alvo.Data.Disconnected ||
            alvo.PlayerId == playerIdSelecionado)
            return true;

        try
        {
            AmongUsClient cliente = AmongUsClient.Instance;
            if (cliente == null)
                return true;

            // 1) Simula o sucesso vanilla de Protect apenas para o impostor.
            // Isso permite que o GuardianAngel local execute seu próprio
            // SetCooldown, sem mostrar proteção aos demais clientes.
            if (anjo.OwnerId == cliente.ClientId)
            {
                anjo.ProtectPlayer(alvo, 0);

                // O host já recebeu a configuração local do GuardianAngel
                // em EnviarConfiguracaoProtectAoAnjo; o vanilla aplica o cooldown.
            }
            else
            {
                MessageWriter protectWriter = cliente.StartRpcImmediately(
                    anjo.NetId,
                    (byte)RpcCalls.ProtectPlayer,
                    SendOption.Reliable,
                    anjo.OwnerId);

                protectWriter.WriteNetObject(alvo);
                protectWriter.Write(0);
                cliente.FinishRpcImmediately(protectWriter);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Protect aprovado somente para o cliente dono. " +
                "PlayerId=" + playerIdSelecionado + ", alvo=" + alvo.PlayerId + ".");

            // O resultado precisa ser um assassinato bem-sucedido e atribuído
            // ao impostor, não uma morte ambiental nem uma proteção falha.
            MurderResultFlags resultado =
                MurderResultFlags.Succeeded | MurderResultFlags.DecisionByHost;

            // 2) Primeiro MurderPlayer: somente para o cliente do impostor.
            // Quando o host é o próprio Anjo, executamos o equivalente local.
            if (anjo.OwnerId == cliente.ClientId)
            {
                anjo.MurderPlayer(alvo, resultado);
            }
            else
            {
                MessageWriter murderOwnerWriter = cliente.StartRpcImmediately(
                    anjo.NetId,
                    (byte)RpcCalls.MurderPlayer,
                    SendOption.Reliable,
                    anjo.OwnerId);

                murderOwnerWriter.WriteNetObject(alvo);
                murderOwnerWriter.Write((int)resultado);
                cliente.FinishRpcImmediately(murderOwnerWriter);

                // O host também precisa registrar a morte em seu estado
                // autoritativo antes de publicar o RPC global.
                anjo.MurderPlayer(alvo, resultado);
            }

            // O host deve refletir a vítima como morta imediatamente,
            // mesmo quando o RPC específico/global não atualiza seu GameData local.
            alvo.Data.IsDead = true;
            NetworkedPlayerInfo dadosAlvoHost = GameData.Instance?.GetPlayerById(alvo.PlayerId);
            if (dadosAlvoHost != null)
            {
                dadosAlvoHost.IsDead = true;
                dadosAlvoHost.SetDirtyBit(uint.MaxValue);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Primeiro MurderPlayer enviado somente ao dono " +
                "do Anjo Traidor. Killer=" + anjo.PlayerId + ", alvo=" + alvo.PlayerId + ".");

            // 3) Segundo MurderPlayer: broadcast para todos os clientes.
            MessageWriter murderGlobalWriter = cliente.StartRpcImmediately(
                anjo.NetId,
                (byte)RpcCalls.MurderPlayer,
                SendOption.Reliable,
                -1);

            murderGlobalWriter.WriteNetObject(alvo);
            murderGlobalWriter.Write((int)resultado);
            cliente.FinishRpcImmediately(murderGlobalWriter);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Segundo MurderPlayer enviado globalmente. " +
                "Impostor=" + anjo.PlayerId + ", vítima=" + alvo.PlayerId + ".");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha no fluxo Protect + dois MurderPlayer: " + ex);
        }

        // O CheckProtect original não pode continuar, pois enviaria Protect
        // vanilla para todos os clientes.
        return true;
    }

    /// <summary>
    /// Envia uma LogicOptions serializada somente ao cliente dono do Anjo Traidor.
    /// O pacote usa o mesmo envelope GameDataTo/LogicOptions do jogo vanilla e
    /// contém ProtectCooldown igual ao KillCooldown, sem alterar a configuração
    /// dos demais jogadores.
    /// </summary>
    private static bool EnviarConfiguracaoProtectAoAnjo(PlayerControl anjo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || anjo == null ||
            AmongUsClient.Instance == null || GameManager.Instance == null)
            return false;

        IGameOptions opcoesAtuais = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opcoesAtuais == null)
            return false;

        NormalGameOptionsV12 opcoes;
        try
        {
            opcoes = opcoesAtuais.TryCast<NormalGameOptionsV12>();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] Não foi possível converter as opções para NormalGameOptionsV12: " + ex.Message);
            return false;
        }

        if (opcoes == null)
            return false;

        float killCooldown = Mathf.Clamp(
            opcoes.GetFloat(FloatOptionNames.KillCooldown), 0f, 255f);

        // Para o próprio host, a configuração precisa permanecer aplicada
        // no LogicOptions local, pois não existe outro cliente para receber o pacote.
        if (anjo.OwnerId == AmongUsClient.Instance.ClientId)
        {
            try
            {
                restaurarCooldownProtectLocal?.Invoke();
                restaurarCooldownProtectLocal = null;

                if (!TentarDefinirProtectCooldown(opcoes, killCooldown, out Action restaurar))
                    return false;

                restaurarCooldownProtectLocal = restaurar;
                AplicarOpcoesNaLogicaLocal(opcoes);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] ProtectCooldown local do host definido para " +
                    killCooldown.ToString("0.##") + "s (igual ao KillCooldown).");
                return true;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Anjo Traidor] Falha ao aplicar ProtectCooldown local: " + ex);
                return false;
            }
        }

        byte[] dadosOpcoes;
        Action restaurarTemporariamente = null;
        try
        {
            if (!TentarDefinirProtectCooldown(opcoes, killCooldown, out restaurarTemporariamente))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Não foi possível localizar GuardianAngelRoleOptions.ProtectCooldown.");
                return false;
            }

            MessageWriter serializador = MessageWriter.Get();
            try
            {
                serializador.Write(opcoes.Version);
                serializador.StartMessage(0);
                serializador.Write((byte)opcoes.GameMode);
                NormalGameOptionsV12.Serialize(serializador, opcoes);
                serializador.EndMessage();
                dadosOpcoes = serializador.ToByteArray(false);
            }
            finally
            {
                serializador.Recycle();
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao serializar ProtectCooldown individual: " + ex);
            return false;
        }
        finally
        {
            // A opção global do host volta ao valor original antes de enviar.
            try { restaurarTemporariamente?.Invoke(); } catch { }
        }

        bool encontrouLogica = false;
        bool enviou = false;

        try
        {
            int quantidade = GameManager.Instance.LogicComponents.Count;
            for (int i = 0; i < quantidade; i++)
            {
                if (GameManager.Instance.LogicComponents[i] is not LogicOptions)
                    continue;

                encontrouLogica = true;

                MessageWriter pacote = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    // GameDataTo: destinatário é exclusivamente o dono do Anjo.
                    pacote.StartMessage(6);
                    pacote.Write(AmongUsClient.Instance.GameId);
                    pacote.WritePacked(anjo.OwnerId);

                    pacote.StartMessage(1);
                    pacote.WritePacked(GameManager.Instance.NetId);
                    pacote.StartMessage((byte)i);
                    pacote.WriteBytesAndSize(dadosOpcoes);
                    pacote.EndMessage();
                    pacote.EndMessage();
                    pacote.EndMessage();

                    AmongUsClient.Instance.SendOrDisconnect(pacote);
                    enviou = true;

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Anjo Traidor] RPC de configuração enviado somente ao Anjo: " +
                        "PlayerId=" + anjo.PlayerId + ", OwnerId=" + anjo.OwnerId +
                        ", LogicOptionsIndex=" + i + ", ProtectCooldown=" +
                        killCooldown.ToString("0.##") + "s.");
                }
                finally
                {
                    pacote.Recycle();
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao enviar RPC individual de configuração: " + ex);
            return false;
        }

        if (!encontrouLogica)
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] Nenhum LogicOptions foi encontrado para enviar a configuração.");

        return encontrouLogica && enviou;
    }

    private static void AplicarOpcoesNaLogicaLocal(IGameOptions opcoes)
    {
        GameManager gerenciador = GameManager.Instance;
        if (gerenciador?.LogicComponents == null)
            return;

        foreach (GameLogicComponent componente in gerenciador.LogicComponents)
            if (componente is LogicOptions logicOptions)
                logicOptions.SetGameOptions(opcoes);

        GameOptionsManager.Instance.CurrentGameOptions = opcoes;
    }

    /// <summary>
    /// Localiza o objeto de opções do Anjo da Guarda por reflexão para não
    /// depender de nomes de campos internos que mudam entre versões do jogo.
    /// </summary>
    private static bool TentarDefinirProtectCooldown(
        object opcoes,
        float valor,
        out Action restaurar)
    {
        restaurar = null;
        object guardianOptions = EncontrarOpcoesGuardianAngel(
            opcoes, 0, new HashSet<int>());

        if (guardianOptions == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic;

        Type tipo = guardianOptions.GetType();
        FieldInfo campo = tipo.GetField("ProtectCooldown", flags);
        PropertyInfo propriedade = tipo.GetProperty("ProtectCooldown", flags);

        object cooldown = campo != null
            ? campo.GetValue(guardianOptions)
            : propriedade != null && propriedade.CanRead
                ? propriedade.GetValue(guardianOptions)
                : null;

        if (campo != null && EhNumero(campo.FieldType) &&
            !campo.IsInitOnly && !campo.IsLiteral)
        {
            object anterior = campo.GetValue(guardianOptions);
            campo.SetValue(guardianOptions, ConverterNumero(valor, campo.FieldType));
            restaurar = () => campo.SetValue(guardianOptions, anterior);
            return true;
        }

        if (propriedade != null && propriedade.CanRead && propriedade.CanWrite &&
            EhNumero(propriedade.PropertyType))
        {
            object anterior = propriedade.GetValue(guardianOptions);
            propriedade.SetValue(guardianOptions, ConverterNumero(valor, propriedade.PropertyType));
            restaurar = () => propriedade.SetValue(guardianOptions, anterior);
            return true;
        }

        // Algumas builds representam a opção como um objeto FloatOption
        // com a propriedade/field Value.
        if (cooldown != null && TentarDefinirValorInterno(cooldown, valor, out restaurar))
            return true;

        return false;
    }

    private static bool TentarDefinirValorInterno(object alvo, float valor, out Action restaurar)
    {
        restaurar = null;
        if (alvo == null)
            return false;

        const BindingFlags flags = BindingFlags.Instance |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic;
        Type tipo = alvo.GetType();

        FieldInfo campo = tipo.GetField("Value", flags);
        if (campo != null && EhNumero(campo.FieldType) &&
            !campo.IsInitOnly && !campo.IsLiteral)
        {
            object anterior = campo.GetValue(alvo);
            campo.SetValue(alvo, ConverterNumero(valor, campo.FieldType));
            restaurar = () => campo.SetValue(alvo, anterior);
            return true;
        }

        PropertyInfo propriedade = tipo.GetProperty("Value", flags);
        if (propriedade != null && propriedade.CanRead && propriedade.CanWrite &&
            EhNumero(propriedade.PropertyType))
        {
            object anterior = propriedade.GetValue(alvo);
            propriedade.SetValue(alvo, ConverterNumero(valor, propriedade.PropertyType));
            restaurar = () => propriedade.SetValue(alvo, anterior);
            return true;
        }

        MethodInfo setter = tipo.GetMethods(flags).FirstOrDefault(m =>
        {
            if (m.Name != "SetValue" && m.Name != "Set")
                return false;
            ParameterInfo[] parametros = m.GetParameters();
            return parametros.Length == 1 && EhNumero(parametros[0].ParameterType);
        });

        if (setter != null)
        {
            // Para wrappers sem getter público, tenta preservar o valor via Value.
            object anterior = null;
            PropertyInfo leitura = tipo.GetProperty("Value", flags);
            if (leitura != null && leitura.CanRead)
                anterior = leitura.GetValue(alvo);
            else
            {
                FieldInfo leituraCampo = tipo.GetField("Value", flags);
                if (leituraCampo != null)
                    anterior = leituraCampo.GetValue(alvo);
            }

            if (anterior == null)
                return false;

            setter.Invoke(alvo, new[] { ConverterNumero(valor, setter.GetParameters()[0].ParameterType) });
            restaurar = () => setter.Invoke(alvo, new[] { anterior });
            return true;
        }

        return false;
    }

    private static object EncontrarOpcoesGuardianAngel(
        object atual,
        int profundidade,
        HashSet<int> visitados)
    {
        if (atual == null || profundidade > 8)
            return null;

        Type tipo = atual.GetType();
        if (tipo.Name.IndexOf("GuardianAngel", StringComparison.OrdinalIgnoreCase) >= 0)
            return atual;

        if (tipo.IsPrimitive || tipo.IsEnum || atual is string || atual is Type)
            return null;

        int identidade = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(atual);
        if (!visitados.Add(identidade))
            return null;

        if (atual is IEnumerable lista)
        {
            foreach (object item in lista)
            {
                object encontrado = EncontrarOpcoesGuardianAngel(item, profundidade + 1, visitados);
                if (encontrado != null)
                    return encontrado;
            }
        }

        const BindingFlags flags = BindingFlags.Instance |
                                   BindingFlags.Public |
                                   BindingFlags.NonPublic;

        foreach (FieldInfo campo in tipo.GetFields(flags))
        {
            if (campo.FieldType.IsPrimitive || campo.FieldType.IsEnum ||
                campo.FieldType == typeof(string))
                continue;

            object filho = null;
            try { filho = campo.GetValue(atual); } catch { }
            object encontrado = EncontrarOpcoesGuardianAngel(filho, profundidade + 1, visitados);
            if (encontrado != null)
                return encontrado;
        }

        foreach (PropertyInfo propriedade in tipo.GetProperties(flags))
        {
            if (!propriedade.CanRead || propriedade.GetIndexParameters().Length != 0 ||
                propriedade.PropertyType.IsPrimitive || propriedade.PropertyType.IsEnum ||
                propriedade.PropertyType == typeof(string))
                continue;

            object filho = null;
            try { filho = propriedade.GetValue(atual); } catch { }
            object encontrado = EncontrarOpcoesGuardianAngel(filho, profundidade + 1, visitados);
            if (encontrado != null)
                return encontrado;
        }

        return null;
    }

    private static bool EhNumero(Type tipo)
    {
        return tipo == typeof(byte) || tipo == typeof(sbyte) ||
               tipo == typeof(short) || tipo == typeof(ushort) ||
               tipo == typeof(int) || tipo == typeof(uint) ||
               tipo == typeof(long) || tipo == typeof(ulong) ||
               tipo == typeof(float) || tipo == typeof(double) ||
               tipo == typeof(decimal);
    }

    private static object ConverterNumero(float valor, Type tipo)
    {
        if (tipo == typeof(byte)) return (byte)Mathf.Clamp(Mathf.RoundToInt(valor), 0, 255);
        if (tipo == typeof(sbyte)) return (sbyte)Mathf.Clamp(Mathf.RoundToInt(valor), -128, 127);
        if (tipo == typeof(short)) return (short)Mathf.RoundToInt(valor);
        if (tipo == typeof(ushort)) return (ushort)Mathf.Clamp(Mathf.RoundToInt(valor), 0, 65535);
        if (tipo == typeof(int)) return Mathf.RoundToInt(valor);
        if (tipo == typeof(uint)) return (uint)Mathf.Max(0, Mathf.RoundToInt(valor));
        if (tipo == typeof(long)) return (long)Math.Round(valor);
        if (tipo == typeof(ulong)) return (ulong)Math.Max(0, Math.Round(valor));
        if (tipo == typeof(double)) return (double)valor;
        if (tipo == typeof(decimal)) return (decimal)valor;
        return valor;
    }

    private static void RestaurarCooldownProtectLocal()
    {
        if (restaurarCooldownProtectLocal == null)
            return;

        try
        {
            restaurarCooldownProtectLocal.Invoke();
            restaurarCooldownProtectLocal = null;

            IGameOptions opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes != null)
                AplicarOpcoesNaLogicaLocal(opcoes);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] Falha ao restaurar ProtectCooldown local: " + ex.Message);
        }
    }

    /// <summary>
    /// Antes do encerramento, remove GuardianAngel do cliente dono e publica
    /// ImpostorGhost globalmente para que nenhum cliente termine com o papel falso.
    /// </summary>
    internal static void AntesDeEncerrarPartida()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ativo && transformado)
        {
            PlayerControl jogador = ObterJogadorSelecionado();
            if (jogador != null && jogador.Data != null)
            {
                try
                {
                    jogador.RpcSetRole(RoleTypes.ImpostorGhost, true);
                    RoleSwitchAPI.RpcSetRoleDesync(
                        jogador, RoleTypes.ImpostorGhost, clientIdSelecionado);

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Anjo Traidor] Antes do fim da partida, GuardianAngel foi " +
                        "substituído por ImpostorGhost globalmente e no cliente dono.");
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                        "[F7/Anjo Traidor] Falha ao converter GuardianAngel antes do fim: " + ex);
                }
            }
        }
    }

    internal static void AoIniciarReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !ativo ||
            !transformado ||
            habilidadeEncerrada)
            return;

        habilidadeEncerrada = true;

        PlayerControl jogador = ObterJogadorSelecionado();
        if (jogador != null)
        {
            // Todos já o veem como ImpostorGhost. Reaplicamos a função também
            // no cliente dono, retirando definitivamente o botão de escudo.
            RoleSwitchAPI.RpcSetRoleDesync(
                jogador, RoleTypes.ImpostorGhost, clientIdSelecionado);
        }

        RestaurarNomesLocais();

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Anjo Traidor] Reunião iniciada. O Anjo da Guarda perdeu a habilidade " +
            "e agora é ImpostorGhost também no próprio cliente.");
    }

    private static PlayerControl ObterJogadorSelecionado()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerIdSelecionado)
                return jogador;

        return null;
    }

    private static void CapturarNomesDosImpostores()
    {
        nomesOriginais.Clear();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null)
                continue;

            bool deveFicarVermelho =
                jogador.PlayerId == playerIdSelecionado ||
                (jogador.Data.Role != null && jogador.Data.Role.IsImpostor);

            if (deveFicarVermelho)
                nomesOriginais[jogador.PlayerId] = jogador.Data.PlayerName ?? string.Empty;
        }
    }

    private static void AplicarNomesLocais()
    {
        foreach (KeyValuePair<byte, string> item in nomesOriginais)
        {
            PlayerControl jogador = ObterJogador(item.Key);
            if (jogador == null || jogador.Data == null)
                continue;

            EnviarNomeSomenteAoClienteDono(
                jogador, "<color=#FF3030>" + item.Value + "</color>");
        }
    }

    private static void RestaurarNomesLocais()
    {
        if (nomesOriginais.Count == 0)
            return;

        foreach (KeyValuePair<byte, string> item in nomesOriginais)
        {
            PlayerControl jogador = ObterJogador(item.Key);
            if (jogador == null || jogador.Data == null)
                continue;

            EnviarNomeSomenteAoClienteDono(jogador, item.Value);
        }

        nomesOriginais.Clear();
    }

    private static PlayerControl ObterJogador(byte playerId)
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador.PlayerId == playerId)
                return jogador;

        return null;
    }

    /// <summary>
    /// GameDataTo (mensagem 6) envia SetName apenas ao cliente dono do Phantom.
    /// Se o dono for o host, a alteração é aplicada diretamente no estado local.
    /// </summary>
    private static void EnviarNomeSomenteAoClienteDono(PlayerControl jogador, string nome)
    {
        AmongUsClient cliente = AmongUsClient.Instance;
        if (jogador == null || jogador.Data == null || cliente == null)
            return;

        if (jogador.OwnerId == cliente.ClientId)
        {
            jogador.SetName(nome);
            jogador.Data.PlayerName = nome;

            if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                jogador.cosmetics.nameText.text = nome;

            return;
        }

        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo: somente o cliente dono do Phantom.
            writer.Write(cliente.GameId);
            writer.WritePacked(clientIdSelecionado);

            writer.StartMessage(2); // RPC SetName.
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();

            writer.EndMessage();
            cliente.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] Falha ao alterar nome local de PlayerId=" +
                jogador.PlayerId + ": " + ex.Message);
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
    }

    internal static void EncerrarPartida()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        RestaurarNomesLocais();
        RestaurarCooldownProtectLocal();

        armado = false;
        ativo = false;
        tentandoSelecionar = false;
        transformado = false;
        habilidadeEncerrada = false;
        playerIdSelecionado = byte.MaxValue;
        clientIdSelecionado = -1;
        nomesOriginais.Clear();
    }

    internal static void Resetar()
    {
        EncerrarPartida();
    }
}

/// <summary>
/// Hydra usa LogicRoleSelectionNormal.AssignRolesFromList para editar o pool
/// de funções antes que o jogo atribua os papéis. O patch só substitui uma
/// vaga do pool impostor; nunca escolhe um tripulante.
/// </summary>
[HarmonyPatch(typeof(LogicRoleSelectionNormal), nameof(LogicRoleSelectionNormal.AssignRolesFromList))]
internal static class EventoAnjoTraidorSelecaoFuncoesPatch
{
    private static void Prefix(
        ref Il2CppSystem.Collections.Generic.List<NetworkedPlayerInfo> players,
        ref Il2CppSystem.Collections.Generic.List<RoleTypes> roleList,
        ref int rolesAssigned)
    {
        EventoAnjoTraidor.TentarSubstituirImpostorNaLista(
            ref players, ref roleList, ref rolesAssigned);
    }
}

/// <summary>
/// Confirma o Phantom que o seletor vanilla atribuiu, caso ele já estivesse
/// presente na lista original de funções.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class EventoAnjoTraidorConfirmarSelecaoPatch
{
    private static void Postfix()
    {
        EventoAnjoTraidor.SelecionarPhantom();
    }
}

/// <summary>
/// O EHR corrige o caminho do Phantom no host tratando CmdCheckVanish
/// diretamente e chamando CheckVanish localmente, em vez de depender do
/// fluxo vanilla de envio do comando.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CmdCheckVanish))]
internal static class EventoAnjoTraidorCmdCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance, float maxDuration)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !EventoAnjoTraidor.EhPhantomSelecionado(__instance))
            return true;

        __instance.CheckVanish();
        return false;
    }
}

/// <summary>
/// CheckVanish cobre a ativação local do host.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckVanish))]
internal static class EventoAnjoTraidorCheckVanishPatch
{
    private static bool Prefix(PlayerControl __instance)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !EventoAnjoTraidor.EhPhantomSelecionado(__instance))
            return true;

        EventoAnjoTraidor.AoTentarDesaparecer(__instance);
        return false;
    }
}

/// <summary>
/// O vanilla possui um caminho problemático para Phantom quando o próprio host
/// é o dono. Reproduzimos o acionamento de CmdCheckVanish usado pelo EHR,
/// deixando o Prefix de CheckVanish executar a transformação do evento.
/// </summary>
[HarmonyPatch(typeof(PhantomRole), nameof(PhantomRole.UseAbility))]
internal static class EventoAnjoTraidorPhantomUseAbilityPatch
{
    private static bool Prefix(PhantomRole __instance)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            __instance == null ||
            __instance.Player == null ||
            !EventoAnjoTraidor.EhPhantomSelecionado(__instance.Player))
            return true;

        PlayerControl jogador = __instance.Player;

        // Não deixa o fluxo vanilla do Phantom executar no host: ele pode
        // falhar antes de chamar CheckVanish. Só dispara quando a habilidade
        // está realmente disponível e o jogador pode agir.
        if (!jogador.AmOwner ||
            jogador.Data == null ||
            jogador.Data.IsDead ||
            !jogador.moveable ||
            Minigame.Instance != null ||
            __instance.IsCoolingDown ||
            __instance.fading)
            return false;

        if (__instance.isInvisible)
        {
            __instance.MakePlayerVisible();
            return false;
        }

        float duracao = GameManager.Instance.LogicOptions.GetRoleFloat(
            FloatOptionNames.PhantomDuration);

        jogador.CmdCheckVanish(duracao);
        return false;
    }
}

/// <summary>
/// Clientes remotos enviam RpcCalls.CheckVanish ao host. Interceptamos esse RPC
/// também, pois CheckVanish nem sempre é chamado no host para jogadores remotos.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class EventoAnjoTraidorHandleRpcVanishPatch
{
    private static bool Prefix(
        PlayerControl __instance,
        byte __0,
        MessageReader __1)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            __0 != (byte)RpcCalls.CheckVanish ||
            !EventoAnjoTraidor.EhPhantomSelecionado(__instance))
            return true;

        EventoAnjoTraidor.AoTentarDesaparecer(__instance);
        return false;
    }
}

/// <summary>
/// Substitui o escudo por uma morte. O RPC vanilla CheckProtect chega ao host
/// mesmo quando o Anjo da Guarda está em um cliente sem o mod.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckProtect))]
internal static class EventoAnjoTraidorCheckProtectPatch
{
    private static bool Prefix(
        PlayerControl __instance,
        [HarmonyArgument(0)] PlayerControl target)
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return true;

        if (EventoAnjoTraidor.AoTentarProteger(__instance, target) ||
            EventoAnjoTraidor.DeveBloquearEscudo(__instance))
            return false;

        return true;
    }

}

/// <summary>
/// Ao abrir a primeira reunião depois da conversão, remove GuardianAngel
/// somente do cliente dono e restaura os nomes locais.
/// </summary>
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.StartMeeting))]
internal static class EventoAnjoTraidorStartMeetingPatch
{
    private static void Prefix()
    {
        EventoAnjoTraidor.AoIniciarReuniao();
    }
}

/// <summary>
/// Limpa a alteração local de nomes se a partida terminar sem reunião.
/// </summary>
[HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
internal static class EventoAnjoTraidorEndGamePatch
{
    private static void Prefix()
    {
        // Precisa ocorrer antes do RPC de fim de jogo ser distribuído.
        EventoAnjoTraidor.AntesDeEncerrarPartida();
    }

    private static void Postfix()
    {
        EventoAnjoTraidor.EncerrarPartida();
    }
}

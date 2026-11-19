using System;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
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
    // Quando um corpo é reportado, aguardamos a animação de entrada na reunião
    // antes de encerrar a partida com vitória dos impostores.
    private static bool reporteSolicitado;
    private static bool aguardandoAnimacaoReporte;
    // Vitória dos tripulantes pendente: só será executada depois de WrapUp,
    // quando a reunião e a tela de ejeção já terminaram.
    private static bool vitoriaTripulantesAposReuniaoPendente;
    private static byte playerIdSelecionado = byte.MaxValue;
    private static int clientIdSelecionado = -1;
    private static float proximoDiagnosticoLogicOptions;

    // Clique único inicia a puxada; segundo ProtectPlayer em até 0,5s mata.
    private static float primeiroRpcProtectEm = -1f;
    private static byte alvoPuxadaPlayerId = byte.MaxValue;
    private static bool puxandoAlvo;
    private static ushort sequenciaSnapPuxada;
    private static bool sequenciaSnapPuxadaInicializada;

    // Os nomes são enviados somente ao cliente dono do Phantom escolhido.
    // Assim, nem os demais jogadores nem o GameData do host recebem a cor vermelha.
    private static readonly Dictionary<byte, string> nomesOriginais = new();

    public override string Nome => "Anjo Traidor";

    // Enquanto o Anjo Traidor estiver localmente como GuardianAngel, o host
    // mantém a partida aberta se ele for o último impostor (0 impostores ativos).
    internal static bool DeveManterPartidaAbertaComoAnjo()
    {
        if (!ativo || !transformado ||
            (habilidadeEncerrada && !aguardandoAnimacaoReporte) ||
            playerIdSelecionado == byte.MaxValue || GameData.Instance == null ||
            PlayerControl.AllPlayerControls == null)
            return false;

        PlayerControl anjo = ObterJogador(playerIdSelecionado);
        // O Anjo é morto de propósito na transformação para receber
        // ImpostorGhost globalmente; o cliente dono recebe GuardianAngel.
        // Portanto Data.IsDead=true é esperado e não pode desativar o bloqueio.
        if (anjo == null || anjo.Data == null || anjo.Data.Disconnected)
            return false;

        int impostoresVivos = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;

            if (jogador.PlayerId == playerIdSelecionado)
                continue; // no cliente do Anjo, ele é GuardianAngel

            if (jogador.Data.Role != null && jogador.Data.Role.IsImpostor)
                impostoresVivos++;
        }

        return impostoresVivos == 0;
    }

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
        reporteSolicitado = false;
        aguardandoAnimacaoReporte = false;
        vitoriaTripulantesAposReuniaoPendente = false;
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
        RegistroFuncoesJogadores.RegistrarPersonalizada(escolhido, "Anjo Traidor", RoleTypes.Phantom);
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

    // O sistema de nomes locais usa a identidade personalizada mesmo depois
    // que o Phantom vira GuardianAngel/ImpostorGhost.
    internal static bool EhAnjoTraidor(PlayerControl jogador)
    {
        return ativo && jogador != null &&
            jogador.PlayerId == playerIdSelecionado;
    }

    // Recupera o nome anterior à coloração privada aplicada ao Anjo Traidor.
    // Isso preserva também o texto Rainbow que o jogador já possuía.
    internal static string ObterNomeOriginalParaExibicao(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return string.Empty;

        if (nomesOriginais.TryGetValue(jogador.PlayerId, out string original))
            return original;

        string nome = jogador.Data.PlayerName ?? string.Empty;

        // O cliente dono recebe o nome temporário vermelho via SetName.
        // Remove somente esse invólucro externo, preservando tags internas do Rainbow.
        const string abertura = "<color=#FF3030>";
        const string fechamento = "</color>";
        if (nome.StartsWith(abertura, StringComparison.OrdinalIgnoreCase) &&
            nome.EndsWith(fechamento, StringComparison.OrdinalIgnoreCase) &&
            nome.Length >= abertura.Length + fechamento.Length)
        {
            nome = nome.Substring(
                abertura.Length,
                nome.Length - abertura.Length - fechamento.Length);
        }

        return nome;
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

            // O EHR separa o fluxo do host do fluxo de jogadores remotos:
            // quando o Phantom é o próprio host, executamos MurderPlayer
            // diretamente para que a rotina local crie também o cadáver.
            // Para um Phantom remoto, mantemos o RPC de morte enviado pelo host.
            if (jogador.AmOwner)
            {
                jogador.MurderPlayer(
                    jogador,
                    MurderResultFlags.Succeeded | MurderResultFlags.DecisionByHost);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] Morte local executada para o Phantom host; " +
                    "cadáver criado pela rotina MurderPlayer.");
            }
            else
            {
                jogador.RpcMurderPlayer(jogador, true);

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] RPC de morte enviado para o Phantom remoto.");
            }

            // Não forçamos Data.IsDead manualmente aqui: MurderPlayer/RpcMurderPlayer
            // precisam concluir o processamento normal da morte e criar o cadáver.
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

        bool enviado = false;
        try
        {
            enviado = RoleSwitchAPI.RpcSetRoleDesync(
                jogador, RoleTypes.GuardianAngel, clientIdSelecionado);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao enviar GuardianAngel após a morte: " + ex);
            yield break;
        }

        if (!enviado)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] Não foi possível enviar GuardianAngel ao cliente dono. " +
                "O jogador continuará como ImpostorGhost.");
            yield break;
        }

        // Igual ao evento de congelamento: LogicOptions pode não estar
        // disponível no primeiro instante. Repetimos o envio a cada 0,75s,
        // como EventoVelocidadeJogadores, até a configuração ser enviada.
        bool cooldownEnviado = false;
        const int maxTentativasCooldown = 10;

        for (int tentativa = 1; tentativa <= maxTentativasCooldown; tentativa++)
        {
            try
            {
                if (EnviarConfiguracaoProtectAoAnjo(jogador))
                {
                    cooldownEnviado = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Anjo Traidor] ProtectCooldown sincronizado após " +
                        tentativa + " tentativa(s).");
                    break;
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Tentativa " + tentativa +
                    " de sincronizar ProtectCooldown falhou: " + ex.Message);
            }

            if (tentativa < maxTentativasCooldown)
                yield return new WaitForSecondsRealtime(0.75f);
        }

        if (!cooldownEnviado)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] Não foi possível sincronizar ProtectCooldown com KillCooldown " +
                "após " + maxTentativasCooldown + " tentativas.");
        }

        try
        {
            AplicarNomesLocais();

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] RPC individual concluído após a transformação: " +
                "PlayerId=" + playerIdSelecionado +
                " recebeu GuardianAngel no cliente dono; demais clientes continuam com ImpostorGhost.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao aplicar nomes locais após a transformação: " + ex);
        }
    }

    private static IEnumerator PuxarAlvoAteAnjo(PlayerControl alvo)
    {
        float proximoSnap = 0f;
        while (puxandoAlvo && ativo && transformado && !habilidadeEncerrada &&
            MeetingHud.Instance == null && ShipStatus.Instance != null &&
            alvo != null && alvo.Data != null && !alvo.Data.IsDead &&
            !alvo.Data.Disconnected && alvo.PlayerId == alvoPuxadaPlayerId)
        {
            PlayerControl anjo = ObterJogadorSelecionado();
            // O Anjo Traidor está morto no estado global por design:
            // ele recebeu ImpostorGhost nos outros clientes e GuardianAngel
            // apenas localmente. Não podemos exigir !Data.IsDead aqui,
            // senão a coroutine termina antes do primeiro SnapTo.
            if (anjo == null || anjo.Data == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Puxada encerrada: instância do Anjo indisponível.");
                break;
            }

            float agora = Time.realtimeSinceStartup;
            if (agora >= proximoSnap)
            {
                // Mesmo fluxo autoritativo usado por /tpin e /tpout.
                proximoSnap = agora + 0.22f;
                try
                {
                    Vector2 destino = anjo.GetTruePosition();
                    if (!TeleporteAPI.Teleportar(alvo, destino))
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            "[F7/Anjo Traidor] TeleporteAPI recusou um passo da puxada.");
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Anjo Traidor] Falha ao puxar alvo pela TeleporteAPI: " + ex.Message);
                }
            }
            yield return new WaitForSecondsRealtime(0.1f);
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
        // Detecta o segundo RPC antes de executar qualquer Protect/Murder vanilla.
        // Um RPC isolado apenas inicia a puxada e não aplica cooldown de escudo.
        if (ativo && transformado && !habilidadeEncerrada &&
            anjo != null && anjo.PlayerId == playerIdSelecionado &&
            alvo != null && alvo.Data != null && !alvo.Data.IsDead &&
            !alvo.Data.Disconnected && MeetingHud.Instance == null &&
            ShipStatus.Instance != null)
        {
            float agoraRpc = Time.realtimeSinceStartup;
            bool segundoClique = primeiroRpcProtectEm >= 0f &&
                agoraRpc - primeiroRpcProtectEm <= 0.5f &&
                alvoPuxadaPlayerId == alvo.PlayerId;

            if (!segundoClique)
            {
                primeiroRpcProtectEm = agoraRpc;
                alvoPuxadaPlayerId = alvo.PlayerId;
                puxandoAlvo = true;
                sequenciaSnapPuxadaInicializada = false;
                MonoBehaviourExtensions.StartCoroutine(anjo, PuxarAlvoAteAnjo(alvo));
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] Primeiro RPC Protect: iniciando puxada de PlayerId=" +
                    alvo.PlayerId + ". Segundo RPC em até 0,5s executará a morte.");
                return true;
            }

            primeiroRpcProtectEm = -1f;
            puxandoAlvo = false;
            alvoPuxadaPlayerId = byte.MaxValue;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Segundo RPC Protect dentro de 0,5s: matando PlayerId=" +
                alvo.PlayerId + ".");
        }

        if (!YouTubeVoteBridgePlugin.EhHost() || !ativo || anjo == null)
            return false;

        // O evento só intercepta a habilidade do Anjo Traidor.
        // Outros Anjos da Guarda continuam usando o Protect vanilla normalmente.
        if (anjo.PlayerId != playerIdSelecionado)
            return false;

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

            // 2) Executa a morte com o caminho correto para cada cenário.
            // Se o Anjo for o próprio host, RpcMurderPlayer faz a morte local
            // e publica o RPC vanilla, incluindo a criação/sincronização do corpo.
            // Nesse caso não enviaremos outro MurderPlayer global mais abaixo.
            bool anjoEhHost = anjo.OwnerId == cliente.ClientId;

            if (anjoEhHost)
            {
                anjo.RpcMurderPlayer(alvo, true);
            }
            else
            {
                // Primeiro envia a morte somente ao cliente dono do Anjo.
                MessageWriter murderOwnerWriter = cliente.StartRpcImmediately(
                    anjo.NetId,
                    (byte)RpcCalls.MurderPlayer,
                    SendOption.Reliable,
                    anjo.OwnerId);

                murderOwnerWriter.WriteNetObject(alvo);
                murderOwnerWriter.Write((int)resultado);
                cliente.FinishRpcImmediately(murderOwnerWriter);

                // O host também registra a morte em seu estado autoritativo.
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

            if (!anjoEhHost)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] Primeiro MurderPlayer enviado somente ao dono " +
                    "do Anjo Traidor. Killer=" + anjo.PlayerId + ", alvo=" + alvo.PlayerId + ".");
            }
            else
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] Morte local do host processada por RpcMurderPlayer. " +
                    "Killer=" + anjo.PlayerId + ", alvo=" + alvo.PlayerId + ".");
            }

            // 3) Para Anjo remoto, completa com broadcast global.
            // Quando o host é o Anjo, RpcMurderPlayer já publicou o RPC global.
            if (!anjoEhHost)
            {
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
            else
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] RpcMurderPlayer executado pelo host; morte local " +
                    "e sincronização global feitas pelo método vanilla. Impostor=" +
                    anjo.PlayerId + ", vítima=" + alvo.PlayerId + ".");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha no fluxo Protect + assassinato: " + ex);
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
                // O host é o próprio Anjo Traidor: mantém a opção aplicada
                // durante a partida, sem restaurá-la ao cooldown vanilla.
                if (!TentarDefinirProtectCooldown(opcoesAtuais, killCooldown, out _))
                    return false;

                AplicarOpcoesNaLogicaLocal(opcoesAtuais);

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
            if (!TentarDefinirProtectCooldown(opcoesAtuais, killCooldown, out restaurarTemporariamente))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] CurrentGameOptions não disponibilizou IGameOptions para alterar GuardianAngelCooldown.");
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

        if (!encontrouLogica && Time.realtimeSinceStartup >= proximoDiagnosticoLogicOptions)
        {
            proximoDiagnosticoLogicOptions = Time.realtimeSinceStartup + 5f;

            string referenciaDireta = "não consultada";
            string indiceReferenciaDireta = "não encontrado";
            string componentes = "indisponível";
            int quantidade = -1;

            try
            {
                GameManager gerenciador = GameManager.Instance;
                var lista = gerenciador?.LogicComponents;
                LogicOptions logicaDireta = gerenciador?.LogicOptions;

                if (logicaDireta == null)
                {
                    referenciaDireta = "null";
                }
                else
                {
                    referenciaDireta =
                        logicaDireta.GetType().FullName +
                        "; Assembly=" + logicaDireta.GetType().Assembly.GetName().Name;
                }

                if (lista != null)
                {
                    quantidade = lista.Count;
                    List<string> nomes = new();

                    for (int i = 0; i < lista.Count; i++)
                    {
                        GameLogicComponent componente = lista[i];
                        if (componente == null)
                        {
                            nomes.Add(i + ":null");
                            continue;
                        }

                        bool mesmaReferencia = logicaDireta != null &&
                            ReferenceEquals(componente, logicaDireta);

                        nomes.Add(i + ":" + componente.GetType().FullName +
                            "; Assembly=" + componente.GetType().Assembly.GetName().Name +
                            "; LogicOptionsDireto=" + mesmaReferencia);

                        if (mesmaReferencia)
                            indiceReferenciaDireta = i.ToString();
                    }

                    componentes = nomes.Count == 0
                        ? "(lista vazia)"
                        : string.Join(" || ", nomes);
                }
                else
                {
                    componentes = "LogicComponents=null";
                }
            }
            catch (Exception ex)
            {
                componentes = "erro ao inspecionar: " + ex;
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor][DIAGNOSTICO] Não foi encontrado LogicOptions na lista. " +
                "PlayerId=" + anjo.PlayerId +
                ", OwnerId=" + anjo.OwnerId +
                ", GameManagerNetId=" + GameManager.Instance?.NetId +
                ", quantidadeLogicComponents=" + quantidade +
                ", LogicOptionsDireto=[" + referenciaDireta + "]" +
                ", indiceLogicOptionsDireto=" + indiceReferenciaDireta +
                ", componentes=[" + componentes + "].");
        }

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
    /// Altera diretamente a opção vanilla GuardianAngelCooldown.
    /// Para o cliente remoto, a ação de restauração é usada somente depois
    /// da serialização do pacote individual; para o host, o valor permanece.
    /// </summary>
    private static bool TentarDefinirProtectCooldown(
        IGameOptions configuracoes,
        float valor,
        out Action restaurar)
    {
        restaurar = null;
        if (configuracoes == null)
            return false;

        // EHR confirma que GuardianAngelCooldown é uma opção FloatOptionNames
        // comum em IGameOptions, acessada com GetFloat/SetFloat.
        float anterior = configuracoes.GetFloat(FloatOptionNames.GuardianAngelCooldown);
        configuracoes.SetFloat(FloatOptionNames.GuardianAngelCooldown, valor);
        restaurar = () => configuracoes.SetFloat(
            FloatOptionNames.GuardianAngelCooldown, anterior);
        return true;
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

    // Chamado antes de o jogo processar um reporte. Não encerra a habilidade
    // aqui: o fluxo normal de StartMeeting fará a conversão definitiva.
    internal static void AoReportarCorpo()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !ativo || !transformado)
            return;

        reporteSolicitado = true;
        RestaurarNomesLocais();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Anjo Traidor] Reporte detectado. Nomes restaurados; a partida aguardará " +
            "a animação da reunião antes de encerrar.");
    }

    internal static void AoIniciarReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !ativo ||
            !transformado ||
            habilidadeEncerrada)
            return;

        // Captura a condição ANTES de remover o GuardianAngel: se o Anjo era o
        // último impostor, a vitória dos tripulantes será agendada após WrapUp.
        vitoriaTripulantesAposReuniaoPendente =
            YouTubeVoteBridgePlugin.SemFimDeJogo != null &&
            !YouTubeVoteBridgePlugin.SemFimDeJogo.Value &&
            DeveManterPartidaAbertaComoAnjo();

        habilidadeEncerrada = true;
        puxandoAlvo = false;
        primeiroRpcProtectEm = -1f;
        alvoPuxadaPlayerId = byte.MaxValue;

        bool encerrarDepoisDaAnimacao = reporteSolicitado;
        if (encerrarDepoisDaAnimacao && !aguardandoAnimacaoReporte)
        {
            aguardandoAnimacaoReporte = true;
            MonoBehaviourExtensions.StartCoroutine(
                PlayerControl.LocalPlayer,
                EncerrarPartidaAposAnimacaoReporte());
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Reunião de reporte iniciada. Aguardando 3 segundos " +
                "para os clientes assistirem à animação antes do fim da partida.");
        }

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

    // WrapUp é o ponto em que a reunião termina e a transição de ejeção já foi
    // concluída. A espera curta permite que o jogo retorne ao estado de partida.
    internal static void AoTerminarReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !vitoriaTripulantesAposReuniaoPendente)
            return;

        MonoBehaviourExtensions.StartCoroutine(
            PlayerControl.LocalPlayer,
            EncerrarComVitoriaTripulantesAposReuniao());
    }


    private static IEnumerator EncerrarComVitoriaTripulantesAposReuniao()
    {
        yield return new WaitForSecondsRealtime(0.75f);


        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !vitoriaTripulantesAposReuniaoPendente ||
            (YouTubeVoteBridgePlugin.SemFimDeJogo != null &&
             YouTubeVoteBridgePlugin.SemFimDeJogo.Value) ||
            GameManager.Instance == null)
            yield break;


        vitoriaTripulantesAposReuniaoPendente = false;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Anjo Traidor] Reunião e ejeção concluídas. Encerrando com vitória dos tripulantes.");
        GameManager.Instance.RpcEndGame((GameOverReason)0, false);
    }


    private static IEnumerator EncerrarPartidaAposAnimacaoReporte()
    {
        // A transição de reporte precisa ser visível nos outros clientes antes
        // de a tela de vitória substituir a reunião.
        yield return new WaitForSecondsRealtime(3f);

        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !ativo ||
            !transformado ||
            !aguardandoAnimacaoReporte)
            yield break;

        GameManager manager = GameManager.Instance;
        if (manager == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Anjo Traidor] GameManager indisponível após a animação do reporte; " +
                "não foi possível encerrar a partida.");
            yield break;
        }

        aguardandoAnimacaoReporte = false;
        reporteSolicitado = false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Anjo Traidor] Animação do reporte concluída. Encerrando a partida " +
            "com vitória dos impostores.");
        // GameOverReason 3 é ImpostorsByKill, o motivo de vitória impostora
        // também reconhecido pelo fluxo de preparação do Oportunista.
        manager.RpcEndGame((GameOverReason)3, false);
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
            {
                YouTubeVoteBridgePlugin.PausarRainbowDuranteTrocaDeNome(
                    jogador, "Anjo Traidor");
                nomesOriginais[jogador.PlayerId] = jogador.Data.PlayerName ?? string.Empty;
            }
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
            YouTubeVoteBridgePlugin.RetomarRainbowDepoisDaTrocaDeNome(
                jogador, "Anjo Traidor");
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
            // Host: altera somente o TMP local. SetName/Data.PlayerName são
            // globais e fariam o nome vermelho vazar para os demais clientes.
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
            writer.Write(false);
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

        puxandoAlvo = false;
        primeiroRpcProtectEm = -1f;
        alvoPuxadaPlayerId = byte.MaxValue;
        armado = false;
        ativo = false;
        tentandoSelecionar = false;
        transformado = false;
        habilidadeEncerrada = false;
        reporteSolicitado = false;
        aguardandoAnimacaoReporte = false;
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
    private static bool Prefix(GameOverReason __0)
    {
        // Quando o Anjo é o último impostor, o cliente dono o enxerga como
        // GuardianAngel. O vanilla pode interpretar isso como vitória dos
        // tripulantes. Bloqueamos somente os motivos de vitória da tripulação;
        // vitórias legítimas dos impostores continuam encerrando normalmente.
        // O código 0 é o encerramento por ausência de impostores ativos.
        // Vitória por tarefas concluídas continua sendo uma condição legítima.
        bool vitoriaPorAusenciaDeImpostores = (int)__0 == 0;

        if (vitoriaPorAusenciaDeImpostores && EventoAnjoTraidor.DeveManterPartidaAbertaComoAnjo())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] RpcEndGame bloqueado: o Anjo ainda está vivo e é o último impostor. " +
                "A partida continuará até a reunião encerrar a habilidade.");
            return false;
        }

        // Precisa ocorrer antes do RPC de fim de jogo ser distribuído.
        EventoAnjoTraidor.AntesDeEncerrarPartida();
        return true;
    }

    private static void Postfix()
    {
        EventoAnjoTraidor.EncerrarPartida();
    }
}

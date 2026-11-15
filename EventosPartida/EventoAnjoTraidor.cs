using System;
using System.Collections;
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
    private static byte playerIdSelecionado = byte.MaxValue;
    private static int clientIdSelecionado = -1;

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
    /// O host aprova exclusivamente o Protect do impostor selecionado.
    /// O efeito vanilla é cancelado e o alvo recebe uma morte real.
    /// </summary>
    internal static bool AoTentarProteger(PlayerControl anjo, PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !ativo || anjo == null)
            return false;

        // Durante o evento, somente o jogador impostor escolhido pode usar
        // Protect. Bloqueia qualquer outro Guardian Angel que tente acioná-lo.
        if (anjo.PlayerId != playerIdSelecionado)
            return true;

        // Se a habilidade já foi encerrada, ainda bloqueia qualquer Protect
        // residual que chegue atrasado ao host.
        if (!transformado || habilidadeEncerrada ||
            MeetingHud.Instance != null ||
            ShipStatus.Instance == null)
            return true;

        if (alvo == null ||
            alvo.Data == null ||
            alvo.Data.IsDead ||
            alvo.Data.Disconnected ||
            alvo.PlayerId == playerIdSelecionado)
        {
            // A tentativa inválida também consome o uso; não deixa o vanilla
            // aplicar um escudo como efeito colateral.
            return true;
        }

        try
        {
            alvo.RpcMurderPlayer(alvo, true);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Protect autorizado para o impostor selecionado; " +
                "o alvo foi morto. Alvo=" + alvo.Data.PlayerName +
                " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao matar o alvo do Protect: " + ex);
        }

        // Nunca permite que Protect aplique o escudo vanilla.
        return true;
    }

    /// <summary>
    /// O GuardianAngel só existe no cliente dono. Nesse cliente, iniciamos
    /// o cooldown visual/funcional do botão usando a mesma duração do Kill.
    /// </summary>
    internal static void AplicarCooldownProtectLocal(PlayerControl anjo)
    {
        try
        {
            AmongUsClient cliente = AmongUsClient.Instance;
            if (anjo == null ||
                cliente == null ||
                anjo.OwnerId != cliente.ClientId ||
                anjo.Data == null ||
                anjo.Data.RoleType != RoleTypes.GuardianAngel ||
                HudManager.Instance == null ||
                HudManager.Instance.AbilityButton == null ||
                GameManager.Instance == null ||
                GameManager.Instance.LogicOptions == null)
                return;

            float cooldownKill = GameManager.Instance.LogicOptions.GetKillCooldown();
            object botao = HudManager.Instance.AbilityButton;
            var metodo = AccessTools.Method(
                botao.GetType(),
                "SetCoolDown",
                new[] { typeof(float), typeof(float) });

            if (metodo == null)
                metodo = AccessTools.Method(
                    botao.GetType(),
                    "SetCooldown",
                    new[] { typeof(float), typeof(float) });

            if (metodo == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Não foi encontrado SetCoolDown no AbilityButton.");
                return;
            }

            metodo.Invoke(botao, new object[] { cooldownKill, cooldownKill });

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Cooldown do Protect aplicado localmente: " +
                cooldownKill + "s (igual ao cooldown de Kill).");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao aplicar cooldown do Protect: " + ex);
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

    private static void Postfix(PlayerControl __instance)
    {
        // Aplicamos depois do processamento vanilla para que o cooldown
        // padrão do GuardianAngel não sobrescreva o cooldown de Kill.
        EventoAnjoTraidor.AplicarCooldownProtectLocal(__instance);
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

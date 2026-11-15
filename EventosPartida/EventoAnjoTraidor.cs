using System;
using System.Collections.Generic;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using InnerNet;

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
    /// Executado depois da seleção vanilla. Se já existir um Phantom impostor,
    /// ele é escolhido; caso contrário, um impostor recebe Phantom antes da Role Reveal.
    /// </summary>
    internal static void SelecionarPhantom()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !armado || tentandoSelecionar)
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

            if (escolhido != null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Anjo Traidor] Phantom vanilla encontrado: " +
                    escolhido.Data.PlayerName + " (PlayerId=" + escolhido.PlayerId + ").");
            }
            else
            {
                List<PlayerControl> impostores = new();

                foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
                {
                    if (!JogadorValido(jogador) ||
                        jogador.Data.Role == null ||
                        !jogador.Data.Role.IsImpostor)
                        continue;

                    impostores.Add(jogador);
                }

                if (impostores.Count > 0)
                {
                    escolhido = impostores[System.Random.Shared.Next(impostores.Count)];

                    // SelectRoles já terminou de montar as atribuições vanilla,
                    // mas a Role Reveal ainda não começou. Substituímos a função
                    // escolhida agora, sem fazer uma troca durante a partida.
                    escolhido.RpcSetRole(RoleTypes.Phantom, true);

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Anjo Traidor] Nenhum Phantom foi sorteado. " +
                        "Atribuindo Phantom ao impostor " + escolhido.Data.PlayerName +
                        " (PlayerId=" + escolhido.PlayerId + ") antes da Role Reveal.");
                }
            }

            if (escolhido == null)
            {
                armado = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Não foi encontrado nenhum impostor para receber Phantom. Evento cancelado.");
                return;
            }

            playerIdSelecionado = escolhido.PlayerId;
            clientIdSelecionado = escolhido.OwnerId;
            ativo = true;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] Jogador escolhido: " + escolhido.Data.PlayerName +
                " (PlayerId=" + playerIdSelecionado +
                ", ClientId=" + clientIdSelecionado + ").");
        }
        catch (Exception ex)
        {
            armado = false;
            ativo = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao escolher o Phantom: " + ex);
        }
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

            // A morte é real e é executada pelo host. Não usamos Exiled:
            // o jogador precisa continuar como um fantasma funcional.
            jogador.RpcMurderPlayer(jogador, true);

            // Primeiro, todos recebem o fantasma impostor vanilla.
            jogador.RpcSetRole(RoleTypes.ImpostorGhost, true);

            // Somente o dono do jogador recebe GuardianAngel.
            // RpcSetRoleDesync já trata tanto o caso de o dono ser o host
            // quanto o caso de ser outro cliente, inclusive vanilla.
            if (!RoleSwitchAPI.RpcSetRoleDesync(
                    jogador, RoleTypes.GuardianAngel, clientIdSelecionado))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F7/Anjo Traidor] Não foi possível enviar GuardianAngel ao cliente dono. " +
                    "O jogador continuará como ImpostorGhost.");
            }

            AplicarNomesLocais();

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] PlayerId=" + playerIdSelecionado +
                " morreu ao tentar Vanish. Papel global=ImpostorGhost; " +
                "papel privado do dono=GuardianAngel.");
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao converter o Phantom em Anjo da Guarda: " + ex);
            return true;
        }
    }

    /// <summary>
    /// O host recebe CheckProtect mesmo quando o dono do jogador é vanilla.
    /// Em vez de proteger, o alvo selecionado recebe uma ordem de suicídio.
    /// </summary>
    internal static bool DeveBloquearEscudo(PlayerControl anjo)
    {
        return ativo &&
            habilidadeEncerrada &&
            anjo != null &&
            anjo.PlayerId == playerIdSelecionado;
    }

    internal static bool AoTentarProteger(PlayerControl anjo, PlayerControl alvo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            !ativo ||
            !transformado ||
            habilidadeEncerrada ||
            anjo == null ||
            anjo.PlayerId != playerIdSelecionado ||
            alvo == null ||
            alvo.Data == null ||
            alvo.Data.IsDead ||
            alvo.Data.Disconnected ||
            MeetingHud.Instance != null ||
            ShipStatus.Instance == null)
            return false;

        try
        {
            alvo.RpcMurderPlayer(alvo, true);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Anjo Traidor] O escudo de PlayerId=" + playerIdSelecionado +
                " foi convertido em morte. Alvo=" + alvo.Data.PlayerName +
                " (PlayerId=" + alvo.PlayerId + ").");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Anjo Traidor] Falha ao matar o alvo do escudo: " + ex);
        }

        // Sempre cancela o escudo vanilla: ele nunca deve proteger o alvo.
        return true;
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
/// A seleção vanilla já foi concluída neste ponto, mas a Role Reveal ainda
/// não começou. O evento apenas substitui uma atribuição de impostor por Phantom.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class EventoAnjoTraidorSelecaoFuncoesPatch
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
    private static void Postfix()
    {
        EventoAnjoTraidor.EncerrarPartida();
    }
}

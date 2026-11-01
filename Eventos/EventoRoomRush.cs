using System;
using System.Collections.Generic;
using System.Linq;
using Hazel;
using InnerNet;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoRoomRush : EventoVotacao
{
    private const float TEMPO_POR_RODADA = 15f;
    private const float INTERVALO_SINCRONIZACAO = 0.5f;

    private static bool ativo;
    private static float prazo;
    private static float proximaSincronizacao;
    private static SystemTypes salaAlvo;
    private static SystemTypes salaAlvoLocal;
    private static float prazoLocal;
    private static bool ativoLocal;
    private static bool reparoReatorPendente;
    private static float repararReatorAte;
    private static int ciclosReatorRestantes;
    private static bool sequenciaReatorAtiva;
    private static bool aguardandoProximoPulsoReator;
    private static SystemTypes sistemaCritico = SystemTypes.Reactor;
    // Aviso de sala aplicado somente ao nome visto pelo próprio cliente vanilla.
    private static readonly Dictionary<byte, string> nomesOriginaisVanilla = new();
    private static readonly HashSet<byte> nomesAlteradosVanilla = new();
    private static float proximaAtualizacaoAvisoNomes;

    public override string Nome => "Vá para a sala ou morra";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ShipStatus.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] O evento só pode ser iniciado durante uma partida.");
            return;
        }

        if (!EscolherSala())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Não foi possível encontrar salas válidas no mapa.");
            Resetar();
            return;
        }

        ativo = true;
        IniciarAvisoReator();
        AplicarEscudoERomperParaTodos();
        prazo = Time.realtimeSinceStartup + TEMPO_POR_RODADA;
        proximaSincronizacao = 0f;
        ReceberEstadoLocal(salaAlvo, TEMPO_POR_RODADA, true);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Iniciado. Os jogadores devem chegar à sala indicada em 15 segundos.");
        SincronizarEstado();
    }

    internal static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ShipStatus.Instance == null || AmongUsClient.Instance == null)
        {
            Resetar();
            return;
        }

        AtualizarReparoReator();

        if (MeetingHud.Instance != null || ExileController.Instance != null)
            return;

        AtualizarAvisoNomesVanilla();

        float agora = Time.realtimeSinceStartup;
        if (agora >= proximaSincronizacao)
            SincronizarEstado();

        // Se todos os jogadores vivos já chegaram, encerra a rodada imediatamente
        // sem esperar o cronômetro e sem eliminar ninguém.
        if (TodosJogadoresVivosNaSala())
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F6/Room Rush] Todos os jogadores vivos chegaram à sala " +
                NomeSala(salaAlvo) + ". Rodada concluída antecipadamente.");
            Resetar();
            return;
        }

        if (agora < prazo)
            return;

        int mortos = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer)
                continue;

            try
            {
                PlainShipRoom salaJogador = ObterSalaJogador(jogador.GetTruePosition());
                if (salaJogador != null && salaJogador.RoomId == salaAlvo)
                    continue;

                jogador.RpcMurderPlayer(jogador, true);
                mortos++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Falha ao eliminar PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Rodada encerrada! " + mortos +
            " jogador(es) eliminado(s) por não chegar à sala.");
        Resetar();
    }

    private static bool TodosJogadoresVivosNaSala()
    {
        int jogadoresVivos = 0;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer)
                continue;

            jogadoresVivos++;

            try
            {
                PlainShipRoom salaJogador = ObterSalaJogador(jogador.GetTruePosition());
                if (salaJogador == null || salaJogador.RoomId != salaAlvo)
                    return false;
            }
            catch
            {
                return false;
            }
        }

        // Não encerra a rodada caso não exista nenhum jogador vivo.
        return jogadoresVivos > 0;
    }

    // Identifica o sistema crítico real do mapa. Airship usa HeliSabotage;
    // Polus usa Laboratory; os mapas clássicos usam Reactor.
    private static bool TentarObterSistemaCritico(ShipStatus navio, out SystemTypes sistema)
    {
        sistema = SystemTypes.Reactor;
        if (navio == null || navio.Systems == null)
            return false;

        if (navio.Systems.ContainsKey(SystemTypes.HeliSabotage))
            sistema = SystemTypes.HeliSabotage;
        else if (navio.Systems.ContainsKey(SystemTypes.Laboratory))
            sistema = SystemTypes.Laboratory;
        else if (navio.Systems.ContainsKey(SystemTypes.Reactor))
            sistema = SystemTypes.Reactor;
        else
            return false;

        return true;
    }

    private static void IniciarAvisoReator()
    {
        reparoReatorPendente = false;
        repararReatorAte = 0f;
        ciclosReatorRestantes = 0;
        sequenciaReatorAtiva = false;
        aguardandoProximoPulsoReator = false;

        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (navio == null ||
                !navio.Systems.ContainsKey(SystemTypes.Sabotage) ||
                !TentarObterSistemaCritico(navio, out sistemaCritico))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Sistemas de sabotagem crítica não encontrados no mapa.");
                return;
            }

            SabotageSystemType sabotagem = navio.Systems[SystemTypes.Sabotage]
                .Cast<SabotageSystemType>();
            if (sabotagem == null)
                return;

            // O sistema global informa se já existe uma sabotagem em andamento.
            // Não interrompemos uma sabotagem iniciada por outro evento/jogador.
            if (sabotagem.AnyActive)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F6/Room Rush] Já existe uma sabotagem ativa; sequência do reator ignorada.");
                return;
            }

            ciclosReatorRestantes = 3;
            sequenciaReatorAtiva = true;
            AcionarProximoCicloReator();
        }
        catch (Exception ex)
        {
            sequenciaReatorAtiva = false;
            ciclosReatorRestantes = 0;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao iniciar sequência do reator: " + ex.Message);
        }
    }

    private static void AcionarProximoCicloReator()
    {
        if (!sequenciaReatorAtiva || ciclosReatorRestantes <= 0 ||
            ShipStatus.Instance == null)
            return;

        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (!navio.Systems.ContainsKey(SystemTypes.Sabotage) ||
                !navio.Systems.ContainsKey(sistemaCritico))
            {
                EncerrarSequenciaReator();
                return;
            }

            SabotageSystemType sabotagem = navio.Systems[SystemTypes.Sabotage]
                .Cast<SabotageSystemType>();
            if (sabotagem == null)
            {
                EncerrarSequenciaReator();
                return;
            }

            sabotagem.Timer = 0f;
            sabotagem.IsDirty = true;
            navio.RpcUpdateSystem(SystemTypes.Sabotage, (byte)sistemaCritico);

            reparoReatorPendente = true;
            repararReatorAte = Time.realtimeSinceStartup + 0.1f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F6/Room Rush] Pulso do reator enviado; reparo em 0,1s. Restantes: " +
                ciclosReatorRestantes);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao acionar o reator: " + ex.Message);
            EncerrarSequenciaReator();
        }
    }

    private static void AtualizarReparoReator()
    {
        if (!sequenciaReatorAtiva || !reparoReatorPendente ||
            Time.realtimeSinceStartup < repararReatorAte)
            return;

        // Mantém 0,1s entre cada ação: depois do reparo, espera antes do próximo reator.
        if (aguardandoProximoPulsoReator)
        {
            aguardandoProximoPulsoReator = false;
            reparoReatorPendente = false;
            repararReatorAte = 0f;
            AcionarProximoCicloReator();
            return;
        }

        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (navio != null && navio.Systems.ContainsKey(sistemaCritico))
            {
                // Reactor e Laboratory usam os sinais 16/17. No Airship,
                // HeliSabotage interpreta esses valores como conclusão dos
                // dois painéis do Avert Crash Course.
                navio.RpcUpdateSystem(sistemaCritico, 16);
                navio.RpcUpdateSystem(sistemaCritico, 17);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F6/Room Rush] Reparo de " + sistemaCritico + " enviado.");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao reparar o pulso do reator: " + ex.Message);
            EncerrarSequenciaReator();
            return;
        }

        reparoReatorPendente = false;
        repararReatorAte = 0f;
        ciclosReatorRestantes--;

        if (ciclosReatorRestantes > 0)
        {
            aguardandoProximoPulsoReator = true;
            reparoReatorPendente = true;
            repararReatorAte = Time.realtimeSinceStartup + 0.1f;
        }
        else
        {
            EncerrarSequenciaReator();
        }
    }

    private static void EncerrarSequenciaReator()
    {
        sequenciaReatorAtiva = false;
        reparoReatorPendente = false;
        repararReatorAte = 0f;
        ciclosReatorRestantes = 0;
        aguardandoProximoPulsoReator = false;
    }

    private static void AplicarEscudoERomperParaTodos()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null)
            return;

        const int corEscudo = 0;
        int aplicados = 0;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.IsDead || jogador.Data.Disconnected ||
                jogador.notRealPlayer || jogador.OwnerId < 0)
                continue;

            try
            {
                if (jogador.AmOwner)
                {
                    jogador.ProtectPlayer(jogador, corEscudo);
                }
                else
                {
                    MessageWriter escudo = AmongUsClient.Instance.StartRpcImmediately(
                        jogador.NetId,
                        (byte)RpcCalls.ProtectPlayer,
                        SendOption.Reliable,
                        jogador.OwnerId
                    );
                    escudo.WriteNetObject(jogador);
                    escudo.Write(corEscudo);
                    AmongUsClient.Instance.FinishRpcImmediately(escudo);
                }

                // A tentativa de morte é enviada apenas ao cliente dono do jogador.
                // FailedProtected reproduz o rompimento do escudo sem matar.
                MessageWriter quebraEscudo = AmongUsClient.Instance.StartRpcImmediately(
                    jogador.NetId,
                    (byte)RpcCalls.MurderPlayer,
                    SendOption.Reliable,
                    jogador.OwnerId
                );
                quebraEscudo.WriteNetObject(jogador);
                quebraEscudo.Write((int)MurderResultFlags.FailedProtected);
                AmongUsClient.Instance.FinishRpcImmediately(quebraEscudo);
                aplicados++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Falha ao reproduzir escudo para PlayerId=" +
                    jogador.PlayerId + ": " + ex.Message);
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F6/Room Rush] Efeito de escudo e rompimento enviado para " +
            aplicados + " jogador(es).");
    }

    // Envia o aviso somente ao dono de cada jogador sem o mod.
    // O GameDataTo (tipo 6) impede que o nome temporário seja replicado para a sala inteira.
    private static bool DefinirNomeLocalParaCliente(PlayerControl jogador, string nome)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            jogador == null || jogador.Data == null || jogador.OwnerId < 0 ||
            jogador.OwnerId == AmongUsClient.Instance.ClientId)
            return false;

        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo: somente o cliente proprietário
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(jogador.OwnerId);

            writer.StartMessage(2);
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F6/Room Rush] Falha ao definir aviso no nome de PlayerId=" +
                jogador.PlayerId + ": " + ex.Message);
            return false;
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
    }

    private static void AtualizarAvisoNomesVanilla()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            Time.realtimeSinceStartup < proximaAtualizacaoAvisoNomes)
            return;

        proximaAtualizacaoAvisoNomes = Time.realtimeSinceStartup + 0.5f;
        int segundos = Mathf.Max(0, Mathf.CeilToInt(prazo - Time.realtimeSinceStartup));
        string cor = segundos <= 5 ? "red" : "yellow";

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected ||
                jogador.OwnerId < 0 ||
                jogador.OwnerId == AmongUsClient.Instance.ClientId ||
                YouTubeVoteBridgePlugin.ClienteTemMod(jogador.OwnerId))
                continue;

            if (!nomesOriginaisVanilla.TryGetValue(jogador.PlayerId, out string nomeOriginal))
            {
                nomeOriginal = jogador.Data.PlayerName ?? "";
                nomesOriginaisVanilla[jogador.PlayerId] = nomeOriginal;
            }

            string nomeAviso = nomeOriginal +
                "\n<color=" + cor + ">Vá para: " + NomeSala(salaAlvo) +
                " (" + segundos + "s)</color>";

            if (DefinirNomeLocalParaCliente(jogador, nomeAviso))
                nomesAlteradosVanilla.Add(jogador.PlayerId);
        }
    }

    private static void RestaurarAvisosNomesVanilla()
    {
        if (YouTubeVoteBridgePlugin.EhHost() && AmongUsClient.Instance != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null ||
                    !nomesOriginaisVanilla.TryGetValue(jogador.PlayerId, out string nomeOriginal))
                    continue;

                if (jogador.OwnerId >= 0 &&
                    jogador.OwnerId != AmongUsClient.Instance.ClientId &&
                    nomesAlteradosVanilla.Contains(jogador.PlayerId))
                    DefinirNomeLocalParaCliente(jogador, nomeOriginal);
            }
        }

        nomesOriginaisVanilla.Clear();
        nomesAlteradosVanilla.Clear();
        proximaAtualizacaoAvisoNomes = 0f;
    }

    private static void SincronizarEstado()
    {
        float restante = Mathf.Max(0f, prazo - Time.realtimeSinceStartup);
        YouTubeVoteBridgePlugin.SincronizarRoomRush((byte)salaAlvo, restante, true);
        ReceberEstadoLocal(salaAlvo, restante, true);
        proximaSincronizacao = Time.realtimeSinceStartup + INTERVALO_SINCRONIZACAO;
    }

    internal static void ReceberEstadoLocal(SystemTypes sala, float segundosRestantes, bool eventoAtivo)
    {
        ativoLocal = eventoAtivo;
        if (!eventoAtivo)
        {
            prazoLocal = 0f;
            return;
        }

        salaAlvoLocal = sala;
        prazoLocal = Time.realtimeSinceStartup + Mathf.Max(0f, segundosRestantes);
    }

    internal static string ObterTextoHud()
    {
        if (!ativoLocal || ShipStatus.Instance == null ||
            MeetingHud.Instance != null || ExileController.Instance != null)
            return "";

        int segundos = Mathf.Max(0, Mathf.CeilToInt(prazoLocal - Time.realtimeSinceStartup));
        PlainShipRoom salaJogador = PlayerControl.LocalPlayer == null
            ? null
            : ObterSalaJogador(PlayerControl.LocalPlayer.GetTruePosition());
        bool estaNaSala = salaJogador != null && salaJogador.RoomId == salaAlvoLocal;

        string cor = estaNaSala ? "green" : segundos <= 5 ? "red" : "yellow";
        return "<color=" + cor + ">Sala: " + NomeSala(salaAlvoLocal) +
            " (" + segundos + "S)</color>";
    }

    private static PlainShipRoom ObterSalaJogador(Vector2 posicao)
    {
        ShipStatus navio = ShipStatus.Instance;
        if (navio == null || navio.AllRooms == null)
            return null;

        PlainShipRoom melhorSala = null;
        float menorArea = float.MaxValue;
        foreach (PlainShipRoom sala in navio.AllRooms)
        {
            if (sala == null || sala.roomArea == null ||
                sala.RoomId == SystemTypes.Hallway || sala.RoomId == SystemTypes.Outside)
                continue;

            try
            {
                if (!sala.roomArea.OverlapPoint(posicao))
                    continue;

                Vector2 tamanho = sala.roomArea.bounds.size;
                float area = tamanho.x * tamanho.y;
                if (area < menorArea)
                {
                    menorArea = area;
                    melhorSala = sala;
                }
            }
            catch { }
        }

        return melhorSala;
    }

    private static bool EscolherSala()
    {
        if (ShipStatus.Instance == null || ShipStatus.Instance.AllRooms == null)
            return false;

        List<SystemTypes> salas = new();
        foreach (PlainShipRoom sala in ShipStatus.Instance.AllRooms)
        {
            if (sala == null || sala.roomArea == null ||
                sala.RoomId.ToString() == "None" ||
                sala.RoomId == SystemTypes.Hallway ||
                sala.RoomId == SystemTypes.Outside)
                continue;

            if (!salas.Contains(sala.RoomId))
                salas.Add(sala.RoomId);
        }

        if (salas.Count == 0)
            return false;

        salaAlvo = salas[System.Random.Shared.Next(salas.Count)];
        return true;
    }

    private static string NomeSala(SystemTypes sala)
    {
        try
        {
            string traduzido = TranslationController.Instance.GetString(sala);
            if (!string.IsNullOrWhiteSpace(traduzido))
                return traduzido;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[F6/Room Rush] Falha ao traduzir nome da sala: " + ex.Message);
        }

        return sala.ToString();
    }

    internal static void Resetar()
    {
        RestaurarAvisosNomesVanilla();

        if (ativo && YouTubeVoteBridgePlugin.EhHost())
            YouTubeVoteBridgePlugin.SincronizarRoomRush(0, 0f, false);

        // Se a rodada terminar durante os 0,1s de espera, não deixa a
        // sabotagem crítica recém-acionada ativa no mapa.
        if (sequenciaReatorAtiva && reparoReatorPendente &&
            ShipStatus.Instance != null &&
            ShipStatus.Instance.Systems.ContainsKey(sistemaCritico))
        {
            try
            {
                ShipStatus.Instance.RpcUpdateSystem(sistemaCritico, 16);
                ShipStatus.Instance.RpcUpdateSystem(sistemaCritico, 17);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F6/Room Rush] Sistema crítico " + sistemaCritico +
                    " reparado durante a limpeza do evento.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[F6/Room Rush] Falha ao reparar " + sistemaCritico +
                    " durante a limpeza: " + ex.Message);
            }
        }

        ativo = false;
        EncerrarSequenciaReator();
        prazo = 0f;
        proximaSincronizacao = 0f;
        ReceberEstadoLocal(salaAlvoLocal, 0f, false);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.Update))]
internal static class EventoRoomRushAtualizarPatch
{
    private static void Postfix()
    {
        EventoRoomRush.Atualizar();
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnGameEnd))]
internal static class EventoRoomRushFimPatch
{
    private static void Postfix()
    {
        EventoRoomRush.Resetar();
    }
}

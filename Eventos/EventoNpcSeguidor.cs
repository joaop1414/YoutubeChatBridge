using System;
using System.Collections.Generic;
using AmongUs.InnerNet.GameDataMessages;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

// NPC de rede: o host cria o objeto e envia atualizações SnapTo aos clientes.
internal sealed class EventoNpcSeguidor : EventoVotacao
{
    public override string Nome => "Criar NPC que segue jogadores";

    private static PlayerControl npc;
    private static float proximaAtualizacao;
    private static float inicioPartida = -1f;
    private static PlayerControl alvo;
    // Posição autoritativa mantida pelo host, independente da posição física do prefab.
    private static Vector2 posicaoControlada;
    private static readonly System.Random Sorteador = new();
    private static int etapaInicializacao;
    private static float proximaEtapaInicializacao;

    private const byte PlayerIdNpc = 254;
    private const float IntervaloMovimento = 0.20f;
    private const float Velocidade = 2.0f;
    private const float DistanciaParada = 0.65f;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        if (MeetingHud.Instance != null || ShipStatus.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC cancelado: entre na fase de tarefas antes de criá-lo.");
            return;
        }

        // Evita criar o Player(Clone) durante a intro/inicialização dos dados.
        // O EHR também posterga objetos de rede até a partida estar estável.
        if (inicioPartida < 0f)
        {
            inicioPartida = Time.realtimeSinceStartup;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC aguardando: espere 10 segundos após o início das tarefas e clique novamente.");
            return;
        }

        if (Time.realtimeSinceStartup - inicioPartida < 10f)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NPC ainda não criado: espere 10 segundos após o início das tarefas e clique novamente.");
            return;
        }

        if (npc != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "O NPC já existe nesta partida.");
            return;
        }

        try
        {
            // Segue o padrão do EHR: cria fora da área jogável e só depois
            // inicializa os clientes; a posição lógica continua sendo a do host.
            Vector2 spawnPos = PlayerControl.LocalPlayer.transform.position;
            posicaoControlada = spawnPos;
            npc = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, Vector2.zero, Quaternion.identity);
            npc.PlayerId = PlayerIdNpc;
            npc.isNew = false;
            npc.notRealPlayer = true;
            npc.NetTransform.SnapTo(new Vector2(50f, 50f));

            // Registra e transmite o objeto falso para todos os clientes.
            AmongUsClient.Instance.NetIdCnt += 1U;
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);

                writer.StartMessage(4);
                var spawn = AmongUsClient.Instance.CreateSpawnMessage(
                    npc, -2, SpawnFlags.None);
                spawn.SerializeValues(writer);
                writer.EndMessage();

                // Os três NetIds auxiliares do prefab não são necessários ao NPC.
                for (uint i = 1; i <= 3; i++)
                {
                    writer.StartMessage(4);
                    writer.WritePacked(2U);
                    writer.WritePacked(-2);
                    writer.Write((byte)SpawnFlags.None);
                    writer.WritePacked(1);
                    writer.WritePacked(AmongUsClient.Instance.NetIdCnt - i);
                    writer.StartMessage(1);
                    writer.EndMessage();
                    writer.EndMessage();
                }

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;
            }
            finally
            {
                writer?.Recycle();
            }

            if (PlayerControl.AllPlayerControls.Contains(npc))
                PlayerControl.AllPlayerControls.Remove(npc);

            // O EHR oculta o corpo padrão; o texto é desenhado pelo sistema visual.
            try { npc.cosmetics.currentBodySprite.BodySprite.color = Color.clear; } catch { }
            try { npc.cosmetics.colorBlindText.color = Color.clear; } catch { }

            // Não associar os dados do host até o objeto já existir nos clientes.
            // O EHR aguarda 0,1 s entre o spawn e a associação temporária por cliente.
            etapaInicializacao = 1;
            proximaEtapaInicializacao = Time.realtimeSinceStartup + 0.10f;

            alvo = EscolherAlvo();
            proximaAtualizacao = Time.realtimeSinceStartup + IntervaloMovimento;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "NPC criado; começando a seguir jogadores reais.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao criar NPC: " + ex);
            RemoverLocal();
        }
    }

    public static void Atualizar()
    {
        if (YouTubeVoteBridgePlugin.EhHost() &&
            AmongUsClient.Instance != null && ShipStatus.Instance != null &&
            MeetingHud.Instance == null && inicioPartida < 0f)
        {
            inicioPartida = Time.realtimeSinceStartup;
        }

        if (npc == null || !YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        if (MeetingHud.Instance != null)
            return;

        if (etapaInicializacao > 0 && Time.realtimeSinceStartup >= proximaEtapaInicializacao)
        {
            try
            {
                if (etapaInicializacao == 1)
                {
                    EnviarInicializacaoParaClientes();
                    etapaInicializacao = 2;
                    proximaEtapaInicializacao = Time.realtimeSinceStartup + 0.15f;
                    return;
                }

                if (etapaInicializacao == 2)
                {
                    EnviarVisualNpc();
                    etapaInicializacao = 0;
                    proximaAtualizacao = Time.realtimeSinceStartup + IntervaloMovimento;
                    return;
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha na inicialização visual do NPC: " + ex);
                etapaInicializacao = 0;
            }
        }

        if (etapaInicializacao > 0)
            return;

        if (Time.realtimeSinceStartup < proximaAtualizacao)
            return;

        proximaAtualizacao = Time.realtimeSinceStartup + IntervaloMovimento;

        if (!AlvoValido(alvo))
            alvo = EscolherAlvo();

        if (!AlvoValido(alvo))
            return;

        // Não usa transform.position como fonte da verdade: colisões ou física podem
        // deixar o prefab preso. O host calcula cada passo usando a variável própria.
        Vector2 atual = posicaoControlada;
        Vector2 destino = alvo.transform.position;
        Vector2 diferenca = destino - atual;
        if (diferenca.sqrMagnitude <= DistanciaParada * DistanciaParada)
            return;

        Vector2 novaPosicao = Vector2.MoveTowards(
            atual, destino, Velocidade * IntervaloMovimento);

        try
        {
            // Atualiza primeiro a posição lógica; mesmo que a colisão do prefab
            // não acompanhe o movimento, o próximo passo não fica travado nela.
            posicaoControlada = novaPosicao;
            // Mesmo padrão do EHR: atualizar localmente com +1 e transmitir
            // o SnapTo com +2, sem Reliable para não sobrecarregar a conexão.
            npc.NetTransform.SnapTo(posicaoControlada,
                (ushort)(npc.NetTransform.lastSequenceId + 1U));

            ushort sequencia = (ushort)(npc.NetTransform.lastSequenceId + 2U);
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                npc.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.None);
            writer.Write(posicaoControlada.x);
            writer.Write(posicaoControlada.y);
            writer.Write(sequencia);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao movimentar NPC: " + ex.Message);
        }
    }

    private static void EnviarInicializacaoParaClientes()
    {
        if (npc == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost)
            return;

        MessageWriter stream = MessageWriter.Get(SendOption.Reliable);
        try
        {
            stream.StartMessage(26);
            stream.WritePacked(AmongUsClient.Instance.GameId);

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.AmOwner || jogador.Data == null ||
                    jogador.Data.Disconnected)
                    continue;

                // Direciona a sequência ao cliente dono deste jogador.
                stream.StartMessage(6);
                stream.Write(AmongUsClient.Instance.GameId);
                stream.WritePacked(jogador.OwnerId);

                // Associa temporariamente o objeto falso ao PlayerId do cliente.
                stream.StartMessage(1);
                stream.WritePacked(npc.NetId);
                stream.Write(jogador.PlayerId);
                stream.EndMessage();

                // RPC de inicialização visual usado por NPCs compatíveis com vanilla.
                stream.StartMessage(2);
                stream.WritePacked(npc.NetId);
                stream.Write((byte)RpcCalls.MurderPlayer);
                stream.WriteNetObject(npc);
                stream.Write((int)MurderResultFlags.FailedError);
                stream.EndMessage();

                // Reatribui o ID reservado ao NPC.
                stream.StartMessage(1);
                stream.WritePacked(npc.NetId);
                stream.Write(PlayerIdNpc);
                stream.EndMessage();

                stream.EndMessage();
            }

            stream.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(stream);
        }
        finally
        {
            stream.Recycle();
        }
    }

    private static PlayerControl EscolherAlvo()
    {
        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == npc || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;
            vivos.Add(jogador);
        }

        return vivos.Count == 0 ? null : vivos[Sorteador.Next(vivos.Count)];
    }

    private static bool AlvoValido(PlayerControl jogador) =>
        jogador != null && jogador.Data != null &&
        !jogador.Data.Disconnected && !jogador.Data.IsDead;

    // Estratégia visual inspirada no EHR: sincroniza temporariamente o outfit
    // do host, aplica Shapeshift no objeto falso e restaura o outfit original.
    private static void EnviarVisualNpc()
    {
        if (npc == null || npc.Data == null || PlayerControl.LocalPlayer?.Data == null ||
            AmongUsClient.Instance == null)
            return;

        var dadosHost = PlayerControl.LocalPlayer.Data;
        var roupa = dadosHost.Outfits[PlayerOutfitType.Default];

        string nomeOriginal = roupa.PlayerName;
        int corOriginal = roupa.ColorId;
        string chapeuOriginal = roupa.HatId;
        string peleOriginal = roupa.SkinId;
        string petOriginal = roupa.PetId;
        string visorOriginal = roupa.VisorId;

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        try
        {
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);

            roupa.PlayerName = "<size=14><br></size>NPC";
            roupa.ColorId = 0;
            roupa.HatId = "";
            roupa.SkinId = "";
            roupa.PetId = "";
            roupa.VisorId = "";

            writer.StartMessage(1);
            writer.WritePacked(dadosHost.NetId);
            dadosHost.Serialize(writer, false);
            writer.EndMessage();

            try { npc.Shapeshift(PlayerControl.LocalPlayer, false); }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Shapeshift visual do NPC: " + ex.Message);
            }

            writer.StartMessage(2);
            writer.WritePacked(npc.NetId);
            writer.Write((byte)RpcCalls.Shapeshift);
            writer.WriteNetObject(PlayerControl.LocalPlayer);
            writer.Write(false);
            writer.EndMessage();

            roupa.PlayerName = nomeOriginal;
            roupa.ColorId = corOriginal;
            roupa.HatId = chapeuOriginal;
            roupa.SkinId = peleOriginal;
            roupa.PetId = petOriginal;
            roupa.VisorId = visorOriginal;

            writer.StartMessage(1);
            writer.WritePacked(dadosHost.NetId);
            dadosHost.Serialize(writer, false);
            writer.EndMessage();

            Vector2 posicao = posicaoControlada;
            ushort sequencia = (ushort)(npc.NetTransform.lastSequenceId + 1);
            writer.StartMessage(2);
            writer.WritePacked(npc.NetTransform.NetId);
            writer.Write((byte)RpcCalls.SnapTo);
            writer.Write(posicao.x);
            writer.Write(posicao.y);
            writer.Write(sequencia);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer = null;
        }
        finally
        {
            writer?.Recycle();
            roupa.PlayerName = nomeOriginal;
            roupa.ColorId = corOriginal;
            roupa.HatId = chapeuOriginal;
            roupa.SkinId = peleOriginal;
            roupa.PetId = petOriginal;
            roupa.VisorId = visorOriginal;
        }
    }

    public static void Limpar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || npc == null ||
            AmongUsClient.Instance == null)
        {
            RemoverLocal();
            return;
        }

        try
        {
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);
            writer.StartMessage(5);
            writer.WritePacked(npc.NetId);
            writer.EndMessage();
            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            AmongUsClient.Instance.RemoveNetObject(npc);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao remover NPC da rede: " + ex.Message);
        }
        finally
        {
            RemoverLocal();
        }
    }

    private static void RemoverLocal()
    {
        if (npc != null)
        {
            try
            {
                if (PlayerControl.AllPlayerControls.Contains(npc))
                    PlayerControl.AllPlayerControls.Remove(npc);
            }
            catch { }

            try { UnityEngine.Object.Destroy(npc.gameObject); }
            catch { }
        }

        npc = null;
        alvo = null;
        posicaoControlada = Vector2.zero;
        etapaInicializacao = 0;
        proximaEtapaInicializacao = 0f;
        // inicioPartida permanece até o fim da partida para impedir um spawn precoce.
    }
}

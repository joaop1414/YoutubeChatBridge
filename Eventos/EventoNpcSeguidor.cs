using System;
using System.Collections.Generic;
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
            // Reutiliza a estrutura de pacote que já funcionou no EventoCriarCorpoFalso,
            // mas mantém o PlayerControl vivo e não envia MurderPlayer nem despawn.
            Vector2 spawnPos = PlayerControl.LocalPlayer.transform.position;
            posicaoControlada = spawnPos;
            npc = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, spawnPos, Quaternion.identity);
            npc.PlayerId = PlayerIdNpc;
            npc.isNew = false;
            npc.notRealPlayer = true;

            // Segue a ordem do EHR: não atribuir CachedPlayerData antes do spawn.
            // O PlayerId 254 ainda não possui dados próprios no GameData; o cache
            // do host será associado somente depois da etapa de rede com os clientes.
            if (npc.NetTransform == null)
                throw new InvalidOperationException(
                    "O prefab do NPC não inicializou NetTransform.");

            npc.NetTransform.SnapTo(spawnPos);

            // Criação de rede centralizada no helper portado do EHR.
            CustomNetObject.EnviarSpawn(npc);

            if (PlayerControl.AllPlayerControls.Contains(npc))
                PlayerControl.AllPlayerControls.Remove(npc);

            // A criação do objeto, sozinha, não basta para clientes vanilla.
            // O EHR associa temporariamente o PlayerId falso a cada cliente,
            // envia MurderPlayer com FailedError e restaura o ID 254.
            // Fazemos isso em uma etapa atrasada, depois do pacote de spawn.
            etapaInicializacao = 1;
            proximaEtapaInicializacao = Time.realtimeSinceStartup + 0.15f;
            alvo = EscolherAlvo();
            proximaAtualizacao = Time.realtimeSinceStartup + IntervaloMovimento;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"NPC criado com pacote do corpo falso (sem morte/despawn), posição=({spawnPos.x:0.00}, {spawnPos.y:0.00}).");
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

        // Mantém o NPC parado durante o diagnóstico. Primeiro, conclui a
        // associação de rede usada pelo EHR para objetos falsos em clientes vanilla.
        if (etapaInicializacao == 1 && Time.realtimeSinceStartup >= proximaEtapaInicializacao)
        {
            try
            {
                int mensagens = CustomNetObject.AssociarClientes(npc, PlayerIdNpc);
                etapaInicializacao = 2;
                proximaEtapaInicializacao = Time.realtimeSinceStartup + 0.15f;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"NPC: associação EHR enviada para {mensagens} clientes vanilla; preparando aparência.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha na associação EHR do NPC: " + ex);
                Limpar();
                return;
            }
        }

        // Etapa visual do EHR: envia temporariamente o outfit do host com
        // nome/cor/acessórios neutros, aplica Shapeshift no objeto falso e
        // restaura imediatamente o outfit real do host na rede.
        if (etapaInicializacao == 2 && Time.realtimeSinceStartup >= proximaEtapaInicializacao)
        {
            PlayerControl host = PlayerControl.LocalPlayer;
            try
            {
                if (host == null || host.Data == null ||
                    host.Data.Outfits == null || npc == null)
                    throw new InvalidOperationException("Host, outfit ou NPC indisponível na etapa visual.");

                // Mesmo preparo do corpo usado pelo CustomNetObject/Gemini do End K not.
                // Sem isso, o PlayerPrefab pode existir na rede mas manter o renderer invisível.
                try
                {
                    var body = npc.cosmetics?.currentBodySprite?.BodySprite;
                    if (body != null)
                    {
                        PlayerMaterial.SetColors(0, body);
                        body.color = Color.white;
                        body.enabled = true;
                    }
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "NPC: preparação do renderer do corpo falhou: " + ex.Message);
                }

                var outfit = host.Data.Outfits[PlayerOutfitType.Default];
                string nomeOriginal = outfit.PlayerName;
                int corOriginal = outfit.ColorId;
                string chapeuOriginal = outfit.HatId;
                string roupaOriginal = outfit.SkinId;
                string mascoteOriginal = outfit.PetId;
                string visorOriginal = outfit.VisorId;

                MessageWriter visual = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    visual.StartMessage(5);
                    visual.Write(AmongUsClient.Instance.GameId);

                    outfit.PlayerName = "NPC";
                    outfit.ColorId = 0;
                    outfit.HatId = "";
                    outfit.SkinId = "";
                    outfit.PetId = "";
                    outfit.VisorId = "";

                    // EHR envia os dados temporários do host antes do Shapeshift.
                    visual.StartMessage(1);
                    visual.WritePacked(host.Data.NetId);
                    host.Data.Serialize(visual, false);
                    visual.EndMessage();

                    try { npc.Shapeshift(host, false); }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            "NPC: Shapeshift local falhou: " + ex.Message);
                    }

                    visual.StartMessage(2);
                    visual.WritePacked(npc.NetId);
                    visual.Write((byte)RpcCalls.Shapeshift);
                    visual.WriteNetObject(host);
                    visual.Write(false);
                    visual.EndMessage();

                    // Restaura o outfit original do host e envia a restauração
                    // no mesmo pacote, como no EHR.
                    outfit.PlayerName = nomeOriginal;
                    outfit.ColorId = corOriginal;
                    outfit.HatId = chapeuOriginal;
                    outfit.SkinId = roupaOriginal;
                    outfit.PetId = mascoteOriginal;
                    outfit.VisorId = visorOriginal;

                    visual.StartMessage(1);
                    visual.WritePacked(host.Data.NetId);
                    host.Data.Serialize(visual, false);
                    visual.EndMessage();

                    // O EHR inclui o SnapTo inicial na mesma sequência de rede
                    // que aplica a aparência, para evitar spawn visual na posição zero.
                    try { npc.NetTransform.SnapTo(posicaoControlada); }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            "NPC: SnapTo inicial local falhou: " + ex.Message);
                    }

                    visual.StartMessage(2);
                    visual.WritePacked(npc.NetTransform.NetId);
                    visual.Write((byte)RpcCalls.SnapTo);
                    visual.Write(posicaoControlada.x);
                    visual.Write(posicaoControlada.y);
                    visual.Write(npc.NetTransform.lastSequenceId);
                    visual.EndMessage();

                    visual.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(visual);
                    visual = null;
                }
                finally
                {
                    visual?.Recycle();

                    // Garantia local de restauração mesmo se o envio falhar.
                    outfit.PlayerName = nomeOriginal;
                    outfit.ColorId = corOriginal;
                    outfit.HatId = chapeuOriginal;
                    outfit.SkinId = roupaOriginal;
                    outfit.PetId = mascoteOriginal;
                    outfit.VisorId = visorOriginal;
                }

                etapaInicializacao = 3; // estável/parado; não ativa movimento
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "NPC: sequência visual do EHR enviada; outfit do host restaurado.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha na etapa visual do NPC: " + ex);
                // Não removemos automaticamente: preservar o estado para diagnóstico.
                etapaInicializacao = 3;
            }
        }

        // As etapas 1 e 2 ainda estão inicializando a rede/aparência.
        // A etapa 3 significa que a inicialização terminou e o NPC já pode andar.
        if (etapaInicializacao is 1 or 2)
            return;

        if (MeetingHud.Instance != null)
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
            CustomNetObject.EnviarPosicao(npc, posicaoControlada);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Falha ao movimentar NPC: " + ex.Message);
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

using AmongUs.GameOptions;
using BepInEx.Unity.IL2CPP.Utils;
using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoCriarCorpoFalso : EventoVotacao
{
    public override string Nome => "Criar corpo falso";

    private static readonly System.Random Sorteador = new();
    private static byte? jogadorForcado;
    private static Vector2? posicaoJogadorForcada;
    private static bool criarComoNoisemaker;

    // Reutiliza a sequência de sincronização do evento para um jogador específico.
    public static void ExecutarParaJogador(byte playerId)
    {
        jogadorForcado = playerId;
        posicaoJogadorForcada = null;
        try { new EventoCriarCorpoFalso().Executar(); }
        finally
        {
            jogadorForcado = null;
            posicaoJogadorForcada = null;
        }
    }

    // Cria o corpo falso exatamente na posição em que o jogador foi morto.
    public static void ExecutarParaJogadorNaPosicao(byte playerId, Vector2 posicao)
    {
        jogadorForcado = playerId;
        posicaoJogadorForcada = posicao;
        try { new EventoCriarCorpoFalso().Executar(); }
        finally
        {
            jogadorForcado = null;
            posicaoJogadorForcada = null;
        }
    }

    // Cria o corpo na posição da vítima usando um PlayerControl temporário
    // que recebe Noisemaker antes do RPC MurderPlayer.
    public static void ExecutarComoNoisemakerParaJogadorNaPosicao(byte playerId, Vector2 posicao)
    {
        jogadorForcado = playerId;
        posicaoJogadorForcada = posicao;
        criarComoNoisemaker = true;
        try { new EventoCriarCorpoFalso().Executar(); }
        finally
        {
            jogadorForcado = null;
            posicaoJogadorForcada = null;
            criarComoNoisemaker = false;
        }
    }

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        // O evento normal não cria corpos depois que a reunião começou.
        // No modo forçado pelo tsunami, porém, tentamos reutilizar a mesma
        // rotina mesmo se o StartMeeting já tiver instanciado o MeetingHud.
        if (MeetingHud.Instance != null && !jogadorForcado.HasValue)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Corpo falso cancelado: já existe uma reunião em andamento.");
            return;
        }

        if (jogadorForcado.HasValue)
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                (criarComoNoisemaker ? "Corpo falso Noisemaker do NPC" : "Corpo falso do tsunami") +
                ": execução forçada para PlayerId=" + jogadorForcado.Value +
                "; MeetingHud ativo=" + (MeetingHud.Instance != null) + ".");

        PlayerControl temporario = null;

        try
        {
            if (GameManager.Instance == null ||
                GameManager.Instance.deadBodyPrefab == null ||
                GameManager.Instance.deadBodyPrefab.Length == 0 ||
                AmongUsClient.Instance.PlayerPrefab == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Não foi possível criar corpo falso: prefab necessário não encontrado.");
                return;
            }

            List<PlayerControl> candidatos = new();

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected ||
                    (jogadorForcado.HasValue && jogador.PlayerId != jogadorForcado.Value) ||
                    (!jogadorForcado.HasValue && jogador.Data.IsDead) ||
                    jogador.KillAnimations == null ||
                    jogador.KillAnimations.Length == 0)
                    continue;

                candidatos.Add(jogador);
            }

            if (candidatos.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Corpo falso cancelado: não há jogadores vivos para servir de modelo.");
                return;
            }

            if (ShipStatus.Instance == null || ShipStatus.Instance.AllRooms == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Não foi possível criar corpo falso: salas do mapa não encontradas.");
                return;
            }

            List<PlainShipRoom> salas = new();

            foreach (PlainShipRoom sala in ShipStatus.Instance.AllRooms)
            {
                if (sala == null || sala.roomArea == null ||
                    sala.RoomId == SystemTypes.Hallway ||
                    sala.RoomId == SystemTypes.Outside)
                    continue;

                salas.Add(sala);
            }

            if (!posicaoJogadorForcada.HasValue && salas.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Corpo falso cancelado: não foram encontradas salas válidas no mapa.");
                return;
            }

            PlayerControl modelo = candidatos[Sorteador.Next(candidatos.Count)];
            Vector3 deslocamento = modelo.KillAnimations[0].BodyOffset;

            Vector2 pontoJogador = Vector2.zero;
            PlainShipRoom salaEscolhida = null;

            if (posicaoJogadorForcada.HasValue)
            {
                // A vítima já está no ponto correto; não sorteia outra sala.
                pontoJogador = posicaoJogadorForcada.Value;
            }
            else
            {
                // Evento normal: escolhe um ponto seguro aleatório no mapa.
                bool encontrouPosicaoSegura = false;

                for (int tentativa = 0; tentativa < Math.Max(20, salas.Count * 4); tentativa++)
                {
                    PlainShipRoom salaTentativa = salas[Sorteador.Next(salas.Count)];
                    if (PosicaoSeguraMapa.TentarObterPontoSeguro(
                        salaTentativa.roomArea, new Vector2(deslocamento.x, deslocamento.y),
                        out pontoJogador))
                    {
                        salaEscolhida = salaTentativa;
                        encontrouPosicaoSegura = true;
                        break;
                    }
                }

                if (!encontrouPosicaoSegura || salaEscolhida == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Corpo falso cancelado: não foi encontrado um ponto seguro livre de colisões.");
                    return;
                }
            }

            Vector3 posicao = new(pontoJogador.x, pontoJogador.y, pontoJogador.y / 1000f);
            Vector3 posicaoCorpo = posicao + deslocamento;
            posicaoCorpo.z = posicaoCorpo.y / 1000f;

            if (posicaoJogadorForcada.HasValue)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Corpo falso criado na posição forçada da vítima: " +
                    $"posição=({posicaoCorpo.x:0.00}, {posicaoCorpo.y:0.00}).");
            }
            else
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Sala escolhida para o corpo falso: {salaEscolhida.RoomId}, " +
                    $"posição=({posicaoCorpo.x:0.00}, {posicaoCorpo.y:0.00}).");
            }

            // O host cria o corpo localmente. Nos clientes, o RPC MurderPlayer
            // do jogador temporário fará o jogo criar o corpo correspondente.
            CriarCorpoLocal(modelo, posicaoCorpo);

            // O clone já nasce na posição correta. Assim não precisamos usar
            // SnapTo local antes do Spawn; a própria mensagem de Spawn replica
            // a posição inicial para os clientes.
            temporario = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, posicao, Quaternion.identity);

            temporario.PlayerId = modelo.PlayerId;
            temporario.isNew = false;
            temporario.notRealPlayer = true;

            // O prefab de PlayerControl contém três objetos de rede.
            AmongUsClient.Instance.NetIdCnt += 1U;

            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                // Mensagem GameData para todos os clientes.
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);

                // Spawn do PlayerControl temporário.
                writer.StartMessage(4);
                var spawn = AmongUsClient.Instance.CreateSpawnMessage(
                    temporario, -2, SpawnFlags.None);
                spawn.SerializeValues(writer);
                writer.EndMessage();

                // Compatibilidade com clientes vanilla: mensagens de limpeza
                // para os NetIds auxiliares do prefab temporário.
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

                // RPC SetColor no objeto temporário.
                writer.StartMessage(2);
                writer.WritePacked(temporario.NetId);
                writer.Write((byte)RpcCalls.SetColor);
                writer.Write(temporario.Data.NetId);
                writer.Write(modelo.Data.DefaultOutfit.ColorId);
                writer.EndMessage();

                if (criarComoNoisemaker)
                {
                    // Aplica localmente e transmite SetRole antes de MurderPlayer.
                    try
                    {
                        // Mesmo padrão já utilizado pela RoleSwitchAPI no projeto:
                        // StartCoroutine diretamente no PlayerControl IL2CPP.
                        temporario.StartCoroutine(
                            temporario.CoSetRole(RoleTypes.Noisemaker, true));
                    }
                    catch (Exception exRole)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            "CoSetRole(Noisemaker) local falhou: " + exRole);
                    }

                    writer.StartMessage(2);
                    writer.WritePacked(temporario.NetId);
                    writer.Write((byte)RpcCalls.SetRole);
                    writer.Write((ushort)RoleTypes.Noisemaker);
                    writer.Write(true);
                    writer.EndMessage();

                    // O host não recebe de volta o próprio RPC MurderPlayer.
                    // Tenta executar o OnDeath da função associada ao clone localmente.
                    try
                    {
                        var roleNoisemaker = temporario.Data?.Role?.Cast<NoisemakerRole>();
                        if (roleNoisemaker != null)
                        {
                            roleNoisemaker.OnDeath(DeathReason.Kill);
                            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                                "NoisemakerRole.OnDeath executado localmente para o corpo falso; PlayerId=" +
                                temporario.PlayerId + ".");
                        }
                        else
                        {
                            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                                "O clone ainda não expõe NoisemakerRole em Data.Role no host; " +
                                "o alerta local pode não ser disparado.");
                        }
                    }
                    catch (Exception exDeath)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            "OnDeath local do Noisemaker falhou: " + exDeath);
                    }

                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "SetRole(Noisemaker) enviado antes de MurderPlayer; PlayerId=" +
                        temporario.PlayerId + ".");
                }

                // RPC MurderPlayer: clientes remotos geram o corpo usando
                // o comportamento normal do jogo, sem matar o jogador real.
                writer.StartMessage(2);
                writer.WritePacked(temporario.NetId);
                writer.Write((byte)RpcCalls.MurderPlayer);
                writer.WriteNetObject(temporario);
                writer.Write((int)MurderResultFlags.Succeeded);
                writer.EndMessage();

                // Restaura a cor base do objeto temporário.
                writer.StartMessage(2);
                writer.WritePacked(temporario.NetId);
                writer.Write((byte)RpcCalls.SetColor);
                writer.Write(temporario.Data.NetId);
                writer.Write(temporario.Data.DefaultOutfit.ColorId);
                writer.EndMessage();

                // Serializa os dados do objeto e remove o jogador temporário.
                writer.StartMessage(1);
                writer.WritePacked(temporario.Data.NetId);
                temporario.Data.Serialize(writer, false);
                writer.EndMessage();

                writer.StartMessage(5);
                writer.WritePacked(temporario.NetId);
                writer.EndMessage();

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Sequência de sincronização do corpo falso enviada. " +
                    $"Modelo PlayerId={modelo.PlayerId}, posição=({posicaoCorpo.x:0.00}, {posicaoCorpo.y:0.00}), " +
                    $"Noisemaker={criarComoNoisemaker}.");
            }
            finally
            {
                writer?.Recycle();
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Falha ao criar/sincronizar corpo falso: " + ex);
        }
        finally
        {
            if (temporario != null)
            {
                try
                {
                    if (PlayerControl.AllPlayerControls.Contains(temporario))
                        PlayerControl.AllPlayerControls.Remove(temporario);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Não foi possível remover o jogador temporário da lista local: " + ex.Message);
                }

                try
                {
                    if (AmongUsClient.Instance != null)
                        AmongUsClient.Instance.RemoveNetObject(temporario);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Falha ao remover NetObject temporário: " + ex.Message);
                }

                try
                {
                    UnityEngine.Object.Destroy(temporario.gameObject);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Falha ao destruir o jogador temporário: " + ex.Message);
                }
            }
        }
    }

    private static void CriarCorpoLocal(PlayerControl modelo, Vector3 posicaoCorpo)
    {
        DeadBody corpo = UnityEngine.Object.Instantiate(
            GameManager.Instance.deadBodyPrefab[0]);

        corpo.ParentId = modelo.PlayerId;

        foreach (SpriteRenderer renderer in corpo.bodyRenderers)
            modelo.SetPlayerMaterialColors(renderer);

        modelo.SetPlayerMaterialColors(corpo.bloodSplatter);
        corpo.transform.position = posicaoCorpo;
        corpo.gameObject.SetActive(true);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"Corpo falso local criado e ativado; ParentId={modelo.PlayerId}, " +
            $"posição=({posicaoCorpo.x:0.00}, {posicaoCorpo.y:0.00}).");
    }
}

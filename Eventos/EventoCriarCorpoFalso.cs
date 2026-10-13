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

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        if (MeetingHud.Instance != null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Corpo falso cancelado: já existe uma reunião em andamento.");
            return;
        }

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
                    jogador.Data.Disconnected || jogador.Data.IsDead ||
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

            PlayerControl modelo = candidatos[Sorteador.Next(candidatos.Count)];
            Vector3 posicao = modelo.transform.position;
            Vector3 deslocamento = modelo.KillAnimations[0].BodyOffset;
            Vector3 posicaoCorpo = posicao + deslocamento;
            posicaoCorpo.z = posicaoCorpo.y / 1000f;

            // O host cria o corpo localmente. Nos clientes, o RPC MurderPlayer
            // do jogador temporário fará o jogo criar o corpo correspondente.
            CriarCorpoLocal(modelo, posicaoCorpo);

            temporario = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, Vector2.zero, Quaternion.identity);

            temporario.PlayerId = modelo.PlayerId;
            temporario.isNew = false;
            temporario.notRealPlayer = true;
            temporario.NetTransform.SnapTo(posicao);

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
                    $"Modelo PlayerId={modelo.PlayerId}, posição=({posicaoCorpo.x:0.00}, {posicaoCorpo.y:0.00}).");
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

        corpo.enabled = false;
        corpo.ParentId = modelo.PlayerId;

        foreach (SpriteRenderer renderer in corpo.bodyRenderers)
            modelo.SetPlayerMaterialColors(renderer);

        modelo.SetPlayerMaterialColors(corpo.bloodSplatter);
        corpo.transform.position = posicaoCorpo;
    }
}

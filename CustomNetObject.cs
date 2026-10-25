using System;
using System.Linq;
using Hazel;
using InnerNet;
using AmongUs.GameOptions;
using AmongUs.InnerNet.GameDataMessages;
using UnityEngine;

namespace MoreGamemodes
{
    /// <summary>
    /// Helpers de rede para personagens artificiais e o indicador do tsunami.
    /// Mantém apenas as operações usadas pelo YouTubeVoteBridge, sem depender
    /// do Main/Utils do mod MoreGamemodes original.
    /// </summary>
    internal static class CustomNetObject
    {
        public static void EnviarSpawn(PlayerControl jogador, bool ocultarVisual = true, bool removerDaLista = true)
        {
            if (jogador == null || AmongUsClient.Instance == null) return;

            // Atribui um NetId novo antes de serializar o spawn.
            AmongUsClient.Instance.NetIdCnt += 1U;
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(4);
                SpawnGameDataMessage spawn = AmongUsClient.Instance.CreateSpawnMessage(jogador, -2, SpawnFlags.None);
                spawn.SerializeValues(writer);
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
            }
            finally
            {
                writer.Recycle();
            }

            if (ocultarVisual)
            {
                try
                {
                    if (jogador.cosmetics?.currentBodySprite?.BodySprite != null)
                        jogador.cosmetics.currentBodySprite.BodySprite.color = Color.clear;
                    if (jogador.cosmetics?.colorBlindText != null)
                        jogador.cosmetics.colorBlindText.color = Color.clear;
                }
                catch { }
            }

            if (removerDaLista && PlayerControl.AllPlayerControls.Contains(jogador))
                PlayerControl.AllPlayerControls.Remove(jogador);
        }

        public static int AssociarClientes(PlayerControl jogador, byte playerId)
        {
            if (jogador == null || AmongUsClient.Instance == null) return 0;

            // O cliente vanilla não aceita a associação direta isolada em GameData.
            // Reproduz a sequência usada pelo MoreGamemodes/EHR: associa temporariamente
            // o NetId ao PlayerId de um jogador real, envia um MurderPlayer que falha
            // deliberadamente e, por fim, aplica o PlayerId reservado ao objeto falso.
            int enviados = 0;
            foreach (PlayerControl jogadorReal in PlayerControl.AllPlayerControls.ToArray())
            {
                if (jogadorReal == null || jogadorReal.AmOwner) continue;

                int clienteId;
                try
                {
                    var cliente = AmongUsClient.Instance.allClients.ToArray()
                        .FirstOrDefault(cd => cd != null && cd.Character != null &&
                            cd.Character.PlayerId == jogadorReal.PlayerId);
                    if (cliente == null) continue;
                    clienteId = cliente.Id;
                }
                catch
                {
                    continue;
                }

                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    // GameDataTo para o cliente específico.
                    writer.StartMessage(6);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(clienteId);

                    // Associação temporária com um PlayerId que o cliente já conhece.
                    writer.StartMessage(1);
                    writer.WritePacked(jogador.NetId);
                    writer.Write(jogadorReal.PlayerId);
                    writer.EndMessage();

                    // RPC intencionalmente inválido: força o cliente a materializar
                    // o PlayerControl antes de receber o PlayerId reservado.
                    writer.StartMessage(2);
                    writer.WritePacked(jogador.NetId);
                    writer.Write((byte)RpcCalls.MurderPlayer);
                    writer.WriteNetObject(jogador);
                    writer.Write((int)MurderResultFlags.FailedError);
                    writer.EndMessage();

                    // Troca para o ID final que o mod reservou para este objeto.
                    writer.StartMessage(1);
                    writer.WritePacked(jogador.NetId);
                    writer.Write(playerId);
                    writer.EndMessage();

                    writer.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                    enviados++;
                }
                finally
                {
                    writer.Recycle();
                }
            }

            return enviados;
        }

        public static void EnviarAparencia(PlayerControl jogador, string nome, byte corId)
        {
            if (jogador?.Data == null || AmongUsClient.Instance == null) return;

            var outfit = jogador.Data.Outfits[PlayerOutfitType.Default];
            string nomeAnterior = outfit.PlayerName;
            var corAnterior = outfit.ColorId;
            string chapeuAnterior = outfit.HatId;
            string roupaAnterior = outfit.SkinId;
            string petAnterior = outfit.PetId;
            string visorAnterior = outfit.VisorId;

            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                outfit.PlayerName = nome ?? string.Empty;
                outfit.ColorId = corId;
                outfit.HatId = string.Empty;
                outfit.SkinId = string.Empty;
                outfit.PetId = string.Empty;
                outfit.VisorId = string.Empty;

                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(1);
                writer.WritePacked(jogador.Data.NetId);
                jogador.Data.Serialize(writer, false);
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
            }
            finally
            {
                outfit.PlayerName = nomeAnterior;
                outfit.ColorId = corAnterior;
                outfit.HatId = chapeuAnterior;
                outfit.SkinId = roupaAnterior;
                outfit.PetId = petAnterior;
                outfit.VisorId = visorAnterior;
                writer.Recycle();
            }
        }

        // Publica sprites/textos customizados pelo mesmo fluxo de Shapeshift
        // usado pelo EHR/MoreGamemodes. Separado de EnviarAparencia para não
        // alterar a aparência normal dos NPCs criados pela FakePlayerAPI.
        public static void EnviarAparenciaSprite(PlayerControl jogador, string sprite)
        {
            PlayerControl host = PlayerControl.LocalPlayer;
            if (jogador == null || host?.Data == null || jogador.Data == null ||
                AmongUsClient.Instance == null) return;

            // MoreGamemodes usa um jogador real como origem da aparência, em vez
            // de sempre usar o host. O nome temporário precisa estar nos dados
            // do alvo ANTES do Shapeshift para os clientes receberem o sprite.
            PlayerControl alvo = PlayerControl.AllPlayerControls.ToArray()
                .Where(p => p != null && p != jogador && p.Data != null &&
                    !p.Data.Disconnected && !p.inVent && !p.walkingToVent)
                .OrderBy(p => p.PlayerId)
                .FirstOrDefault();
            if (alvo == null) alvo = host;
            if (alvo?.Data == null) return;

            var outfit = alvo.Data.Outfits[PlayerOutfitType.Default];
            string nomeAnterior = outfit.PlayerName;
            var corAnterior = outfit.ColorId;
            string chapeuAnterior = outfit.HatId;
            string roupaAnterior = outfit.SkinId;
            string petAnterior = outfit.PetId;
            string visorAnterior = outfit.VisorId;

            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            bool outfitRestaurado = false;
            try
            {
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);

                // Primeiro publica o nome do sprite no jogador-alvo.
                outfit.PlayerName = "<size=14><br></size>" + (sprite ?? string.Empty);
                outfit.ColorId = 0;
                outfit.HatId = string.Empty;
                outfit.SkinId = string.Empty;
                outfit.PetId = string.Empty;
                outfit.VisorId = string.Empty;

                writer.StartMessage(1);
                writer.WritePacked(alvo.Data.NetId);
                alvo.Data.Serialize(writer, false);
                writer.EndMessage();

                // Só depois da publicação do outfit envia o Shapeshift ao objeto.
                try { jogador.Shapeshift(alvo, false); }
                catch { }

                writer.StartMessage(2);
                writer.WritePacked(jogador.NetId);
                writer.Write((byte)RpcCalls.Shapeshift);
                writer.WriteNetObject(alvo);
                writer.Write(false);
                writer.EndMessage();

                // Restaura o jogador real e publica seus dados originais no mesmo pacote.
                outfit.PlayerName = nomeAnterior;
                outfit.ColorId = corAnterior;
                outfit.HatId = chapeuAnterior;
                outfit.SkinId = roupaAnterior;
                outfit.PetId = petAnterior;
                outfit.VisorId = visorAnterior;
                outfitRestaurado = true;

                writer.StartMessage(1);
                writer.WritePacked(alvo.Data.NetId);
                alvo.Data.Serialize(writer, false);
                writer.EndMessage();

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
            }
            finally
            {
                if (!outfitRestaurado)
                {
                    outfit.PlayerName = nomeAnterior;
                    outfit.ColorId = corAnterior;
                    outfit.HatId = chapeuAnterior;
                    outfit.SkinId = roupaAnterior;
                    outfit.PetId = petAnterior;
                    outfit.VisorId = visorAnterior;
                }
                writer.Recycle();
            }
        }

        public static void EnviarPosicao(PlayerControl jogador, Vector2 posicao)
        {
            if (jogador?.NetTransform == null || AmongUsClient.Instance == null) return;

            ushort sequencia = (ushort)(jogador.NetTransform.lastSequenceId + 1);
            jogador.NetTransform.SnapTo(posicao, sequencia);

            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.Reliable, -1);
            NetHelpers.WriteVector2(posicao, writer);
            writer.Write(sequencia);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
    }
}

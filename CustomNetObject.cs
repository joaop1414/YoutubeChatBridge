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
            // Envia o spawn individualmente para cada cliente remoto.
            // O objeto é o mesmo (NetId único), mas cada destinatário recebe
            // a sua própria mensagem GameDataTo, sem depender do broadcast.
            foreach (ClientData cliente in AmongUsClient.Instance.allClients.ToArray())
            {
                if (cliente == null || cliente.Id == AmongUsClient.Instance.ClientId)
                    continue;

                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    writer.StartMessage(6);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(cliente.Id);
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
        public static PlayerControl EnviarAparenciaSprite(PlayerControl jogador, string sprite, out string nomeOriginal)
        {
            nomeOriginal = null;
            PlayerControl host = PlayerControl.LocalPlayer;
            if (jogador == null || host?.Data == null || jogador.Data == null ||
                AmongUsClient.Instance == null) return null;

            // O host é a origem do Shapeshift, mas seu nome nunca deve ser
            // alterado nem por RPC temporário: SetName no próprio jogador pode
            // deixar o nome de aviso preso na interface local.
            PlayerControl alvo = host;
            if (alvo?.Data == null) return null;

            nomeOriginal = alvo.Data.Outfits[PlayerOutfitType.Default].PlayerName ?? string.Empty;

            var outfit = alvo.Data.Outfits[PlayerOutfitType.Default];
            string nomeOutfitAnterior = outfit.PlayerName;
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

                // Mantém o nome original intacto; somente os cosméticos são temporários.
                outfit.ColorId = 0;
                outfit.HatId = string.Empty;
                outfit.SkinId = string.Empty;
                outfit.PetId = string.Empty;
                outfit.VisorId = string.Empty;

                writer.StartMessage(1);
                writer.WritePacked(alvo.Data.NetId);
                alvo.Data.Serialize(writer, false);
                writer.EndMessage();

                try { jogador.Shapeshift(alvo, false); }
                catch { }

                writer.StartMessage(2);
                writer.WritePacked(jogador.NetId);
                writer.Write((byte)RpcCalls.Shapeshift);
                writer.WriteNetObject(alvo);
                writer.Write(false);
                writer.EndMessage();

                outfit.PlayerName = nomeOutfitAnterior;
                outfit.ColorId = corAnterior;
                outfit.HatId = chapeuAnterior;
                outfit.SkinId = roupaAnterior;
                outfit.PetId = petAnterior;
                outfit.VisorId = visorAnterior;
                outfitRestaurado = true;

                // Reenvia os dados originais do host após restaurar os cosméticos.
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
                    outfit.PlayerName = nomeOutfitAnterior;
                    outfit.ColorId = corAnterior;
                    outfit.HatId = chapeuAnterior;
                    outfit.SkinId = roupaAnterior;
                    outfit.PetId = petAnterior;
                    outfit.VisorId = visorAnterior;
                }
                writer.Recycle();
            }

            return alvo;
        }

        // Mesmo formato vanilla usado pelo LobbyTagManager para SetName.
        // É separado do pacote de Shapeshift para preservar a ordem de chegada.
        public static void EnviarSetName(PlayerControl jogador, string nome)
        {
            if (jogador == null || jogador.Data == null || AmongUsClient.Instance == null)
                return;

            MessageWriter writer = null;
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(2);
                writer.WritePacked(jogador.NetId);
                writer.Write((byte)RpcCalls.SetName);
                writer.Write(jogador.Data.NetId);
                writer.Write(nome ?? string.Empty);
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer.Recycle();
                writer = null;

                jogador.SetName(nome ?? string.Empty);
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Recycle(); } catch { }
                }
            }
        }

        public static void EnviarPosicao(PlayerControl jogador, Vector2 posicao)
        {
            if (jogador?.NetTransform == null || AmongUsClient.Instance == null) return;

            ushort sequencia = (ushort)(jogador.NetTransform.lastSequenceId + 1);
            jogador.NetTransform.SnapTo(posicao, sequencia);

            // O host atualiza a sua cópia e manda SnapTo em mensagens
            // individuais para cada cliente remoto.
            foreach (ClientData cliente in AmongUsClient.Instance.allClients.ToArray())
            {
                if (cliente == null || cliente.Id == AmongUsClient.Instance.ClientId)
                    continue;

                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    jogador.NetTransform.NetId, (byte)RpcCalls.SnapTo,
                    SendOption.Reliable, cliente.Id);
                NetHelpers.WriteVector2(posicao, writer);
                writer.Write(sequencia);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
        }
    }
}

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

                    // O EHR confirmou que servidores vanilla precisam receber três
                    // mensagens Spawn vazias depois do objeto customizado. Sem esse
                    // preenchimento, clientes vanilla podem descartar/ignorar o objeto
                    // de rede e a onda do Tsunami não aparece.
                    if (EhRegiaoVanilla())
                    {
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
                    }

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

        // O EHR aplica o padding de Spawn somente nas regiões vanilla (NA/EU/AS).
        // A leitura por reflexão evita depender de variações da API de região entre versões.
        private static bool EhRegiaoVanilla()
        {
            try
            {
                object manager = ServerManager.Instance;
                if (manager == null) return false;

                var propRegiao = manager.GetType().GetProperty("CurrentRegion");
                object regiao = propRegiao?.GetValue(manager);
                if (regiao == null) return false;

                var propNome = regiao.GetType().GetProperty("Name");
                string nome = propNome?.GetValue(regiao)?.ToString() ?? string.Empty;
                nome = nome.Trim().ToLowerInvariant();

                return nome == "na" || nome == "eu" || nome == "as" ||
                       nome.Contains("north america") ||
                       nome.Contains("europe") ||
                       nome == "asia" || nome.Contains("asia ");
            }
            catch
            {
                return false;
            }
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

                // O nome do desenho é colocado apenas no estado temporário
                // serializado para os clientes. Restauramos o objeto local do
                // host imediatamente após serializar, antes de atualizar a cena.
                // Assim, o host não deve renderizar o próprio nome como o do NPC.
                outfit.PlayerName = "<size=14><br></size>" + (sprite ?? string.Empty);
                writer.StartMessage(1);
                writer.WritePacked(alvo.Data.NetId);
                alvo.Data.Serialize(writer, false);
                writer.EndMessage();

                // Restaura o nome e os cosméticos locais antes de executar
                // Shapeshift; a mensagem já contém a cópia temporária.
                outfit.PlayerName = nomeOutfitAnterior;
                outfit.ColorId = corAnterior;
                outfit.HatId = chapeuAnterior;
                outfit.SkinId = roupaAnterior;
                outfit.PetId = petAnterior;
                outfit.VisorId = visorAnterior;
                outfitRestaurado = true;

                try { jogador.Shapeshift(alvo, false); }
                catch { }

                writer.StartMessage(2);
                writer.WritePacked(jogador.NetId);
                writer.Write((byte)RpcCalls.Shapeshift);
                writer.WriteNetObject(alvo);
                writer.Write(false);
                writer.EndMessage();

                // Reenvia os dados originais do host depois da transformação,
                // para que os outros clientes não mantenham o nome temporário.
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
        // SetName é um RPC do PlayerControl: o payload contém somente a string.
        // Para sprites, o prefixo de linha é necessário para o layout usado pelo EHR.
        public static void EnviarSetName(PlayerControl jogador, string nome)
        {
            if (jogador == null || jogador.Data == null || AmongUsClient.Instance == null)
                return;

            MessageWriter writer = null;
            string texto = "<size=14><br></size>" + (nome ?? string.Empty);
            try
            {
                writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(2);
                writer.WritePacked(jogador.NetId);
                writer.Write((byte)RpcCalls.SetName);
                writer.Write(texto);
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer.Recycle();
                writer = null;

                jogador.SetName(texto);
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Recycle(); } catch { }
                }
            }
        }

        // Remove o objeto customizado também nos clientes remotos. Destruir
        // apenas o GameObject local deixaria o PlayerControl antigo nos clientes.
        public static void Despawn(PlayerControl jogador)
        {
            if (jogador == null || AmongUsClient.Instance == null) return;

            try
            {
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                try
                {
                    writer.StartMessage(5);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.StartMessage(5);
                    writer.WritePacked(jogador.NetId);
                    writer.EndMessage();
                    writer.EndMessage();
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                }
                finally
                {
                    writer.Recycle();
                }
            }
            catch { }

            try { AmongUsClient.Instance.RemoveNetObject(jogador); } catch { }
            try
            {
                if (PlayerControl.AllPlayerControls.Contains(jogador))
                    PlayerControl.AllPlayerControls.Remove(jogador);
            }
            catch { }
            try { UnityEngine.Object.Destroy(jogador.gameObject); } catch { }
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

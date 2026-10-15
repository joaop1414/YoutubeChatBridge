using System;
using System.Collections.Generic;
using Hazel;
using InnerNet;

namespace YouTubeVoteBridge;

/// <summary>
/// Porta para o YouTubeVoteBridge o procedimento de associação de PlayerControl
/// falso usado pelo CustomNetObject do EHR para clientes vanilla.
/// </summary>
internal static class CustomNetObject
{
    private const int TamanhoMaximoLote = 500;


    /// <summary>
    /// Envia a criação de um PlayerControl falso no formato usado pelo EHR.
    /// O objeto deve ter sido instanciado, configurado com PlayerId/isNew/notRealPlayer
    /// e posicionado pelo chamador antes desta chamada.
    /// </summary>
    public static void EnviarSpawn(PlayerControl objeto) =>
        EnviarSpawn(objeto, ocultarVisual: true, removerDaLista: true);

    /// <summary>
    /// Variante experimental: permite manter o corpo visível e o PlayerControl
    /// em AllPlayerControls para testar um spawn com aparência de jogador normal.
    /// Isso não cria um cliente/proprietário de rede real.
    /// </summary>
    public static void EnviarSpawn(
        PlayerControl objeto, bool ocultarVisual, bool removerDaLista)
    {
        if (objeto == null || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null || !AmongUsClient.Instance.AmHost)
            throw new InvalidOperationException("Não é possível criar o PlayerControl falso fora do host.");

        // O EHR não nasce o objeto na posição visível. Ele o cria fora da área
        // jogável e só o move depois que os clientes vanilla o associaram.
        // Isso evita o frame em que o prefab aparece como um crewmate normal.
        try { objeto.NetTransform.SnapTo(new UnityEngine.Vector2(50f, 50f)); }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "CustomNetObject: falha ao posicionar o objeto fora da tela antes do spawn: " + ex.Message);
        }

        // Reprodução do EHR: o contador avança exatamente uma posição.
        // O Instantiate já registra os NetObjects do prefab; reservar +7 faz os
        // três spawns auxiliares usarem IDs incorretos e gera colisões.
        uint contadorAntes = AmongUsClient.Instance.NetIdCnt;
        AmongUsClient.Instance.NetIdCnt += 1U;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"CustomNetObject: preparando spawn falso; NetId do PlayerControl={objeto.NetId}, " +
            $"NetIdCnt {contadorAntes} -> {AmongUsClient.Instance.NetIdCnt}; " +
            $"NetIds auxiliares={AmongUsClient.Instance.NetIdCnt - 1U}," +
            $"{AmongUsClient.Instance.NetIdCnt - 2U},{AmongUsClient.Instance.NetIdCnt - 3U}.");

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);

        try
        {
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);

            writer.StartMessage(4);
            var spawn = AmongUsClient.Instance.CreateSpawnMessage(
                objeto, -2, SpawnFlags.None);
            spawn.SerializeValues(writer);
            writer.EndMessage();

            // Não enviar os três LobbyBehaviour auxiliares do EHR nesta build:
            // no Among Us 19.0.0 eles colidem com NetIds já registrados pelo
            // PlayerPrefab (28/27/26 nos logs). O EHR condiciona esse workaround
            // ao tipo de servidor; sem a mesma detecção, enviar sempre é incorreto.

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer = null;
        }
        finally
        {
            writer?.Recycle();
        }

        if (removerDaLista && PlayerControl.AllPlayerControls.Contains(objeto))
            PlayerControl.AllPlayerControls.Remove(objeto);

        if (ocultarVisual)
        {
            // Comportamento padrão para os objetos artificiais atuais:
            // corpo e texto de daltônico ficam invisíveis.
            try
            {
                var corpo = objeto.cosmetics?.currentBodySprite?.BodySprite;
                if (corpo != null)
                    corpo.color = UnityEngine.Color.clear;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "CustomNetObject: não foi possível ocultar o corpo do objeto: " + ex.Message);
            }

            try
            {
                var texto = objeto.cosmetics?.colorBlindText;
                if (texto != null)
                    texto.color = UnityEngine.Color.clear;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "CustomNetObject: não foi possível ocultar o texto de cor: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Envia nome e cor para clientes vanilla depois da associação do PlayerControl.
    /// </summary>
    public static void EnviarAparencia(PlayerControl objeto, string nome, byte corId)
    {
        if (objeto == null || objeto.Data == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            PlayerControl.LocalPlayer?.Data == null)
            throw new InvalidOperationException("Não é possível sincronizar a aparência do PlayerControl falso.");

        objeto.RawSetName(nome);

        var hostData = PlayerControl.LocalPlayer.Data;
        var outfit = hostData.Outfits[PlayerOutfitType.Default];

        string nomeOriginal = outfit.PlayerName;
        int corOriginal = outfit.ColorId;
        string chapeuOriginal = outfit.HatId;
        string skinOriginal = outfit.SkinId;
        string petOriginal = outfit.PetId;
        string visorOriginal = outfit.VisorId;

        var destinatarios = new List<PlayerControl>();
        var proprietariosIncluidos = new HashSet<int>();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (!CompatibilidadeClientes.ClienteRemotoElegivel(
                    jogador, objeto, proprietariosIncluidos))
                continue;
            destinatarios.Add(jogador);
        }

        if (destinatarios.Count == 0)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "CustomNetObject: nenhum cliente vanilla elegível para receber a aparência individual do tsunami.");
            return;
        }

        int enviados = 0;
        try
        {
            // O outfit temporário e o Shapeshift são empacotados separadamente
            // para cada cliente, usando GameDataTo (mensagem 6), em vez de
            // transmitir uma única atualização global.
            foreach (PlayerControl destinatario in destinatarios)
            {
                MessageWriter writer = null;
                try
                {
                    writer = MessageWriter.Get(SendOption.Reliable);
                    writer.StartMessage(26);
                    writer.WritePacked(AmongUsClient.Instance.GameId);

                    writer.StartMessage(6);
                    writer.Write(AmongUsClient.Instance.GameId);
                    writer.WritePacked(destinatario.OwnerId);

                    outfit.PlayerName = "<size=14><br></size>" + nome;
                    outfit.ColorId = 255;
                    outfit.HatId = "";
                    outfit.SkinId = "";
                    outfit.PetId = "";
                    outfit.VisorId = "";

                    writer.StartMessage(1);
                    writer.WritePacked(hostData.NetId);
                    hostData.Serialize(writer, false);
                    writer.EndMessage();

                    writer.StartMessage(2);
                    writer.WritePacked(objeto.NetId);
                    writer.Write((byte)RpcCalls.Shapeshift);
                    writer.WriteNetObject(PlayerControl.LocalPlayer);
                    writer.Write(false);
                    writer.EndMessage();

                    outfit.PlayerName = nomeOriginal;
                    outfit.ColorId = corOriginal;
                    outfit.HatId = chapeuOriginal;
                    outfit.SkinId = skinOriginal;
                    outfit.PetId = petOriginal;
                    outfit.VisorId = visorOriginal;

                    writer.EndMessage(); // GameDataTo (destinatário)
                    writer.EndMessage(); // GameData
                    AmongUsClient.Instance.SendOrDisconnect(writer);
                    writer = null;
                    enviados++;
                }
                finally
                {
                    writer?.Recycle();
                    // Restaura antes de preparar o próximo cliente, mesmo se houver erro.
                    outfit.PlayerName = nomeOriginal;
                    outfit.ColorId = corOriginal;
                    outfit.HatId = chapeuOriginal;
                    outfit.SkinId = skinOriginal;
                    outfit.PetId = petOriginal;
                    outfit.VisorId = visorOriginal;
                }
            }
        }
        finally
        {
            outfit.PlayerName = nomeOriginal;
            outfit.ColorId = corOriginal;
            outfit.HatId = chapeuOriginal;
            outfit.SkinId = skinOriginal;
            outfit.PetId = petOriginal;
            outfit.VisorId = visorOriginal;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"CustomNetObject: aparência individual enviada para {enviados}/{destinatarios.Count} clientes; " +
            $"PlayerId={objeto.PlayerId}, nome='{nome}', cor solicitada={corId}.");
    }

    /// <summary>
    /// Replica a atualização de posição no padrão usado pelo CustomNetObject do EHR.
    /// </summary>
    public static void EnviarPosicao(PlayerControl objeto, UnityEngine.Vector2 posicao)
    {
        if (objeto == null || objeto.NetTransform == null ||
            AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost)
            return;

        try
        {
            objeto.NetTransform.SnapTo(
                posicao, (ushort)(objeto.NetTransform.lastSequenceId + 1U));
            ushort sequencia = (ushort)(objeto.NetTransform.lastSequenceId + 2U);
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                objeto.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.None);
            NetHelpers.WriteVector2(posicao, writer);
            writer.Write(sequencia);
            AmongUsClient.Instance.FinishRpcImmediately(writer);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "CustomNetObject: falha ao sincronizar posição: " + ex.Message);
        }
    }

    public static int AssociarClientes(PlayerControl objeto, byte playerIdReservado)
    {
        if (objeto == null || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null || !AmongUsClient.Instance.AmHost)
            return 0;

        var destinatarios = new List<PlayerControl>();
        var proprietariosIncluidos = new HashSet<int>();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (!CompatibilidadeClientes.ClienteRemotoElegivel(
                    jogador, objeto, proprietariosIncluidos))
                continue;

            destinatarios.Add(jogador);
        }

        if (destinatarios.Count == 0)
        {
            objeto.CachedPlayerData = PlayerControl.LocalPlayer.Data;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "CustomNetObject: nenhum cliente remoto elegível para associação.");
            return 0;
        }

        int enviados = 0;
        int mensagensNoLote = 0;
        MessageWriter writer = null;

        try
        {
            writer = NovoLote();

            foreach (PlayerControl jogador in destinatarios)
            {
                // O EHR coloca as mensagens direcionadas (6) dentro de uma
                // mensagem GameData (26); não envia a mensagem 6 isoladamente.
                if (mensagensNoLote > 0 && writer.Length > TamanhoMaximoLote)
                {
                    FecharEnviarLote(writer);
                    writer = NovoLote();
                    mensagensNoLote = 0;
                }

                writer.StartMessage(6);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.WritePacked(jogador.OwnerId);

                // Temporariamente associa o objeto falso ao PlayerId do cliente.
                writer.StartMessage(1);
                writer.WritePacked(objeto.NetId);
                writer.Write(jogador.PlayerId);
                writer.EndMessage();

                // Sequência do EHR: MurderPlayer com FailedError inicializa
                // o PlayerControl remoto sem efetivamente matar o objeto.
                writer.StartMessage(2);
                writer.WritePacked(objeto.NetId);
                writer.Write((byte)RpcCalls.MurderPlayer);
                writer.WriteNetObject(objeto);
                writer.Write((int)MurderResultFlags.FailedError);
                writer.EndMessage();

                // Restaura o PlayerId reservado para o objeto falso.
                writer.StartMessage(1);
                writer.WritePacked(objeto.NetId);
                writer.Write(playerIdReservado);
                writer.EndMessage();

                writer.EndMessage();
                mensagensNoLote++;
                enviados++;
            }

            if (mensagensNoLote > 0)
            {
                FecharEnviarLote(writer);
                writer = null;
            }

            objeto.CachedPlayerData = PlayerControl.LocalPlayer.Data;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Compatibilidade: associação do fake NetId={objeto.NetId}, PlayerId={objeto.PlayerId}, " +
                $"enviada individualmente para {enviados} cliente(s) remoto(s) elegível(is).");
            return enviados;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "CustomNetObject: falha ao enviar associação no formato EHR: " + ex);
            return enviados;
        }
        finally
        {
            writer?.Recycle();
        }
    }

    private static MessageWriter NovoLote()
    {
        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        writer.StartMessage(26);
        writer.WritePacked(AmongUsClient.Instance.GameId);
        return writer;
    }

    private static void FecharEnviarLote(MessageWriter writer)
    {
        writer.EndMessage();
        AmongUsClient.Instance.SendOrDisconnect(writer);
        writer.Recycle();
    }
}

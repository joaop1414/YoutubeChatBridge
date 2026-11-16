using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Hazel;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Mostra uma etiqueta abaixo dos personagens dos jogadores que possuem o mod.
/// A lista é enviada pelo host somente aos clientes que confirmaram o mod.
/// </summary>
internal static class LobbyModLabelManager
{
    private const byte RpcListaJogadoresComMod = 249;
    private const string NomeObjeto = "YouTubeVoteBridge_ModLabel";
    private static readonly HashSet<byte> JogadoresComMod = new();
    private static readonly Dictionary<byte, TextMeshPro> Etiquetas = new();
    private static float proximaSincronizacao;

    internal static void SincronizarComClientes()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null)
            return;

        List<byte> ids = ObterIdsComMod();

        // O host também aplica a lista localmente, sem precisar enviar RPC para si.
        AplicarLista(ids);

        foreach (int clientId in ObterClientesComMod())
        {
            if (clientId < 0 || clientId == AmongUsClient.Instance.ClientId)
                continue;

            try
            {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    PlayerControl.LocalPlayer.NetId,
                    RpcListaJogadoresComMod,
                    SendOption.Reliable,
                    clientId);

                writer.Write((byte)Math.Min(ids.Count, byte.MaxValue));
                foreach (byte playerId in ids)
                    writer.Write(playerId);

                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Etiqueta mod] Falha ao sincronizar com ClientId=" +
                    clientId + ": " + ex.Message);
            }
        }

        proximaSincronizacao = Time.realtimeSinceStartup + 2.5f;
    }

    internal static void ReceberLista(MessageReader reader)
    {
        if (reader == null || YouTubeVoteBridgePlugin.EhHost())
            return;

        try
        {
            int quantidade = reader.ReadByte();
            List<byte> ids = new(quantidade);
            for (int i = 0; i < quantidade; i++)
                ids.Add(reader.ReadByte());

            AplicarLista(ids);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Etiqueta mod] Lista recebida inválida: " + ex.Message);
        }
    }

    internal static void Atualizar()
    {
        if (!EstaNoLobbyLocal())
        {
            DestruirEtiquetas();
            JogadoresComMod.Clear();
            return;
        }

        // Reenvia periodicamente para atualizar a lista quando alguém sai da sala.
        if (YouTubeVoteBridgePlugin.EhHost() &&
            Time.realtimeSinceStartup >= proximaSincronizacao)
            SincronizarComClientes();

        if (PlayerControl.AllPlayerControls == null)
            return;

        HashSet<byte> presentes = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.notRealPlayer)
                continue;

            presentes.Add(jogador.PlayerId);

            if (JogadoresComMod.Contains(jogador.PlayerId))
                CriarOuAtualizarEtiqueta(jogador);
            else
                RemoverEtiqueta(jogador.PlayerId);
        }

        foreach (byte id in Etiquetas.Keys.Where(id => !presentes.Contains(id)).ToArray())
            RemoverEtiqueta(id);
    }

    private static List<byte> ObterIdsComMod()
    {
        List<byte> ids = new();
        if (PlayerControl.AllPlayerControls == null)
            return ids;

        int clientIdLocal = AmongUsClient.Instance.ClientId;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.notRealPlayer)
                continue;

            if (jogador.OwnerId == clientIdLocal ||
                YouTubeVoteBridgePlugin.ClienteTemMod(jogador.OwnerId))
                ids.Add(jogador.PlayerId);
        }

        return ids.Distinct().Take(byte.MaxValue).ToList();
    }

    private static int[] ObterClientesComMod()
    {
        // A lista é obtida pelo método público já usado pelo sistema de sincronização.
        return LobbyModClientesSnapshot.Obter();
    }

    private static void AplicarLista(IEnumerable<byte> ids)
    {
        JogadoresComMod.Clear();
        foreach (byte id in ids)
            JogadoresComMod.Add(id);

        Atualizar();
    }

    private static bool EstaNoLobbyLocal()
    {
        try
        {
            if (AmongUsClient.Instance == null ||
                PlayerControl.LocalPlayer == null ||
                ShipStatus.Instance != null)
                return false;

            Type tipo = AmongUsClient.Instance.GetType();
            object estado = tipo.GetProperty(
                "GameState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(AmongUsClient.Instance);

            if (estado == null)
                estado = tipo.GetField(
                    "GameState", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(AmongUsClient.Instance);

            string nome = estado?.ToString() ?? string.Empty;
            return string.Equals(nome, "Joined", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(nome, "Lobby", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void CriarOuAtualizarEtiqueta(PlayerControl jogador)
    {
        try
        {
            if (jogador?.cosmetics?.nameText == null)
                return;

            if (!Etiquetas.TryGetValue(jogador.PlayerId, out TextMeshPro etiqueta) ||
                etiqueta == null)
            {
                TextMeshPro modelo = jogador.cosmetics.nameText;
                etiqueta = UnityEngine.Object.Instantiate(
                    modelo, modelo.transform.parent, false);

                etiqueta.name = NomeObjeto + "_" + jogador.PlayerId;
                etiqueta.alignment = TextAlignmentOptions.Center;
                etiqueta.enableWordWrapping = false;
                etiqueta.overflowMode = TextOverflowModes.Overflow;
                etiqueta.fontSize = modelo.fontSize * 0.68f;
                etiqueta.color = new Color(0.35f, 0.92f, 1f, 1f);
                etiqueta.text = "Jogador com o mod";

                Vector3 posicao = modelo.transform.localPosition;
                etiqueta.transform.localPosition = posicao + new Vector3(0f, -0.82f, -0.01f);
                etiqueta.transform.localRotation = modelo.transform.localRotation;
                etiqueta.transform.localScale = modelo.transform.localScale;

                Etiquetas[jogador.PlayerId] = etiqueta;
            }
            else if (etiqueta.text != "Jogador com o mod")
            {
                etiqueta.text = "Jogador com o mod";
            }

            etiqueta.gameObject.SetActive(true);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[Etiqueta mod] Falha ao criar etiqueta para PlayerId=" +
                jogador.PlayerId + ": " + ex.Message);
        }
    }

    private static void RemoverEtiqueta(byte playerId)
    {
        if (!Etiquetas.TryGetValue(playerId, out TextMeshPro etiqueta))
            return;

        Etiquetas.Remove(playerId);
        if (etiqueta != null)
            UnityEngine.Object.Destroy(etiqueta.gameObject);
    }

    private static void DestruirEtiquetas()
    {
        foreach (TextMeshPro etiqueta in Etiquetas.Values)
            if (etiqueta != null)
                UnityEngine.Object.Destroy(etiqueta.gameObject);

        Etiquetas.Clear();
    }
}

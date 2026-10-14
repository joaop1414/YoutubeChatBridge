using System;
using System.Collections.Generic;
using Hazel;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Sincroniza o rótulo visual do NPC com os clientes que possuem o mod.
/// O nome é aplicado apenas ao texto local, sem SetName/RPC de identidade.
/// </summary>
internal static class NomeNpcRpc
{
    private const byte RpcNomeNpcLocal = 251;
    private static readonly Dictionary<uint, string> NomesPorNetId = new();
    private static readonly Dictionary<uint, PlayerControl> ReferenciasLocais = new();

    public static void Publicar(PlayerControl npc, string nome)
    {
        if (npc == null || AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.AmHost || PlayerControl.LocalPlayer == null)
            return;

        nome = string.IsNullOrWhiteSpace(nome) ? "NPC" : nome.Trim();
        NomesPorNetId[npc.NetId] = nome;
        ReferenciasLocais[npc.NetId] = npc;
        Aplicar(npc, nome);

        try
        {
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                PlayerControl.LocalPlayer.NetId,
                RpcNomeNpcLocal,
                SendOption.Reliable,
                -1);
            writer.WritePacked(npc.NetId);
            writer.Write(nome);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"NomeNpcRpc: enviado rótulo local '{nome}' para NPC NetId={npc.NetId}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NomeNpcRpc: falha ao enviar o nome local: " + ex.Message);
        }
    }

    public static void Receber(MessageReader reader)
    {
        try
        {
            uint netId = reader.ReadPackedUInt32();
            string nome = reader.ReadString();
            if (string.IsNullOrWhiteSpace(nome))
                nome = "NPC";

            NomesPorNetId[netId] = nome;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"NomeNpcRpc: recebido rótulo local '{nome}' para NPC NetId={netId}.");
            Atualizar();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "NomeNpcRpc: pacote de nome inválido: " + ex.Message);
        }
    }

    public static void Atualizar()
    {
        if (NomesPorNetId.Count == 0)
            return;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || !NomesPorNetId.TryGetValue(jogador.NetId, out string nome))
                continue;

            Aplicar(jogador, nome);
        }

        // O host remove o NPC de AllPlayerControls para que ele não seja tratado
        // como um jogador real, então também atualizamos a referência guardada.
        foreach (var item in ReferenciasLocais)
        {
            if (item.Value != null && NomesPorNetId.TryGetValue(item.Key, out string nome))
                Aplicar(item.Value, nome);
        }
    }

    private static void Aplicar(PlayerControl jogador, string nome)
    {
        try
        {
            if (jogador == null || jogador.cosmetics == null ||
                jogador.cosmetics.nameText == null)
                return;

            if (jogador.cosmetics.nameText.text != nome)
                jogador.cosmetics.nameText.text = nome;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "NomeNpcRpc: ainda não foi possível aplicar o texto local: " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class NomeNpcRpcPatch
{
    private const byte RpcNomeNpcLocal = 251;

    public static bool Prefix(byte callId, MessageReader reader)
    {
        if (callId != RpcNomeNpcLocal)
            return true;

        NomeNpcRpc.Receber(reader);
        return false;
    }
}

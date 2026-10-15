using System;
using System.Collections;
using System.Collections.Generic;
using AmongUs.GameOptions;
#if IL2CPP
using BepInEx.Unity.IL2CPP.Utils.Collections;
#endif
using HarmonyLib;
using Hazel;
using InnerNet;
using UnityEngine;

namespace YouTubeVoteBridge;

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.CoStartGameHost))]
internal static class SelecaoInicialDeFuncoesPatch
{
#if IL2CPP
    [HarmonyPrefix]
    private static bool Prefix(AmongUsClient __instance, ref Il2CppSystem.Collections.IEnumerator __result)
    {
        if (!__instance.AmHost) return true;
        __result = Iniciar(__instance).WrapToIl2Cpp();
        return false;
    }
#else
    [HarmonyPrefix]
    private static bool Prefix(AmongUsClient __instance, ref IEnumerator __result)
    {
        if (!__instance.AmHost) return true;
        __result = Iniciar(__instance);
        return false;
    }
#endif

    private static IEnumerator Iniciar(AmongUsClient client)
    {
        var loading = LoadingBarManager.Instance;
        try
        {
            loading.ToggleLoadingBar(true);
            loading.SetLoadingPercent(0f, StringNames.LoadingBarGameStart);
        }
        catch (Exception ex) { YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[FuncoesIniciais] Loading UI: " + ex.Message); }

        if (LobbyBehaviour.Instance == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("[FuncoesIniciais] LobbyBehaviour nulo.");
            yield break;
        }

        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        writer.StartMessage(5);
        writer.Write(client.GameId);
        writer.StartMessage(5);
        writer.WritePacked(LobbyBehaviour.Instance.NetId);
        writer.EndMessage();
        writer.EndMessage();
        client.SendOrDisconnect(writer);
        writer.Recycle();
        client.RemoveNetObject(LobbyBehaviour.Instance);
        UnityEngine.Object.Destroy(LobbyBehaviour.Instance.gameObject);

        if (ShipStatus.Instance == null)
        {
            int map = Mathf.Clamp(GameOptionsManager.Instance.CurrentGameOptions.MapId, 0, Constants.MapNames.Length - 1);
            client.ShipLoadingAsyncHandle = client.ShipPrefabs[map].InstantiateAsync();
            while (!client.ShipLoadingAsyncHandle.IsDone)
            {
                try { loading.SetLoadingPercent(Mathf.Lerp(0f, 10f, client.ShipLoadingAsyncHandle.PercentComplete), StringNames.LoadingBarGameStart); }
                catch { }
                yield return null;
            }
            ShipStatus.Instance = client.ShipLoadingAsyncHandle.Result.GetComponent<ShipStatus>();
            client.Spawn(ShipStatus.Instance);
        }

        DateTime start = DateTime.Now;
        while (true)
        {
            bool ready = true;
            int limit = GameOptionsManager.Instance.CurrentGameOptions.MapId is 4 or 5 ? 15 : 10;
            float elapsed = (float)(DateTime.Now - start).TotalSeconds;
            lock (client.allClients)
            {
                for (int i = 0; i < client.allClients.Count; i++)
                {
                    ClientData data = client.allClients[i];
                    if (data.Id != client.ClientId && !data.IsReady)
                    {
                        if (elapsed < limit) ready = false;
                        else
                        {
                            client.SendLateRejection(data.Id, DisconnectReasons.ClientTimeout);
                            data.IsReady = true;
                            client.OnPlayerLeft(data, DisconnectReasons.ClientTimeout);
                        }
                    }
                }
            }
            try { loading.SetLoadingPercent(10f + Mathf.Clamp01(elapsed / limit) * 80f, StringNames.LoadingBarGameStartWaitingPlayers); }
            catch { }
            if (!ready) yield return new WaitForEndOfFrame();
            else break;
        }

        client.SendClientReady();
        AtribuirFuncoes();
        try { loading.SetLoadingPercent(100f, StringNames.LoadingBarGameStart); }
        catch { }
    }

    private static void AtribuirFuncoes()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        var options = GameOptionsManager.Instance?.CurrentGameOptions;
        if (options == null) return;

        List<PlayerControl> players = new();
        foreach (PlayerControl player in PlayerControl.AllPlayerControls)
            if (player != null && player.Data != null && !player.Data.Disconnected) players.Add(player);
        if (players.Count == 0) return;

        int impostors = Math.Clamp(options.NumImpostors, 0, Math.Max(0, players.Count - 1));
        for (int i = players.Count - 1; i > 0; i--)
        {
            int j = System.Random.Shared.Next(i + 1);
            (players[i], players[j]) = (players[j], players[i]);
        }

        for (int i = 0; i < players.Count; i++)
            RoleSwitchAPI.TrocarFuncao(players[i], i < impostors ? RoleTypes.Impostor : RoleTypes.Crewmate);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo($"[FuncoesIniciais] Mod atribuiu {impostors} impostor(es) e {players.Count - impostors} tripulante(s).");
    }
}

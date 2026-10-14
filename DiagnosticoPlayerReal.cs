using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Hazel;

namespace YouTubeVoteBridge;

/// <summary>
/// Diagnóstico temporário para capturar a criação e os RPCs de jogadores reais.
/// No jogo, pressione F8 para iniciar/parar a coleta. Os dados vão para o LogOutput.log.
/// </summary>
internal static class DiagnosticoPlayerReal
{
    private static bool ativo;
    private static float proximoSnapshot;
    private static int snapshots;
    private static readonly HashSet<string> metodosDumpados = new();

    public static void Atualizar()
    {
        try
        {
            if (Input.GetKeyDown(KeyCode.F8))
            {
                ativo = !ativo;
                proximoSnapshot = 0f;
                snapshots = 0;
                metodosDumpados.Clear();
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[DIAG-PLAYER-REAL] Coleta " + (ativo ? "INICIADA" : "ENCERRADA") +
                    ". " + (ativo
                        ? "Reproduza o problema e depois pressione F8 novamente."
                        : "Envie o LogOutput.log para análise."));
            }

            if (!ativo || Time.realtimeSinceStartup < proximoSnapshot)
                return;

            proximoSnapshot = Time.realtimeSinceStartup + 0.5f;
            snapshots++;
            CapturarJogadores("SNAPSHOT-" + snapshots);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[DIAG-PLAYER-REAL] Erro no Update: " + ex);
        }
    }

    public static bool Ativo => ativo;

    public static void RegistrarRpc(PlayerControl jogador, byte callId)
    {
        if (!ativo)
            return;

        try
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[DIAG-PLAYER-REAL][RPC] " +
                "CallId=" + callId +
                "; AlvoNetId=" + Seguro(() => jogador?.NetId.ToString()) +
                "; PlayerId=" + Seguro(() => jogador?.PlayerId.ToString()) +
                "; NomeData='" + Seguro(() => jogador?.Data?.PlayerName) +
                "'; OwnerId=" + Seguro(() => jogador?.OwnerId.ToString()) +
                "; IsHost=" + (AmongUsClient.Instance?.AmHost.ToString() ?? "null") + ".");
        }
        catch { }
    }

    public static void RegistrarJogadorEntrou(object cliente)
    {
        if (!ativo || cliente == null)
            return;

        try
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[DIAG-PLAYER-REAL][PLAYER-JOINED] " + DescreverObjeto(cliente));
            CapturarJogadores("PLAYER-JOINED");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[DIAG-PLAYER-REAL] Falha ao registrar entrada: " + ex.Message);
        }
    }

    private static void CapturarJogadores(string etapa)
    {
        try
        {
            var cliente = AmongUsClient.Instance;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[DIAG-PLAYER-REAL][" + etapa + "][SESSION] " +
                "AmHost=" + (cliente?.AmHost.ToString() ?? "null") +
                "; GameId=" + Seguro(() => cliente?.GameId.ToString()) +
                "; NetIdCnt=" + Seguro(() => cliente?.NetIdCnt.ToString()) +
                "; LocalPlayerNetId=" + Seguro(() => PlayerControl.LocalPlayer?.NetId.ToString()) +
                "; LocalPlayerId=" + Seguro(() => PlayerControl.LocalPlayer?.PlayerId.ToString()) +
                "; AllPlayerControls=" + (PlayerControl.AllPlayerControls?.Count ?? -1) + ".");

            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            {
                if (p == null) continue;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[DIAG-PLAYER-REAL][" + etapa + "][PLAYER] " + DescreverJogador(p));

                if (p == PlayerControl.LocalPlayer)
                    DumparMetadadosTipo(p.GetType(), "PlayerControl");

                if (p.Data != null && p == PlayerControl.LocalPlayer)
                    DumparMetadadosTipo(p.Data.GetType(), "NetworkedPlayerInfo");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[DIAG-PLAYER-REAL] Falha no snapshot: " + ex);
        }
    }

    private static string DescreverJogador(PlayerControl p)
    {
        string nomeVisual = "<indisponível>";
        string posicao = "<indisponível>";
        string outfit = "<indisponível>";
        try { nomeVisual = p.cosmetics?.nameText?.text ?? "<null>"; } catch { }
        try { posicao = p.transform != null ? p.transform.position.ToString("F3") : "<null>"; } catch { }
        try
        {
            var o = p.Data?.DefaultOutfit;
            if (o != null)
                outfit = "PlayerName='" + o.PlayerName + "',ColorId=" + o.ColorId +
                    ",HatId='" + o.HatId + "',SkinId='" + o.SkinId +
                    "',PetId='" + o.PetId + "',VisorId='" + o.VisorId + "'";
        }
        catch { }

        return "NetId=" + Seguro(() => p.NetId.ToString()) +
            "; DataNetId=" + Seguro(() => p.Data?.NetId.ToString()) +
            "; PlayerId=" + Seguro(() => p.PlayerId.ToString()) +
            "; OwnerId=" + Seguro(() => p.OwnerId.ToString()) +
            "; DataName='" + Seguro(() => p.Data?.PlayerName) +
            "'; VisualName='" + nomeVisual +
            "'; IsDead=" + Seguro(() => p.Data?.IsDead.ToString()) +
            "; Disconnected=" + Seguro(() => p.Data?.Disconnected.ToString()) +
            "; isNew=" + Seguro(() => p.isNew.ToString()) +
            "; notRealPlayer=" + Seguro(() => p.notRealPlayer.ToString()) +
            "; CachedPlayerDataNetId=" + Seguro(() => p.CachedPlayerData?.NetId.ToString()) +
            "; Position=" + posicao +
            "; Outfit={" + outfit + "}.";
    }

    private static string DescreverObjeto(object obj)
    {
        var partes = new List<string>();
        Type t = obj.GetType();
        foreach (string nome in new[] { "Id", "PlayerName", "ClientId", "OwnerId", "PlayerId", "NetId", "Name" })
        {
            try
            {
                PropertyInfo prop = t.GetProperty(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo field = t.GetField(nome, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object valor = prop != null ? prop.GetValue(obj) : field?.GetValue(obj);
                if (valor != null) partes.Add(nome + "='" + valor + "'");
            }
            catch { }
        }
        return t.FullName + " { " + string.Join("; ", partes) + " }";
    }

    private static void DumparMetadadosTipo(Type t, string categoria)
    {
        string chave = categoria + ":" + t.FullName;
        if (!metodosDumpados.Add(chave)) return;

        try
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[DIAG-PLAYER-REAL][TYPE] " + categoria + "=" + t.AssemblyQualifiedName);

            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;
                string n = m.Name;
                if (n.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Outfit", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Player", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Rpc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Serialize", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Shapeshift", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Start", StringComparison.OrdinalIgnoreCase) >= 0)
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[DIAG-PLAYER-REAL][METHOD] " + categoria + ": " + Assinatura(m));
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "[DIAG-PLAYER-REAL] Metadados indisponíveis: " + ex.Message);
        }
    }

    private static string Assinatura(MethodInfo m)
    {
        try
        {
            var p = m.GetParameters();
            var nomes = new string[p.Length];
            for (int i = 0; i < p.Length; i++)
                nomes[i] = p[i].ParameterType.Name + " " + p[i].Name;
            return m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", nomes) + ")";
        }
        catch { return m.Name; }
    }

    private static string Seguro(Func<string> obter)
    {
        try { return obter() ?? "<null>"; }
        catch (Exception ex) { return "<erro:" + ex.GetType().Name + ">"; }
    }
}

[HarmonyPatch]
internal static class DiagnosticoPlayerRealChamadasPatch
{
    private static readonly string[] MetodosObservados =
    {
        "Awake", "Start", "Deserialize", "SetName", "RpcSetName",
        "SetColor", "RpcSetColor", "Shapeshift", "Serialize",
        "SetPlayerName", "SetOutfit", "HandleRpc"
    };

    public static IEnumerable<MethodBase> TargetMethods()
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (MethodInfo metodo in typeof(PlayerControl).GetMethods(flags))
        {
            if (Array.IndexOf(MetodosObservados, metodo.Name) >= 0)
                yield return metodo;
        }
    }

    public static void Prefix(object __instance, object[] __args, MethodBase __originalMethod)
    {
        if (!DiagnosticoPlayerReal.Ativo)
            return;

        try
        {
            var args = new List<string>();
            if (__args != null)
            {
                foreach (object arg in __args)
                {
                    if (arg == null) args.Add("null");
                    else if (arg is string || arg.GetType().IsPrimitive || arg.GetType().IsEnum)
                        args.Add(arg.GetType().Name + "=" + arg);
                    else
                        args.Add(arg.GetType().FullName);
                }
            }

            string identificacao = __instance is PlayerControl p
                ? " PlayerId=" + p.PlayerId + "; NetId=" + p.NetId +
                  "; DataName='" + (p.Data?.PlayerName ?? "<null>") + "'"
                : "";

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[DIAG-PLAYER-REAL][CALL] " + __originalMethod.DeclaringType?.FullName +
                "." + __originalMethod.Name + "(" + string.Join(", ", args) + ")" +
                ";" + identificacao);
        }
        catch { }
    }
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.HandleRpc))]
internal static class DiagnosticoPlayerRealRpcPatch
{
    public static void Prefix(PlayerControl __instance, byte callId)
    {
        DiagnosticoPlayerReal.RegistrarRpc(__instance, callId);
    }
}

[HarmonyPatch(typeof(AmongUsClient), nameof(AmongUsClient.OnPlayerJoined))]
internal static class DiagnosticoPlayerRealJoinedPatch
{
    public static void Postfix(object __0)
    {
        DiagnosticoPlayerReal.RegistrarJogadorEntrou(__0);
    }
}

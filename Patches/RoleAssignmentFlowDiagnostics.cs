using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Diagnóstico passivo da atribuição vanilla. Não altera listas, funções,
/// argumentos, RPCs ou coroutines; apenas registra o fluxo real do jogo.
/// </summary>
[HarmonyPatch]
internal static class YouTubeVoteRoleAssignmentFlowDiagnostics
{
    private static readonly BindingFlags Flags =
        BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic;

    private static IEnumerable<MethodBase> TargetMethods()
    {
        Type selector = AccessTools.TypeByName("LogicRoleSelectionNormal");
        if (selector != null)
        {
            foreach (MethodInfo method in selector.GetMethods(Flags))
            {
                if (method.Name == "AssignRolesFromList")
                    yield return method;
            }
        }

        Type player = typeof(PlayerControl);
        foreach (string name in new[] { "SetRole", "CoSetRole", "RpcSetRole" })
        {
            foreach (MethodInfo method in player.GetMethods(Flags))
            {
                if (method.Name == name)
                    yield return method;
            }
        }

        Type intro = typeof(IntroCutscene);
        foreach (string name in new[] { "BeginCrewmate", "BeginImpostor", "ShowRole", "ShowTeam" })
        {
            foreach (MethodInfo method in intro.GetMethods(Flags))
            {
                if (method.Name == name)
                    yield return method;
            }
        }
    }

    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            string nome = __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name;

            if (__originalMethod?.Name == "AssignRolesFromList")
            {
                Log($"ENTER {nome} | {FormatRoleSelectionArguments(__args)}");
                return;
            }

            if (__originalMethod?.DeclaringType == typeof(PlayerControl))
            {
                PlayerControl player = __args == null ? null :
                    __args.OfType<PlayerControl>().FirstOrDefault();

                // Em métodos de instância, __instance é registrado no Postfix.
                Log($"ENTER {nome} | args=[{FormatArguments(__args)}]");
                return;
            }

            Log($"ENTER {nome} | args=[{FormatArguments(__args)}]");
        }
        catch (Exception ex)
        {
            Log("Erro no Prefix do diagnóstico: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Postfix(
        MethodBase __originalMethod,
        object __instance,
        object[] __args,
        object __result)
    {
        try
        {
            string nome = __originalMethod?.DeclaringType?.Name + "." + __originalMethod?.Name;

            if (__originalMethod?.Name == "AssignRolesFromList")
            {
                Log($"EXIT {nome} | {FormatRoleSelectionArguments(__args)}");
                return;
            }

            if (__instance is PlayerControl player)
            {
                Log($"EXIT {nome} | {DescribePlayer(player)} | resultType={__result?.GetType().FullName ?? "null"}");
                return;
            }

            if (__instance is IntroCutscene)
            {
                Log($"EXIT {nome} | resultType={__result?.GetType().FullName ?? "null"}");
                return;
            }

            Log($"EXIT {nome} | resultType={__result?.GetType().FullName ?? "null"}");
        }
        catch (Exception ex)
        {
            Log("Erro no Postfix do diagnóstico: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static string FormatRoleSelectionArguments(object[] args)
    {
        if (args == null)
            return "args=null";

        var partes = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            object arg = args[i];
            string nome = i switch
            {
                0 => "players",
                1 => "roleList",
                2 => "rolesAssigned",
                _ => "arg" + i
            };

            partes.Add(nome + "=" + FormatCollection(arg));
        }

        return string.Join(" | ", partes);
    }

    private static string FormatArguments(object[] args)
    {
        if (args == null)
            return "null";

        return string.Join(", ", args.Select((arg, index) =>
            index + ":" + (arg == null ? "null" : arg.GetType().Name + "=" + SafeString(arg))));
    }

    private static string FormatCollection(object value)
    {
        if (value == null)
            return "null";

        try
        {
            int? count = ReadInt(value, "Count");
            if (count.HasValue)
            {
                var itens = new List<string>();
                PropertyInfo itemProperty = value.GetType().GetProperty("Item", Flags);
                MethodInfo getItem = itemProperty?.GetGetMethod(true);

                if (getItem != null)
                {
                    int limite = Math.Min(count.Value, 32);
                    for (int i = 0; i < limite; i++)
                    {
                        object item = getItem.Invoke(value, new object[] { i });
                        itens.Add(DescribeCollectionItem(item));
                    }

                    if (count.Value > limite)
                        itens.Add("... +" + (count.Value - limite));
                }

                return $"Count={count.Value} [{string.Join(", ", itens)}]";
            }
        }
        catch (Exception ex)
        {
            return value.GetType().Name + " <erro ao enumerar: " + ex.GetType().Name + ">";
        }

        return value.GetType().FullName + "=" + SafeString(value);
    }

    private static string DescribeCollectionItem(object item)
    {
        if (item == null)
            return "null";

        object playerId = ReadMember(item, "PlayerId");
        object playerName = ReadMember(item, "PlayerName");
        object dataRole = ReadMember(item, "Role");

        if (playerId != null || playerName != null)
            return $"PlayerId={SafeString(playerId)}, Name={SafeString(playerName)}, Role={SafeString(dataRole)}";

        return SafeString(item);
    }

    private static string DescribePlayer(PlayerControl player)
    {
        if (player == null)
            return "player=null";

        object data = ReadMember(player, "Data");
        object role = ReadMember(data, "Role");
        object roleType = ReadMember(role, "Role");
        object isImpostor = ReadMember(role, "IsImpostor");

        return $"playerId={SafeString(ReadMember(player, "PlayerId"))}, " +
               $"ownerId={SafeString(ReadMember(player, "OwnerId"))}, " +
               $"name={SafeString(ReadMember(data, "PlayerName"))}, " +
               $"dataRole={SafeString(roleType)}, isImpostor={SafeString(isImpostor)}, " +
               $"dataRoleType={role?.GetType().FullName ?? "null"}";
    }

    private static object ReadMember(object instance, string name)
    {
        if (instance == null)
            return null;

        try
        {
            Type type = instance.GetType();
            PropertyInfo property = type.GetProperty(name, Flags);
            if (property != null)
                return property.GetValue(instance);

            FieldInfo field = type.GetField(name, Flags);
            return field?.GetValue(instance);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadInt(object instance, string name)
    {
        object value = ReadMember(instance, name);
        if (value == null)
            return null;

        try { return Convert.ToInt32(value); }
        catch { return null; }
    }

    private static string SafeString(object value)
    {
        if (value == null)
            return "null";

        try { return value.ToString(); }
        catch { return "<ToString falhou>"; }
    }

    private static void Log(string message)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleFlowDiag] " + message);
    }
}

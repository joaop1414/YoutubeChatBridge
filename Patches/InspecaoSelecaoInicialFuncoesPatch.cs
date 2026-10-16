using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace YouTubeVoteBridge;

/// <summary>
/// Inspeciona a seleção inicial vanilla sem bloquear nem alterar SelectRoles.
/// Registra opções relacionadas a funções e o estado dos jogadores antes/depois.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class InspecaoSelecaoInicialFuncoesPatch
{
    private const string Prefixo = "[RoleSelectInspector] ";

    [HarmonyPrefix]
    private static void AntesDeSelecionar()
    {
        Registrar("ANTES");
    }

    [HarmonyPostfix]
    private static void DepoisDeSelecionar()
    {
        Registrar("DEPOIS");
    }

    private static void Registrar(string etapa)
    {
        try
        {
            var plugin = YouTubeVoteBridgePlugin.Instance;
            if (plugin == null)
                return;

            var client = AmongUsClient.Instance;
            plugin.Log.LogInfo(
                Prefixo + etapa +
                $" SelectRoles; clientId={Tentar(() => client?.ClientId)}; " +
                $"amHost={Tentar(() => client?.AmHost)}; " +
                $"gameState={Tentar(() => AmongUsClient.Instance?.GameState)}");

            var options = GameOptionsManager.Instance?.CurrentGameOptions;
            if (options == null)
            {
                plugin.Log.LogWarning(Prefixo + etapa + " CurrentGameOptions=NULL");
            }
            else
            {
                RegistrarMembrosRelevantes(options, etapa);
            }

            var players = PlayerControl.AllPlayerControls;
            if (players == null)
            {
                plugin.Log.LogWarning(Prefixo + etapa + " AllPlayerControls=NULL");
                return;
            }

            int total = 0;
            foreach (PlayerControl player in players)
            {
                if (player == null)
                {
                    plugin.Log.LogInfo(Prefixo + etapa + " PLAYER=null");
                    continue;
                }

                total++;
                string nome = "(sem PlayerData)";
                string desconectado = "?";
                string morto = "?";
                object data = player.Data;

                if (data != null)
                {
                    nome = Tentar(() => player.Data.PlayerName) ?? "(nome nulo)";
                    desconectado = Tentar(() => player.Data.Disconnected) ?? "?";
                    morto = Tentar(() => player.Data.IsDead) ?? "?";
                }

                string roleData = LerMembro(data, "Role");
                string roleType = LerMembro(data, "RoleType");
                string roleAssigned = LerMembro(data, "roleAssigned", "RoleAssigned");

                plugin.Log.LogInfo(
                    Prefixo + etapa +
                    $" PLAYER playerId={Tentar(() => player.PlayerId)}" +
                    $" ownerId={Tentar(() => player.OwnerId)}" +
                    $" netId={Tentar(() => player.NetId)}" +
                    $" name=\"{nome}\"" +
                    $" disconnected={desconectado} dead={morto}" +
                    $" dataRole={roleData} roleType={roleType} roleAssigned={roleAssigned}");
            }

            plugin.Log.LogInfo(Prefixo + etapa + $" totalEnumerado={total}");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                Prefixo + etapa + " erro durante inspeção: " + ex);
        }
    }

    private static void RegistrarMembrosRelevantes(object options, string etapa)
    {
        var plugin = YouTubeVoteBridgePlugin.Instance;
        if (plugin == null || options == null)
            return;

        Type tipo = options.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var membros = tipo.GetMembers(flags)
            .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property)
            .Where(m =>
            {
                string n = m.Name.ToLowerInvariant();
                return n.Contains("role") || n.Contains("impostor") ||
                       n.Contains("crewmate") || n.Contains("engineer") ||
                       n.Contains("scientist") || n.Contains("shapeshift") ||
                       n.Contains("phantom") || n.Contains("guardian") ||
                       n.Contains("tracker") || n.Contains("noisemaker") ||
                       n.Contains("probab") || n.Contains("count") ||
                       n.Contains("option");
            })
            .OrderBy(m => m.Name);

        foreach (MemberInfo membro in membros)
        {
            try
            {
                object valor = membro switch
                {
                    FieldInfo f => f.GetValue(options),
                    PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(options),
                    _ => null
                };
                plugin.Log.LogInfo(
                    Prefixo + etapa + $" OPTION {tipo.Name}.{membro.Name}={Formatar(valor)}");
            }
            catch (Exception ex)
            {
                plugin.Log.LogInfo(
                    Prefixo + etapa + $" OPTION {tipo.Name}.{membro.Name}=<erro:{ex.GetType().Name}>");
            }
        }
    }

    private static string LerMembro(object alvo, params string[] nomes)
    {
        if (alvo == null)
            return "(null)";

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type tipo = alvo.GetType();

        foreach (string nome in nomes)
        {
            try
            {
                var prop = tipo.GetProperty(nome, flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    return Formatar(prop.GetValue(alvo));

                var campo = tipo.GetField(nome, flags);
                if (campo != null)
                    return Formatar(campo.GetValue(alvo));
            }
            catch (Exception ex)
            {
                return "<erro:" + ex.GetType().Name + ">";
            }
        }

        return "(membro não encontrado)";
    }

    private static string Tentar<T>(Func<T> obter)
    {
        try
        {
            object valor = obter();
            return valor?.ToString() ?? "(null)";
        }
        catch (Exception ex)
        {
            return "<erro:" + ex.GetType().Name + ">";
        }
    }

    private static string Formatar(object valor)
    {
        if (valor == null)
            return "(null)";

        if (valor is string texto)
            return "\"" + texto.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

        return valor.ToString();
    }
}

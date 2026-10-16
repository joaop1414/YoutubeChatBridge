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
                RegistrarColecaoRoleOptions(options, etapa);
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


    // RoleOptions é uma coleção; registrar apenas seu tipo, membros simples e
    // assinaturas de métodos. Não invoca métodos da coleção nem altera opções.
    private static void RegistrarColecaoRoleOptions(object options, string etapa)
    {
        var plugin = YouTubeVoteBridgePlugin.Instance;
        if (plugin == null || options == null)
            return;

        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type tipoOptions = options.GetType();
            object colecao = null;

            var prop = tipoOptions.GetProperty("RoleOptions", flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                colecao = prop.GetValue(options);

            if (colecao == null)
            {
                var campo = tipoOptions.GetField("RoleOptions", flags);
                if (campo != null)
                    colecao = campo.GetValue(options);
            }

            if (colecao == null)
            {
                plugin.Log.LogInfo(Prefixo + etapa + " ROLE_OPTIONS=(null ou não encontrado)");
                return;
            }

            Type tipo = colecao.GetType();
            plugin.Log.LogInfo(Prefixo + etapa + $" ROLE_OPTIONS tipo={tipo.FullName}");

            foreach (MemberInfo membro in tipo.GetMembers(flags)
                .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property)
                .Where(m => m.Name != "EqualityContract" && !m.Name.StartsWith("<"))
                .OrderBy(m => m.Name))
            {
                try
                {
                    object valor = membro switch
                    {
                        FieldInfo f => f.GetValue(colecao),
                        PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(colecao),
                        _ => null
                    };

                    string texto;
                    if (valor == null || valor is string || valor.GetType().IsPrimitive || valor.GetType().IsEnum ||
                        valor is decimal)
                        texto = Formatar(valor);
                    else
                        texto = $"<{valor.GetType().FullName}>";

                    plugin.Log.LogInfo(Prefixo + etapa +
                        $" ROLE_OPTION_MEMBER {tipo.Name}.{membro.Name}={texto}");
                }
                catch (Exception ex)
                {
                    plugin.Log.LogInfo(Prefixo + etapa +
                        $" ROLE_OPTION_MEMBER {tipo.Name}.{membro.Name}=<erro:{ex.GetType().Name}>");
                }
            }

            RegistrarValoresRoleOptions(colecao, etapa);

            foreach (MethodInfo metodo in tipo.GetMethods(flags)
                .Where(m => !m.IsSpecialName && !m.ContainsGenericParameters)
                .OrderBy(m => m.Name))
            {
                string parametros = string.Join(", ", metodo.GetParameters()
                    .Select(p => p.ParameterType.Name + " " + p.Name));
                plugin.Log.LogInfo(Prefixo + etapa +
                    $" ROLE_OPTION_METHOD {metodo.Name}({parametros}) -> {metodo.ReturnType.Name}");
            }
        }
        catch (Exception ex)
        {
            plugin.Log.LogWarning(Prefixo + etapa +
                " erro ao inspecionar RoleOptions: " + ex);
        }
    }

    // Consulta os getters documentados pela própria coleção para cada RoleTypes.
    // São chamadas somente de leitura: não modificam a seleção nem as opções.
    private static void RegistrarValoresRoleOptions(object colecao, string etapa)
    {
        var plugin = YouTubeVoteBridgePlugin.Instance;
        if (plugin == null || colecao == null)
            return;

        try
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            Type tipoColecao = colecao.GetType();

            // Descobre o tipo da função pela assinatura dos getters, evitando
            // depender de um namespace específico de RoleTypes entre versões.
            MethodInfo chanceMethod = tipoColecao.GetMethods(flags)
                .FirstOrDefault(m => m.Name == "GetChancePerGame" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsEnum);
            MethodInfo quantidadeMethod = tipoColecao.GetMethods(flags)
                .FirstOrDefault(m => m.Name == "GetNumPerGame" &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType.IsEnum);

            Type tipoRole = chanceMethod?.GetParameters()[0].ParameterType
                ?? quantidadeMethod?.GetParameters()[0].ParameterType;

            if (tipoRole == null || (!tipoRole.IsEnum))
            {
                plugin.Log.LogInfo(Prefixo + etapa + " ROLE_OPTION_VALUES=getters de enum não encontrados");
                return;
            }

            if (chanceMethod != null &&
                chanceMethod.GetParameters()[0].ParameterType != tipoRole)
                chanceMethod = null;

            if (quantidadeMethod != null &&
                quantidadeMethod.GetParameters()[0].ParameterType != tipoRole)
                quantidadeMethod = null;

            foreach (object role in Enum.GetValues(tipoRole))
            {
                string chance = "(indisponível)";
                string quantidade = "(indisponível)";

                try
                {
                    if (chanceMethod != null)
                        chance = Formatar(chanceMethod.Invoke(colecao, new[] { role }));
                }
                catch (Exception ex)
                {
                    chance = "<erro:" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name) + ">";
                }

                try
                {
                    if (quantidadeMethod != null)
                        quantidade = Formatar(quantidadeMethod.Invoke(colecao, new[] { role }));
                }
                catch (Exception ex)
                {
                    quantidade = "<erro:" + (ex.InnerException?.GetType().Name ?? ex.GetType().Name) + ">";
                }

                plugin.Log.LogInfo(Prefixo + etapa +
                    $" ROLE_OPTION_VALUES role={role} chancePerGame={chance} numPerGame={quantidade}");
            }
        }
        catch (Exception ex)
        {
            plugin.Log.LogWarning(Prefixo + etapa + " erro ao consultar valores de RoleOptions: " + ex);
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

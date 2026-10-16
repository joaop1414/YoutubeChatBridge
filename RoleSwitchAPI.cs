using System;
using System.Reflection;
using System.Linq;
using AmongUs.GameOptions;
using Hazel;

namespace YouTubeVoteBridge;

/// <summary>
/// Troca de função inspirada no CustomRpcSender do EHR:
/// envia SetRole somente ao cliente dono do jogador, com canOverrideRole=true.
/// Se o jogador pertence ao próprio host, aplica localmente com SetRole.
/// </summary>
internal static class RoleSwitchAPI
{
    internal static bool TrocarFuncao(PlayerControl jogador, string nomeFuncao)
    {
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            LogAviso("Troca cancelada: jogador inválido ou desconectado.");
            return false;
        }

        if (!Enum.TryParse(typeof(RoleTypes), nomeFuncao, true, out object valor))
        {
            LogAviso($"RoleTypes não contém a função '{nomeFuncao}'.");
            return false;
        }

        return TrocarFuncao(jogador, (RoleTypes)valor);
    }

    internal static bool TrocarFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
        {
            LogAviso("Troca cancelada: jogador inválido ou desconectado.");
            return false;
        }

        try
        {
            InspecionarEstadoPapel("ANTES_TROCA", jogador, funcao);

            var cliente = AmongUsClient.Instance;
            if (cliente == null)
            {
                LogErro("AmongUsClient.Instance está nulo.");
                return false;
            }

            // Padrão inspirado no Hydra/EHR para o host:
            // 1) atualiza o estado local do jogador no host com RoleManager.SetRole;
            // 2) transmite SetRole para todos os clientes com canOverrideRole=true.
            // Para a aplicação local, usamos CoSetRole(funcao, true), como no EHR.
            // O RPC global continua sendo enviado separadamente pelo host; não
            // chamamos RpcSetRole por método para evitar caminhos recursivos.
            if (cliente.AmHost)
            {
                if (RoleManager.Instance == null)
                {
                    LogErro("RoleManager.Instance está nulo.");
                    return false;
                }

                // Use o mesmo caminho de aplicação local do EHR: CoSetRole com
                // canOverrideRole=true. SetRole direto no RoleManager pode deixar
                // o estado local diferente do papel enviado pelo RPC, especialmente
                // quando o evento troca dois jogadores em sequência.
                jogador.StartCoroutine(jogador.CoSetRole(funcao, true));

                // A mudança para função fantasma também precisa atualizar o botão
                // Reportar no HUD local quando o alvo é o próprio host.
                if (PlayerControl.LocalPlayer != null &&
                    jogador.PlayerId == PlayerControl.LocalPlayer.PlayerId &&
                    HudManager.Instance?.ReportButton != null)
                {
                    HudManager.Instance.ReportButton.gameObject.SetActive(
                        !RoleManager.IsGhostRole(funcao));
                }

                MessageWriter writer = cliente.StartRpcImmediately(
                    jogador.NetId,
                    (byte)RpcCalls.SetRole,
                    SendOption.Reliable,
                    -1);

                writer.Write((ushort)funcao);
                writer.Write(true); // canOverrideRole
                cliente.FinishRpcImmediately(writer);

                InspecionarMetodosPapel(jogador);
                InspecionarEstadoPapel("DEPOIS_RPC_GLOBAL", jogador, funcao);
                LogInfo($"SetRole global enviado pelo host: jogador={jogador.PlayerId}, destino={funcao}, canOverrideRole=true.");
                return true;
            }

            // Para um cliente que não é host, preservamos o comportamento direcionado
            // ao dono do jogador, sem fingir que podemos sincronizar todos os clientes.
            if (cliente.ClientId == jogador.OwnerId)
            {
                if (RoleManager.Instance == null)
                {
                    LogErro("RoleManager.Instance está nulo.");
                    return false;
                }

                RoleManager.Instance.SetRole(jogador, funcao);

                if (PlayerControl.LocalPlayer != null &&
                    jogador.PlayerId == PlayerControl.LocalPlayer.PlayerId &&
                    HudManager.Instance?.ReportButton != null)
                {
                    HudManager.Instance.ReportButton.gameObject.SetActive(
                        !RoleManager.IsGhostRole(funcao));
                }

                InspecionarEstadoPapel("DEPOIS_SETROLE_LOCAL", jogador, funcao);
                LogInfo($"SetRole local aplicado: jogador={jogador.PlayerId}, destino={funcao}.");
                return true;
            }

            LogAviso("Cliente não host não pode atribuir função a outro jogador por esta API.");
            return false;
        }
        catch (Exception ex)
        {
            LogErro($"Exceção ao trocar função do jogador={jogador.PlayerId}: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Tentativa de recuperação baseada no FixBlackScreen do EHR.
    /// O método do EHR é uma extensão estática; procuramos por reflexão para
    /// não adicionar uma dependência obrigatória ao EHR.
    /// </summary>
    internal static void TentarCorrigirTelaPretaTodos()
    {
        try
        {
            Type tipoPlayer = typeof(PlayerControl);
            MethodInfo metodoFix = null;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] tipos;
                try { tipos = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { tipos = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (Type tipo in tipos)
                {
                    MethodInfo candidato = tipo.GetMethod(
                        "FixBlackScreen",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        null,
                        new[] { tipoPlayer },
                        null);

                    if (candidato == null || candidato.ReturnType != typeof(void))
                        continue;

                    metodoFix = candidato;
                    break;
                }

                if (metodoFix != null)
                    break;
            }

            if (metodoFix == null)
            {
                LogAviso("Tentativa FixBlackScreen: método do EHR não está carregado; nenhuma correção externa foi executada.");
                return;
            }

            int tentativas = 0;
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                    continue;

                try
                {
                    metodoFix.Invoke(null, new object[] { jogador });
                    tentativas++;
                    LogInfo($"FixBlackScreen do EHR chamado para PlayerId={jogador.PlayerId}, OwnerId={jogador.OwnerId}.");
                }
                catch (Exception ex)
                {
                    LogAviso($"FixBlackScreen falhou para PlayerId={jogador.PlayerId}: {(ex.InnerException ?? ex).Message}");
                }
            }

            LogInfo($"Tentativa de correção de tela preta concluída; chamadas solicitadas={tentativas}.");
        }
        catch (Exception ex)
        {
            LogErro("Erro na tentativa de recuperação FixBlackScreen: " + ex);
        }
    }

    // Inspetor temporário: apenas registra dados e assinaturas; não invoca métodos extras.
    private static void InspecionarEstadoPapel(string etapa, PlayerControl jogador, RoleTypes destino)
    {
        try
        {
            object roleData = jogador?.Data?.Role;
            LogInfo($"[INSPECTOR][{etapa}] PlayerId={jogador?.PlayerId}, OwnerId={jogador?.OwnerId}, " +
                $"ClientIdLocal={AmongUsClient.Instance?.ClientId}, Destino={destino}, " +
                $"PapelAtual={LerFuncao(jogador)}, RoleDataTipo={roleData?.GetType().FullName ?? "null"}, " +
                $"DataNula={jogador?.Data == null}, Desconectado={jogador?.Data?.Disconnected}, " +
                $"Morto={jogador?.Data?.IsDead}, NetId={jogador?.NetId}");
        }
        catch (Exception ex)
        {
            LogAviso($"[INSPECTOR][{etapa}] Falha ao ler estado: {ex.Message}");
        }
    }

    private static void InspecionarMetodosPapel(PlayerControl jogador)
    {
        try
        {
            Type tipo = jogador?.GetType();
            if (tipo == null)
                return;

            foreach (MethodInfo metodo in tipo.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (metodo.Name.IndexOf("Role", StringComparison.OrdinalIgnoreCase) < 0 &&
                    metodo.Name.IndexOf("Murder", StringComparison.OrdinalIgnoreCase) < 0 &&
                    metodo.Name.IndexOf("BlackScreen", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string parametros = string.Join(", ", metodo.GetParameters().Select(p =>
                    p.ParameterType.Name + " " + p.Name));
                LogInfo($"[INSPECTOR][METHOD] {metodo.DeclaringType?.FullName}.{metodo.Name}({parametros}) -> {metodo.ReturnType.FullName}");
            }
        }
        catch (Exception ex)
        {
            LogAviso("[INSPECTOR] Falha ao enumerar métodos: " + ex.Message);
        }
    }

    private static string LerFuncao(PlayerControl jogador)
    {
        try
        {
            object roleData = jogador?.Data?.Role;
            if (roleData == null)
                return "desconhecida";

            Type tipo = roleData.GetType();
            object papel = tipo.GetProperty("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);
            papel ??= tipo.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);
            return papel?.ToString() ?? "desconhecida";
        }
        catch
        {
            return "indisponível";
        }
    }

    private static void LogInfo(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[RoleSwitchAPI] " + mensagem);

    private static void LogAviso(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[RoleSwitchAPI] " + mensagem);

    private static void LogErro(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogError("[RoleSwitchAPI] " + mensagem);
}

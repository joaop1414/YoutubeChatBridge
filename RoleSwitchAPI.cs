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
            LogInfo($"Solicitando troca direcionada: jogador={jogador.PlayerId}, atual={LerFuncao(jogador)}, destino={funcao}, OwnerId={jogador.OwnerId}.");

            if (AmongUsClient.Instance == null)
            {
                LogErro("AmongUsClient.Instance está nulo.");
                return false;
            }

            // Igual ao EHR: o host aplica diretamente se ele próprio é o dono.
            // Evita enviar um RPC para si mesmo e evita propagar a troca a todos.
            if (AmongUsClient.Instance.ClientId == jogador.OwnerId)
            {
                // EHR usa a coroutine nativa CoSetRole para evitar a tela preta
                // ao alterar o papel do cliente local.
                jogador.StartCoroutine(jogador.CoSetRole(funcao, true));
                LogInfo($"CoSetRole local agendado: jogador={jogador.PlayerId}, função={funcao}, canOverrideRole=true.");
                return true;
            }

            // EHR CustomRpcSender.RpcSetRole envia RpcCalls.SetRole ao OwnerId
            // e escreve RoleTypes + true (canOverrideRole) no payload.
            MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                jogador.NetId,
                (byte)RpcCalls.SetRole,
                SendOption.Reliable,
                jogador.OwnerId);

            writer.Write((ushort)funcao);
            writer.Write(true);
            AmongUsClient.Instance.FinishRpcImmediately(writer);

            LogInfo($"RPC SetRole direcionado enviado: jogador={jogador.PlayerId}, função={funcao}, destino={jogador.OwnerId}, canOverrideRole=true.");
            return true;
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

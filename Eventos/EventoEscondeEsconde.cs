using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using HarmonyLib;
using Hazel;
using UnityEngine;

namespace YouTubeVoteBridge;

// Primeira versão do esconde-esconde: escolhe um caçador e um fugitivo,
// atribui Impostor/Engineer, impede o caçador de matar terceiros e encerra
// após 60 segundos ou quando o fugitivo é pego.
internal sealed class EventoEscondeEsconde : EventoVotacao
{
    private const float DuracaoSegundos = 60f;
    private static readonly System.Random Sorteador = new();

    private static PlayerControl cacador;
    private static PlayerControl fugitivo;
    private static object papelOriginalCacador;
    private static object papelOriginalFugitivo;
    private static string nomeOriginalCacador;
    private static string nomeOriginalFugitivo;
    private static float terminaEm;
    private static bool ativo;
    private static bool aplicandoPapel;

    // O Vanilla pode ignorar novas atribuições de papel sem canOverride.
    // Mantemos a flag ativa apenas durante cada operação do evento.

    public override string Nome => "Esconde-esconde: um caçador contra um fugitivo por 60 segundos";

    internal static bool Ativo => ativo;
    internal static byte? CacadorId => cacador != null ? cacador.PlayerId : null;
    internal static byte? FugitivoId => fugitivo != null ? fugitivo.PlayerId : null;

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        if (ativo)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Já existe uma rodada ativa.");
            return;
        }

        List<PlayerControl> candidatos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data != null && !jogador.Data.Disconnected &&
                !jogador.Data.IsDead)
                candidatos.Add(jogador);
        }

        if (candidatos.Count < 2)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Evento cancelado: são necessários pelo menos dois jogadores vivos.");
            return;
        }

        int indiceCacador = Sorteador.Next(candidatos.Count);
        int indiceFugitivo;
        do { indiceFugitivo = Sorteador.Next(candidatos.Count); }
        while (indiceFugitivo == indiceCacador);

        cacador = candidatos[indiceCacador];
        fugitivo = candidatos[indiceFugitivo];
        papelOriginalCacador = LerPapelOriginal(cacador);
        papelOriginalFugitivo = LerPapelOriginal(fugitivo);

        if (papelOriginalCacador == null || papelOriginalFugitivo == null)
        {
            LimparEstado();
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Não foi possível guardar os papéis originais. Rodada cancelada.");
            return;
        }

        nomeOriginalCacador = cacador.Data.PlayerName;
        nomeOriginalFugitivo = fugitivo.Data.PlayerName;

        // Atribui as funções apenas na perspectiva do próprio participante.
        // O RPC vanilla SetRole é enviado diretamente ao cliente dono do jogador.
        bool papelCacadorDefinido = DefinirPapelParaCliente(cacador, "Impostor", cacador.OwnerId);
        bool papelFugitivoDefinido = DefinirPapelParaCliente(fugitivo, "Engineer", fugitivo.OwnerId);
        if (!papelCacadorDefinido || !papelFugitivoDefinido)
        {
            RestaurarPapeis();
            RestaurarNicks();
            LimparEstado();
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Não consegui atribuir os papéis localmente. Consulte o log.");
            return;
        }

        ativo = true;
        terminaEm = Time.realtimeSinceStartup + DuracaoSegundos;
        YouTubeVoteBridgePlugin.DefinirNomeLocal(
            cacador, nomeOriginalCacador + "\n<color=#FF5555><size=70%>VOCÊ É O CAÇADOR</size></color>");
        YouTubeVoteBridgePlugin.DefinirNomeLocal(
            fugitivo, nomeOriginalFugitivo + "\n<color=#55FF55><size=70%>VOCÊ É O FUGITIVO</size></color>");

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Esconde-esconde] Iniciado por {DuracaoSegundos:0}s. " +
            $"Caçador={nomeOriginalCacador} (PlayerId={cacador.PlayerId}); " +
            $"fugitivo={nomeOriginalFugitivo} (PlayerId={fugitivo.PlayerId}). " +
            "Papéis e avisos de nick enviados localmente; mortes bloqueadas.");
    }

    public static void Atualizar()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        if (cacador == null || cacador.Data == null || cacador.Data.Disconnected ||
            fugitivo == null || fugitivo.Data == null || fugitivo.Data.Disconnected)
        {
            Encerrar("rodada cancelada porque um dos participantes saiu");
            return;
        }

        if (fugitivo.Data.IsDead || cacador.Data.IsDead)
        {
            Encerrar("rodada cancelada porque um participante morreu");
            return;
        }

        if (Time.realtimeSinceStartup >= terminaEm)
            Encerrar("o tempo de 60 segundos terminou");
    }

    internal static bool BloquearMorte(PlayerControl atacante, PlayerControl alvo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            atacante == null || alvo == null)
            return false;

        // Este evento é apenas uma distração: nenhuma morte é permitida durante a rodada.
        return true;
    }

    internal static bool BloquearEntradaDuto(PlayerControl jogador)
    {
        return ativo && YouTubeVoteBridgePlugin.EhHost() &&
               jogador != null && jogador.PlayerId == CacadorId;
    }

    private static void Encerrar(string motivo)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[Esconde-esconde] Fim: " + motivo + ".");
        YouTubeVoteBridgePlugin.EnviarMensagemIndividual(
            cacador, "O esconde-esconde terminou: " + motivo + ".");
        YouTubeVoteBridgePlugin.EnviarMensagemIndividual(
            fugitivo, "O esconde-esconde terminou: " + motivo + ".");
        RestaurarPapeis();
        LimparEstado();
    }

    private static void RestaurarPapeis()
    {
        if (cacador != null && cacador.Data != null && !cacador.Data.Disconnected &&
            papelOriginalCacador != null)
            DefinirPapelParaCliente(cacador, papelOriginalCacador.ToString(), cacador.OwnerId);

        if (fugitivo != null && fugitivo.Data != null && !fugitivo.Data.Disconnected &&
            papelOriginalFugitivo != null)
            DefinirPapelParaCliente(fugitivo, papelOriginalFugitivo.ToString(), fugitivo.OwnerId);

        RestaurarNicks();
    }

    private static void LimparEstado()
    {
        ativo = false;
        cacador = null;
        fugitivo = null;
        papelOriginalCacador = null;
        papelOriginalFugitivo = null;
        nomeOriginalCacador = null;
        nomeOriginalFugitivo = null;
        terminaEm = 0f;
    }

    private static object LerPapelOriginal(PlayerControl jogador)
    {
        try
        {
            object roleData = jogador?.Data?.Role;
            if (roleData == null)
                return null;

            Type tipo = roleData.GetType();
            object papel = tipo.GetProperty("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);
            papel ??= tipo.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);
            return papel;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Falha ao ler papel original: " + ex.Message);
            return null;
        }
    }

    // Replica o comportamento essencial do EHR sem depender do método de extensão
    // RpcSetRoleDesync, que não existe como método de instância neste projeto.
    private static bool DefinirPapelParaCliente(PlayerControl jogador, string nomePapel, int clientId)
    {
        aplicandoPapel = true;
        try
        {
            return DefinirPapelParaClienteInterno(jogador, nomePapel, clientId);
        }
        finally
        {
            aplicandoPapel = false;
        }
    }

    internal static bool DeveForcarOverridePapel => ativo || aplicandoPapel;

    private static bool DefinirPapelParaClienteInterno(PlayerControl jogador, string nomePapel, int clientId)
    {
        if (jogador == null || string.IsNullOrWhiteSpace(nomePapel) ||
            clientId < 0 || AmongUsClient.Instance == null ||
            !Enum.TryParse(typeof(RoleTypes), nomePapel, true, out object valorPapel))
            return false;

        try
        {
            RoleTypes papel = (RoleTypes)valorPapel;
            bool aplicadoLocalmente = false;

            // Igual ao RpcSetRoleDesync do EHR:
            // o papel só é aplicado diretamente se o dono do personagem for o host
            // local. Para qualquer outro jogador, o RPC precisa ser direcionado
            // exclusivamente ao clientId proprietário.
            if (clientId == AmongUsClient.Instance.ClientId)
            {
                aplicadoLocalmente = TentarAplicarPapelLocal(jogador, papel);
                if (!aplicadoLocalmente)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[Esconde-esconde] Não foi possível iniciar CoSetRole local para jogador={jogador.PlayerId}, papel={papel}.");
                    return false;
                }
            }
            else
            {
                MessageWriter writer = AmongUsClient.Instance.StartRpcImmediately(
                    jogador.NetId, (byte)RpcCalls.SetRole, SendOption.Reliable, clientId);
                writer.Write((ushort)papel);
                writer.Write(true);
                AmongUsClient.Instance.FinishRpcImmediately(writer);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Esconde-esconde] SetRole direcionado: jogador={jogador.PlayerId}, papel={papel}, " +
                $"destino={clientId}, aplicadoLocalmente={aplicadoLocalmente}.");
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Esconde-esconde] Falha ao definir papel " + nomePapel + ": " + ex);
            return false;
        }
    }

    private static bool TentarAplicarPapelLocal(PlayerControl jogador, RoleTypes papel)
    {
        try
        {
            // O SecretTraitor usa RpcSetRole(role, true) para pedir a aplicação
            // com canOverride habilitado. Aqui só é chamado para o personagem
            // cujo OwnerId é o cliente local; jogadores remotos continuam usando
            // o RPC direcionado em DefinirPapelParaClienteInterno.
            foreach (MethodInfo metodo in jogador.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (metodo.Name != "RpcSetRole")
                    continue;

                ParameterInfo[] parametros = metodo.GetParameters();
                if (parametros.Length == 2 &&
                    parametros[0].ParameterType == typeof(RoleTypes) &&
                    parametros[1].ParameterType == typeof(bool))
                {
                    metodo.Invoke(jogador, new object[] { papel, true });
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        $"[Esconde-esconde] RpcSetRole local chamado com canOverride=true: " +
                        $"jogador={jogador.PlayerId}, papel={papel}.");
                    return true;
                }
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Não encontrei RpcSetRole(RoleTypes, bool) para aplicar o papel local.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Esconde-esconde] Falha em RpcSetRole local: " +
                (ex.InnerException ?? ex));
        }

        return false;
    }

    private static void RestaurarNicks()
    {
        if (cacador != null && !string.IsNullOrEmpty(nomeOriginalCacador))
            YouTubeVoteBridgePlugin.DefinirNomeLocal(cacador, nomeOriginalCacador);
        if (fugitivo != null && !string.IsNullOrEmpty(nomeOriginalFugitivo))
            YouTubeVoteBridgePlugin.DefinirNomeLocal(fugitivo, nomeOriginalFugitivo);
    }

    private static bool DefinirPapel(PlayerControl jogador, string nomePapel)
    {
        if (jogador == null || string.IsNullOrEmpty(nomePapel))
            return false;

        try
        {
            Type tipoEnum = typeof(RoleTypes);
            if (!Enum.TryParse(tipoEnum, nomePapel, true, out object valorPapel))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Esconde-esconde] RoleTypes não contém o papel " + nomePapel + ".");
                return false;
            }

            foreach (MethodInfo metodo in jogador.GetType().GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (metodo.Name != "RpcSetRole")
                    continue;

                ParameterInfo[] parametros = metodo.GetParameters();
                if (parametros.Length == 1 && parametros[0].ParameterType == tipoEnum)
                {
                    metodo.Invoke(jogador, new[] { valorPapel });
                    return true;
                }

                if (parametros.Length == 2 && parametros[0].ParameterType == tipoEnum &&
                    parametros[1].ParameterType == typeof(bool))
                {
                    metodo.Invoke(jogador, new[] { valorPapel, (object)true });
                    return true;
                }
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Não encontrei um RpcSetRole compatível.");
            return false;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Esconde-esconde] Falha ao definir papel " + nomePapel + ": " +
                (ex.InnerException ?? ex));
            return false;
        }
    }

    private static void CompletarUmaTarefa(PlayerControl jogador)
    {
        try
        {
            object tarefasObj = jogador?.Data?.Tasks;
            if (tarefasObj is not System.Collections.IEnumerable tarefas)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Esconde-esconde] Não foi possível obter as tarefas do fugitivo.");
                return;
            }

            foreach (object tarefa in tarefas)
            {
                if (tarefa == null)
                    continue;

                Type tipo = tarefa.GetType();
                object id = tipo.GetProperty("Id")?.GetValue(tarefa);
                id ??= tipo.GetField("Id")?.GetValue(tarefa);
                if (id == null)
                    continue;

                foreach (MethodInfo metodo in jogador.GetType().GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (metodo.Name != "RpcCompleteTask")
                        continue;

                    ParameterInfo[] parametros = metodo.GetParameters();
                    if (parametros.Length == 1 &&
                        parametros[0].ParameterType.IsInstanceOfType(id))
                    {
                        metodo.Invoke(jogador, new[] { id });
                        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                            "[Esconde-esconde] Solicitada conclusão de uma tarefa para o fugitivo.");
                        return;
                    }
                }
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Não encontrei uma tarefa compatível para concluir.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Falha ao concluir tarefa: " + (ex.InnerException ?? ex));
        }
    }
}

// Bloqueia tentativas de assassinato não permitidas durante o esconde-esconde.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CheckMurder))]
internal static class EventoEscondeEscondeMortePatch
{
    public static bool Prefix(PlayerControl __instance, [HarmonyArgument(0)] PlayerControl alvo)
    {
        if (!EventoEscondeEsconde.BloquearMorte(__instance, alvo))
            return true;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Esconde-esconde] Morte bloqueada: atacante=" +
            (__instance?.Data?.PlayerName ?? "?") + ", alvo=" +
            (alvo?.Data?.PlayerName ?? "?") + ".");
        return false;
    }
}

// Intercepta RpcEnterVent por reflexão para não depender da assinatura do método.
[HarmonyPatch]
internal static class EventoEscondeEscondeDutoPatch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(PlayerPhysics), "RpcEnterVent");
    }

    private static bool Prefix(PlayerPhysics __instance)
    {
        if (__instance == null)
            return true;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.MyPhysics == __instance &&
                EventoEscondeEsconde.BloquearEntradaDuto(jogador))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Esconde-esconde] Entrada em duto bloqueada para o caçador.");
                return false;
            }
        }

        return true;
    }
}

// Durante a atribuição/restauração do evento, garante que a chamada nativa
// RpcSetRole não perca o canOverride. Não altera atribuições de outros sistemas
// fora do evento.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.RpcSetRole))]
internal static class EventoEscondeEscondeRpcSetRolePatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] RoleTypes roleType,
        [HarmonyArgument(1)] ref bool canOverrideRole)
    {
        // Segue o comportamento do EHR: força canOverrideRole em todas as chamadas
        // nativas de RpcSetRole, não apenas nas iniciadas pelo evento.
        canOverrideRole = true;

        if (EventoEscondeEsconde.DeveForcarOverridePapel)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Esconde-esconde][DIAG] RpcSetRole: jogador={__instance?.PlayerId}, " +
                $"papel={roleType}, canOverrideRole=true.");
        }
    }
}

// Diagnóstico da aplicação real do papel. RpcSetRole pode ser enviado sem que
// o papel final observado em Data.Role seja o esperado; este patch registra a
// entrada na coroutine nativa que efetivamente processa a atribuição.
[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CoSetRole))]
internal static class EventoEscondeEscondeCoSetRoleDiagnosticoPatch
{
    public static void Prefix(PlayerControl __instance, [HarmonyArgument(0)] ref RoleTypes role,
        [HarmonyArgument(1)] ref bool canOverride)
    {
        if (!EventoEscondeEsconde.DeveForcarOverridePapel)
            return;

        canOverride = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Esconde-esconde][DIAG] CoSetRole recebido: jogador={__instance?.PlayerId}, " +
            $"nome={__instance?.Data?.PlayerName ?? "?"}, papel={role}, canOverride=true.");
    }
}

using System;
using System.Collections.Generic;
using System.Reflection;
using AmongUs.GameOptions;
using HarmonyLib;
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
    private static float terminaEm;
    private static bool ativo;

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

        if (!DefinirPapel(cacador, "Impostor") ||
            !DefinirPapel(fugitivo, "Engineer"))
        {
            RestaurarPapeis();
            LimparEstado();
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Não consegui atribuir os papéis necessários. Consulte o log.");
            return;
        }

        ativo = true;
        terminaEm = Time.realtimeSinceStartup + DuracaoSegundos;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Esconde-esconde] Iniciado por {DuracaoSegundos:0}s. " +
            $"Caçador={cacador.Data.PlayerName} (PlayerId={cacador.PlayerId}); " +
            $"fugitivo={fugitivo.Data.PlayerName} (PlayerId={fugitivo.PlayerId}). " +
            "O caçador só pode matar o fugitivo.");
        YouTubeVoteBridgePlugin.EnviarMensagemIndividual(
            cacador, "Você é o CAÇADOR! Encontre o fugitivo em até 60 segundos. Não pode usar dutos nem matar outros jogadores.");
        YouTubeVoteBridgePlugin.EnviarMensagemIndividual(
            fugitivo, "Você é o FUGITIVO! Sobreviva por 60 segundos. Seu papel temporário é Engenheiro.");
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

        if (fugitivo.Data.IsDead)
        {
            Encerrar("o fugitivo foi pego; vitória do caçador");
            return;
        }

        if (cacador.Data.IsDead)
        {
            Encerrar("rodada cancelada porque o caçador morreu");
            return;
        }

        if (Time.realtimeSinceStartup >= terminaEm)
        {
            if (!fugitivo.Data.IsDead)
                CompletarUmaTarefa(fugitivo);

            Encerrar("o fugitivo sobreviveu por 60 segundos; uma tarefa foi concluída se havia uma disponível");
        }
    }

    internal static bool BloquearMorte(PlayerControl atacante, PlayerControl alvo)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            atacante == null || alvo == null)
            return false;

        // O fugitivo não pode matar. O caçador só pode matar o fugitivo.
        if (atacante.PlayerId == FugitivoId ||
            (atacante.PlayerId == CacadorId && alvo.PlayerId != FugitivoId))
            return true;

        return false;
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
            DefinirPapel(cacador, papelOriginalCacador.ToString());

        if (fugitivo != null && fugitivo.Data != null && !fugitivo.Data.Disconnected &&
            papelOriginalFugitivo != null)
            DefinirPapel(fugitivo, papelOriginalFugitivo.ToString());
    }

    private static void LimparEstado()
    {
        ativo = false;
        cacador = null;
        fugitivo = null;
        papelOriginalCacador = null;
        papelOriginalFugitivo = null;
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

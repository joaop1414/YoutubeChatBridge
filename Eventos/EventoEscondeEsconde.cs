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

        // Antes dos papéis do evento, percorre a sequência usada como tentativa
        // de forçar a atualização de papel em clientes vanilla. Cada RPC remoto
        // é direcionado somente ao dono do personagem e inclui canOverrideRole=true.
        bool cicloCacador = ExecutarCicloPapel(cacador, papelOriginalCacador);
        bool cicloFugitivo = ExecutarCicloPapel(fugitivo, papelOriginalFugitivo);
        if (!cicloCacador || !cicloFugitivo)
        {
            RestaurarPapeis();
            LimparEstado();
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Esconde-esconde] Ciclo de atualização de papel falhou; evento cancelado.");
            return;
        }

        // Só depois do ciclo aplica os papéis do evento.
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

    // Executa: Crewmate -> papel original -> Impostor -> papel original.
    // O papel desejado pelo evento é aplicado separadamente pelo chamador.
    private static bool ExecutarCicloPapel(PlayerControl jogador, object papelOriginal)
    {
        if (jogador == null || papelOriginal == null || jogador.Data == null ||
            jogador.Data.Disconnected)
            return false;

        string original = papelOriginal.ToString();
        string[] sequencia = { "Crewmate", original, "Impostor", original };
        foreach (string papel in sequencia)
        {
            if (!DefinirPapelParaCliente(jogador, papel, jogador.OwnerId))
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[Esconde-esconde] Ciclo interrompido: jogador={jogador.PlayerId}, papel={papel}.");
                return false;
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Esconde-esconde] Ciclo completo: jogador={jogador.PlayerId}, papel original={original}.");
        return true;
    }

    // Chamado quando a tela de reunião é destruída (reunião encerrada).
    // Não escolhe novos participantes: reaplica o ciclo nos mesmos dois jogadores.
    internal static void ReaplicarAposReuniao()
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost())
            return;

        if (cacador == null || fugitivo == null ||
            cacador.Data == null || fugitivo.Data == null ||
            cacador.Data.Disconnected || fugitivo.Data.Disconnected ||
            cacador.Data.IsDead || fugitivo.Data.IsDead)
        {
            Encerrar("participante indisponível após a reunião");
            return;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Esconde-esconde] Reaplicando papéis após reunião.");

        bool cicloCacador = ExecutarCicloPapel(cacador, papelOriginalCacador);
        bool cicloFugitivo = ExecutarCicloPapel(fugitivo, papelOriginalFugitivo);
        bool papelCacadorDefinido = cicloCacador &&
            DefinirPapelParaCliente(cacador, "Impostor", cacador.OwnerId);
        bool papelFugitivoDefinido = cicloFugitivo &&
            DefinirPapelParaCliente(fugitivo, "Engineer", fugitivo.OwnerId);

        if (!papelCacadorDefinido || !papelFugitivoDefinido)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Esconde-esconde] Não foi possível reaplicar os papéis após a reunião.");
            Encerrar("falha ao restaurar os papéis após a reunião");
            return;
        }

        YouTubeVoteBridgePlugin.DefinirNomeLocal(
            cacador, nomeOriginalCacador + "\\n<color=#FF5555><size=70%>VOCÊ É O CAÇADOR</size></color>");
        YouTubeVoteBridgePlugin.DefinirNomeLocal(
            fugitivo, nomeOriginalFugitivo + "\\n<color=#55FF55><size=70%>VOCÊ É O FUGITIVO</size></color>");
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

    // Toda troca passa pela API central: CrewmateGhost (canOverrideRole=true)
    // e, em seguida, a função solicitada.
    private static bool DefinirPapelParaCliente(PlayerControl jogador, string nomePapel, int clientId)
    {
        if (jogador == null || string.IsNullOrWhiteSpace(nomePapel))
            return false;

        // A API usa o dono real do jogador para direcionar o RPC corretamente.
        if (clientId != jogador.OwnerId)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                $"[Esconde-esconde] Destino informado ({clientId}) difere do dono ({jogador.OwnerId}); usando o dono do jogador.");
        }

        return RoleSwitchAPI.TrocarFuncao(jogador, nomePapel);
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


// Após a reunião, o jogo pode recriar/normalizar os papéis. O host reaplica
// o ciclo somente depois que a tela de reunião é destruída.
[HarmonyPatch(typeof(MeetingHud), "OnDestroy")]
internal static class EventoEscondeEscondeFimReuniaoPatch
{
    public static void Postfix()
    {
        EventoEscondeEsconde.ReaplicarAposReuniao();
    }
}

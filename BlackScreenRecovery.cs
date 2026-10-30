using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

// Adaptação do AntiBlackout do EndlessHostRoles.
// Durante a tela de ejeção, força temporariamente uma composição de funções
// que evita a condição de tela preta. Depois do WrapUp, restaura as funções
// originais (ou a variante fantasma correta para quem morreu).
internal static class BlackScreenRecovery
{
    private sealed class EstadoFuncao
    {
        public PlayerControl Jogador;
        public RoleTypes FuncaoOriginal;
    }

    private static readonly Dictionary<byte, EstadoFuncao> FuncoesOriginais = new();
    private static bool AntiBlackoutAtivo;
    private static bool RestauracaoPendente;
    private static float PrazoRestauracaoAlternativo;

    // Chamado quando MeetingHud é destruído, no mesmo ponto do EHR.
    public static void PrepararFuncoesTemporarias()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            AmongUsClient.Instance == null ||
            !AmongUsClient.Instance.IsGameStarted ||
            ShipStatus.Instance == null ||
            AntiBlackoutAtivo ||
            GameManager.Instance == null)
            return;

        try
        {
            List<PlayerControl> todos = ObterJogadoresConectados();
            if (todos.Count <= 2)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[AntiBlackout] Ignorado: há no máximo 2 jogadores conectados; " +
                    "não é possível montar a composição segura do EHR.");
                return;
            }

            List<PlayerControl> vivos = todos
                .Where(j => j.Data != null && !j.Data.IsDead)
                .ToList();

            if (vivos.Count < 2)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[AntiBlackout] Menos de 2 jogadores vivos. A composição temporária não será aplicada.");
                return;
            }

            // O EHR precisa de 1 impostor visível e pelo menos 2 tripulantes
            // visíveis. Com exatamente 2 vivos, usa um jogador morto como
            // tripulante temporário, sem revivê-lo de verdade.
            PlayerControl tripulanteFantasmaTemporario = null;
            if (vivos.Count == 2)
            {
                byte? exiladoId = ObterIdJogadorExilado();
                tripulanteFantasmaTemporario = todos.FirstOrDefault(j =>
                    j.Data != null &&
                    j.Data.IsDead &&
                    !j.Data.Disconnected &&
                    (!exiladoId.HasValue || j.PlayerId != exiladoId.Value));

                if (tripulanteFantasmaTemporario == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[AntiBlackout] Existem apenas 2 jogadores vivos e não foi possível " +
                        "identificar um jogador morto que possa servir de tripulante temporário. " +
                        "A prevenção foi ignorada para não alterar incorretamente as funções.");
                    return;
                }
            }

            FuncoesOriginais.Clear();

            // Guarda as funções reais de todos os jogadores antes de alterar
            // qualquer RoleType. A identidade das funções personalizadas do mod
            // não é modificada por esta troca temporária do RoleType vanilla.
            foreach (PlayerControl jogador in todos)
            {
                if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                    continue;

                if (!TentarLerRoleType(jogador, out RoleTypes funcao))
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        $"[AntiBlackout] Não foi possível ler a função original de PlayerId={jogador.PlayerId}. " +
                        "Cancelando a prevenção para evitar perder a função ao restaurar.");
                    FuncoesOriginais.Clear();
                    return;
                }

                FuncoesOriginais[jogador.PlayerId] = new EstadoFuncao
                {
                    Jogador = jogador,
                    FuncaoOriginal = funcao
                };
            }

            // Prioriza um impostor real já existente. Caso contrário, escolhe
            // um jogador vivo determinístico para ser o impostor temporário.
            PlayerControl impostorTemporario = vivos
                .OrderByDescending(j => j.Data.Role != null && j.Data.Role.IsImpostor)
                .ThenBy(j => j.PlayerId)
                .FirstOrDefault();

            if (impostorTemporario == null)
            {
                FuncoesOriginais.Clear();
                return;
            }

            AntiBlackoutAtivo = true;
            RestauracaoPendente = true;
            PrazoRestauracaoAlternativo = Time.realtimeSinceStartup + 8f;

            // Mesma composição usada pelo EHR: um impostor e todos os demais
            // jogadores vivos como tripulantes.
            if (!DefinirFuncao(impostorTemporario, RoleTypes.Impostor))
                throw new InvalidOperationException("Não foi possível definir o impostor temporário.");

            foreach (PlayerControl jogador in vivos)
            {
                if (jogador == null || jogador.PlayerId == impostorTemporario.PlayerId)
                    continue;

                if (!DefinirFuncao(jogador, RoleTypes.Crewmate))
                    throw new InvalidOperationException(
                        $"Não foi possível definir tripulante temporário para PlayerId={jogador.PlayerId}.");
            }

            if (tripulanteFantasmaTemporario != null)
            {
                if (!DefinirFuncao(tripulanteFantasmaTemporario, RoleTypes.Crewmate))
                    throw new InvalidOperationException(
                        "Não foi possível definir o tripulante temporário adicional.");
            }

            // Jogadores mortos restantes aparecem como fantasmas durante a
            // transição, evitando que mantenham uma função viva na tela de ejeção.
            foreach (PlayerControl jogador in todos)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.Disconnected || !jogador.Data.IsDead ||
                    (tripulanteFantasmaTemporario != null &&
                     jogador.PlayerId == tripulanteFantasmaTemporario.PlayerId))
                    continue;

                RoleTypes fantasma = ObterFuncaoFantasmaOriginal(jogador.PlayerId);
                DefinirFuncao(jogador, fantasma);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[AntiBlackout] Prevenção aplicada após a reunião. " +
                $"Impostor temporário: PlayerId={impostorTemporario.PlayerId}; " +
                $"jogadores vivos: {vivos.Count}; jogadores conectados: {todos.Count}. " +
                "As funções originais serão restauradas após a tela de ejeção.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[AntiBlackout] Erro ao preparar funções temporárias: " + ex);

            // Se uma troca falhar no meio do processo, tenta restaurar tudo
            // imediatamente para não deixar ninguém com uma função provisória.
            RestaurarFuncoesOriginais(true);
        }
    }

    // Chamado pelo WrapUp da tela de ejeção.
    public static void NotificarFimDaReuniao()
    {
        RestaurarFuncoesOriginais(false);
    }

    // Fallback para reuniões sem ExileController (por exemplo, empate).
    // O plugin já chama este método durante Update.
    public static void Atualizar()
    {
        if (!RestauracaoPendente || !AntiBlackoutAtivo ||
            !YouTubeVoteBridgePlugin.EhHost())
            return;

        if (Time.realtimeSinceStartup < PrazoRestauracaoAlternativo)
            return;

        if (MeetingHud.Instance != null ||
            UnityEngine.Object.FindObjectOfType<ExileController>() != null)
            return;

        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
            "[AntiBlackout] O WrapUp não notificou a restauração; executando restauração alternativa.");
        RestaurarFuncoesOriginais(false);
    }

    private static void RestaurarFuncoesOriginais(bool restauracaoPorFalha)
    {
        if (!AntiBlackoutAtivo && FuncoesOriginais.Count == 0)
            return;

        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        int restauradas = 0;

        foreach (KeyValuePair<byte, EstadoFuncao> item in FuncoesOriginais.ToArray())
        {
            PlayerControl jogador = item.Value.Jogador;
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            try
            {
                // Depois da ejeção, quem morreu precisa receber uma função
                // fantasma. Os vivos voltam exatamente ao RoleType salvo.
                RoleTypes funcao = jogador.Data.IsDead
                    ? ObterFuncaoFantasmaOriginal(item.Key)
                    : item.Value.FuncaoOriginal;

                if (DefinirFuncao(jogador, funcao))
                    restauradas++;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    $"[AntiBlackout] Falha ao restaurar PlayerId={item.Key}: {ex.Message}");
            }
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[AntiBlackout] Restauração concluída: {restauradas}/{FuncoesOriginais.Count} jogador(es)." +
            (restauracaoPorFalha ? " (após falha na preparação)" : string.Empty));

        FuncoesOriginais.Clear();
        AntiBlackoutAtivo = false;
        RestauracaoPendente = false;
        PrazoRestauracaoAlternativo = 0f;
    }

    private static List<PlayerControl> ObterJogadoresConectados()
    {
        List<PlayerControl> jogadores = new();

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;

            jogadores.Add(jogador);
        }

        return jogadores;
    }

    private static bool DefinirFuncao(PlayerControl jogador, RoleTypes funcao)
    {
        if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
            return false;

        return RoleSwitchAPI.TrocarFuncao(jogador, funcao);
    }

    private static bool TentarLerRoleType(PlayerControl jogador, out RoleTypes funcao)
    {
        funcao = default;

        try
        {
            object roleData = jogador?.Data?.Role;
            if (roleData == null)
                return false;

            Type tipo = roleData.GetType();
            object valor =
                tipo.GetProperty("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData) ??
                tipo.GetField("Role", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(roleData);

            if (valor is RoleTypes role)
            {
                funcao = role;
                return true;
            }

            return valor != null && Enum.TryParse(valor.ToString(), true, out funcao);
        }
        catch
        {
            return false;
        }
    }

    private static RoleTypes ObterFuncaoFantasmaOriginal(byte playerId)
    {
        if (!FuncoesOriginais.TryGetValue(playerId, out EstadoFuncao estado))
            return RoleTypes.CrewmateGhost;

        string nome = estado.FuncaoOriginal.ToString();
        bool eraImpostor =
            nome.IndexOf("Impostor", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nome.IndexOf("Shapeshifter", StringComparison.OrdinalIgnoreCase) >= 0 ||
            nome.IndexOf("Viper", StringComparison.OrdinalIgnoreCase) >= 0;

        return eraImpostor ? RoleTypes.ImpostorGhost : RoleTypes.CrewmateGhost;
    }

    private static byte? ObterIdJogadorExilado()
    {
        try
        {
            ExileController exile = UnityEngine.Object.FindObjectOfType<ExileController>();
            if (exile == null)
                return null;

            Type tipo = exile.GetType();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (FieldInfo campo in tipo.GetFields(flags))
            {
                if (campo.Name.IndexOf("exil", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                object valor = campo.GetValue(exile);
                byte? id = ExtrairPlayerId(valor);
                if (id.HasValue)
                    return id;
            }

            foreach (PropertyInfo propriedade in tipo.GetProperties(flags))
            {
                if (propriedade.Name.IndexOf("exil", StringComparison.OrdinalIgnoreCase) < 0 ||
                    propriedade.GetIndexParameters().Length != 0)
                    continue;

                object valor = propriedade.GetValue(exile);
                byte? id = ExtrairPlayerId(valor);
                if (id.HasValue)
                    return id;
            }
        }
        catch { }

        return null;
    }

    private static byte? ExtrairPlayerId(object valor)
    {
        if (valor == null)
            return null;

        if (valor is PlayerControl jogador && jogador.Data != null)
            return jogador.PlayerId;

        if (valor is NetworkedPlayerInfo info)
            return info.PlayerId;

        try
        {
            Type tipo = valor.GetType();
            object playerId =
                tipo.GetProperty("PlayerId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(valor) ??
                tipo.GetField("PlayerId", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(valor);

            if (playerId is byte id)
                return id;
        }
        catch { }

        return null;
    }
}

// O EHR aplica o AntiBlackout no OnDestroy da MeetingHud, antes de terminar
// a transição da tela de ejeção. Este patch reproduz o mesmo ponto do ciclo.
[HarmonyPatch(typeof(MeetingHud), nameof(MeetingHud.OnDestroy))]
internal static class BlackScreenAntiBlackoutMeetingPatch
{
    public static void Postfix()
    {
        BlackScreenRecovery.PrepararFuncoesTemporarias();
    }
}

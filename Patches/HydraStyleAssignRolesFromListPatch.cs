using AmongUs.GameOptions;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;

namespace YouTubeVoteBridge;

/// <summary>
/// Primeiro experimento inspirado no Hydra:
/// intercepta AssignRolesFromList e reserva uma vaga Impostor para o host,
/// removendo o jogador e a função das listas para o seletor vanilla continuar.
/// Não substitui RoleManager.SelectRoles nem altera o transporte dos RPCs.
/// </summary>
[HarmonyPatch(typeof(LogicRoleSelectionNormal), nameof(LogicRoleSelectionNormal.AssignRolesFromList))]
internal static class HydraStyleAssignRolesFromListPatch
{
    private static bool hostAtribuidoNestaSelecao;

    internal static void ReiniciarSelecao()
    {
        hostAtribuidoNestaSelecao = false;
    }

    [HarmonyPrefix]
    private static void Prefix(
        ref List<NetworkedPlayerInfo> players,
        ref List<RoleTypes> roleList,
        ref int rolesAssigned)
    {
        try
        {
            if (hostAtribuidoNestaSelecao ||
                AmongUsClient.Instance == null ||
                !AmongUsClient.Instance.AmHost ||
                PlayerControl.LocalPlayer == null ||
                PlayerControl.LocalPlayer.Data == null ||
                players == null ||
                roleList == null)
                return;

            // Este teste usa uma função de equipe Impostor que o seletor
            // vanilla já colocou na lista. Não cria uma vaga adicional.
            Il2CppSystem.Predicate<RoleTypes> localizarFuncao =
                (Il2CppSystem.Predicate<RoleTypes>)(role => role == RoleTypes.Impostor);

            int roleIndex = roleList.FindIndex(localizarFuncao);
            if (roleIndex < 0)
            {
                Registrar($"Nenhuma vaga Impostor nesta chamada; players={players.Count}, roles={roleList.Count}, assigned={rolesAssigned}.");
                return;
            }

            NetworkedPlayerInfo hostData = PlayerControl.LocalPlayer.Data;
            Il2CppSystem.Predicate<NetworkedPlayerInfo> localizarHost =
                (Il2CppSystem.Predicate<NetworkedPlayerInfo>)(player => player == hostData);

            int playerIndex = players.FindIndex(localizarHost);
            if (playerIndex < 0)
            {
                Registrar($"Host não está na lista desta chamada; players={players.Count}, roles={roleList.Count}, assigned={rolesAssigned}.");
                return;
            }

            // Consumimos exatamente uma vaga e um jogador, como o Hydra.
            players.RemoveAt(playerIndex);
            roleList.RemoveAt(roleIndex);

            hostAtribuidoNestaSelecao = true;

            // Mantém o RPC vanilla original, mas ativa canOverrideRole=true
            // somente nesta atribuição controlada do host.
            PlayerControl.LocalPlayer.RpcSetRole(RoleTypes.Impostor, true);
            rolesAssigned++;

            Registrar($"HYDRA TESTE: host recebeu Impostor. players restantes={players.Count}, roles restantes={roleList.Count}, rolesAssigned={rolesAssigned}.");
        }
        catch (System.Exception ex)
        {
            Registrar("Erro no Prefix Hydra-style: " + ex);
        }
    }

    private static void Registrar(string mensagem)
    {
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[HydraAssignTest] " + mensagem);
    }
}

/// <summary>
/// Reinicia a proteção de atribuição única antes de cada seleção vanilla.
/// </summary>
[HarmonyPatch(typeof(RoleManager), nameof(RoleManager.SelectRoles))]
internal static class HydraStyleSelectRolesResetPatch
{
    [HarmonyPrefix]
    private static void Prefix()
    {
        HydraStyleAssignRolesFromListPatch.ReiniciarSelecao();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[HydraAssignTest] SelectRoles começou; experimento Hydra-style armado.");
    }
}

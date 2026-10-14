using System.Collections.Generic;

namespace YouTubeVoteBridge;

/// <summary>
/// Centraliza IDs reservados e valida quais clientes podem receber a associação
/// de um PlayerControl artificial. A mesma regra é usada pela API e pelo tsunami.
/// </summary>
internal static class CompatibilidadeClientes
{
    // IDs fora do intervalo normal de jogadores; não reutilizar entre sistemas.
    public const byte PlayerIdFakeApi = 253;
    public const byte PlayerIdIndicadorTsunami = 254;

    public static bool ClienteRemotoElegivel(
        PlayerControl candidato,
        PlayerControl objetoFalso,
        HashSet<int> proprietariosIncluidos)
    {
        if (AmongUsClient.Instance == null ||
            candidato == null ||
            candidato == objetoFalso ||
            candidato == PlayerControl.LocalPlayer ||
            candidato.Data == null ||
            candidato.Data.Disconnected ||
            candidato.OwnerId < 0)
            return false;

        if (!proprietariosIncluidos.Add(candidato.OwnerId))
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Compatibilidade: ignorando PlayerControl duplicado para OwnerId=" +
                candidato.OwnerId + " durante associação de objeto falso.");
            return false;
        }

        return true;
    }
}

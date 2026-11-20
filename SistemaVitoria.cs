using System;

namespace YouTubeVoteBridge;

/// <summary>
/// Centraliza a regra usada pelos patches que bloqueiam encerramentos vanilla.
/// Nesta etapa, mantém o comportamento existente do "Sem fim de jogo" e da
/// suspensão durante troca de funções, sem ativar bloqueio permanente ainda.
/// </summary>
internal static class SistemaVitoria
{
    public static bool BloquearVitoriaVanilla(bool bloquearDuranteTroca = true)
    {
        if (YouTubeVoteBridgePlugin.PermitirFimDeJogoForcado)
            return false;

        if (bloquearDuranteTroca && YouTubeVoteBridgePlugin.BloquearFimDeJogoDuranteTrocaDeFuncoes)
            return true;

        return YouTubeVoteBridgePlugin.SemFimDeJogo != null &&
               YouTubeVoteBridgePlugin.SemFimDeJogo.Value;
    }
}

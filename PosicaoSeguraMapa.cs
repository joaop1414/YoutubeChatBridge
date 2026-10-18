using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Seleciona posições caminháveis dentro de uma sala, rejeitando pontos fora
/// da área e pontos ocupados por colisores sólidos. Compartilhado por
/// teleporte aleatório e criação de corpos falsos.
/// </summary>
internal static class PosicaoSeguraMapa
{
    private const int TentativasPorSala = 48;
    private const float RaioVerificacao = 0.24f;

    public static bool TentarObterPontoSeguro(
        Collider2D area, Vector2 deslocamentoFinal, out Vector2 posicaoBase)
    {
        posicaoBase = Vector2.zero;
        if (area == null || !area.enabled || !area.gameObject.activeInHierarchy)
            return false;

        Bounds limites = area.bounds;
        for (int i = 0; i < TentativasPorSala; i++)
        {
            Vector2 candidato = new(
                UnityEngine.Random.Range(limites.min.x, limites.max.x),
                UnityEngine.Random.Range(limites.min.y, limites.max.y));

            Vector2 destinoFinal = candidato + deslocamentoFinal;
            if (!area.OverlapPoint(candidato) || !area.OverlapPoint(destinoFinal))
                continue;

            if (!PontoLivre(candidato, area) || !PontoLivre(destinoFinal, area))
                continue;

            posicaoBase = candidato;
            return true;
        }

        return false;
    }

    private static bool PontoLivre(Vector2 ponto, Collider2D area)
    {
        Collider2D[] colisores = Physics2D.OverlapCircleAll(ponto, RaioVerificacao);
        foreach (Collider2D colisor in colisores)
        {
            if (colisor == null || !colisor.enabled || !colisor.gameObject.activeInHierarchy)
                continue;

            // A própria área da sala define a região caminhável, não é obstáculo.
            if (colisor == area || colisor.isTrigger)
                continue;

            // Jogadores não devem bloquear um ponto; eles podem se mover antes
            // de o snap chegar e não representam paredes/limites do mapa.
            if (colisor.GetComponentInParent<PlayerControl>() != null)
                continue;

            // Um colisor sólido indica parede, objeto ou obstáculo no destino.
            return false;
        }

        return true;
    }
}

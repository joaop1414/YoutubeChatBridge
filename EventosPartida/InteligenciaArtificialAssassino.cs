using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal enum EstadoIAAssassino
{
    Inativo,
    Inicializando,
    Patrulhando,
    Perseguindo,
    Investigando,
    ResolvendoSabotagem,
    Transportando,
    Aguardando
}

/// <summary>
/// IA simples: escolhe exclusivamente o jogador válido e visível mais próximo.
/// Não patrulha, investiga posições antigas nem toma decisões de sabotagem.
/// </summary>
internal sealed class InteligenciaArtificialAssassino
{
    private PlayerControl alvo;

    public EstadoIAAssassino EstadoAtual { get; private set; } = EstadoIAAssassino.Inativo;
    public PlayerControl AlvoAtual => alvo;
    public bool EstaInvestigando => false;
    public bool EstaPerseguindo => alvo != null;
    public Vector2 UltimaPosicaoConhecida => alvo != null ? alvo.GetTruePosition() : Vector2.zero;
    public Vector2 DestinoAtual => alvo != null ? alvo.GetTruePosition() : Vector2.zero;

    public PlayerControl AtualizarAlvo(
        List<PlayerControl> jogadores,
        PlayerControl npc,
        Vector2 origem,
        float agora,
        Func<PlayerControl, bool> podeSerAlvo,
        Func<PlayerControl, bool> estaVisivel)
    {
        alvo = null;

        if (npc == null || jogadores == null || podeSerAlvo == null || estaVisivel == null)
        {
            EstadoAtual = EstadoIAAssassino.Inativo;
            return null;
        }

        float menorDistancia = float.MaxValue;
        foreach (PlayerControl jogador in jogadores)
        {
            if (jogador == null || jogador == npc ||
                !podeSerAlvo(jogador) || !estaVisivel(jogador))
                continue;

            float distancia = Vector2.Distance(origem, jogador.GetTruePosition());
            if (distancia < menorDistancia)
            {
                menorDistancia = distancia;
                alvo = jogador;
            }
        }

        EstadoAtual = alvo != null
            ? EstadoIAAssassino.Perseguindo
            : EstadoIAAssassino.Aguardando;
        return alvo;
    }

    public void EsquecerAlvo()
    {
        alvo = null;
        EstadoAtual = EstadoIAAssassino.Aguardando;
    }

    public void DefinirEstado(EstadoIAAssassino estado)
    {
        EstadoAtual = estado;
    }

    public void Resetar()
    {
        alvo = null;
        EstadoAtual = EstadoIAAssassino.Inativo;
    }
}

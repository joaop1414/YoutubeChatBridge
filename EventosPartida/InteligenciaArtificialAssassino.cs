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
/// Controlador de decisões do NPC Assassino.
/// Mantém o alvo, aplica histerese para evitar trocas constantes e guarda
/// a última posição vista para investigar quando perde a linha de visão.
/// Não executa física nem interações de mapa.
/// </summary>
internal sealed class InteligenciaArtificialAssassino
{
    private const float TempoMemoriaVisual = 1.8f;
    private const float IntervaloAvaliacaoAlvo = 0.20f;
    private const float MargemTrocaAlvo = 1.25f;
    private const float DistanciaChegadaInvestigacao = 0.32f;

    private PlayerControl alvo;
    private Vector2 ultimaPosicaoConhecida;
    private float ultimoAvistamento;
    private float proximaAvaliacao;
    private bool possuiPosicaoConhecida;

    public EstadoIAAssassino EstadoAtual { get; private set; } =
        EstadoIAAssassino.Inativo;

    public PlayerControl AlvoAtual => alvo;

    public bool EstaInvestigando =>
        EstadoAtual == EstadoIAAssassino.Investigando;

    public Vector2 DestinoAtual
    {
        get
        {
            if (EstaInvestigando && possuiPosicaoConhecida)
                return ultimaPosicaoConhecida;

            return alvo != null ? alvo.GetTruePosition() : Vector2.zero;
        }
    }

    public PlayerControl AtualizarAlvo(
        List<PlayerControl> jogadores,
        PlayerControl npc,
        Vector2 origem,
        float agora,
        Func<PlayerControl, bool> podeSerAlvo,
        Func<PlayerControl, bool> estaVisivel)
    {
        if (npc == null || jogadores == null ||
            podeSerAlvo == null || estaVisivel == null)
        {
            EsquecerAlvo();
            EstadoAtual = EstadoIAAssassino.Patrulhando;
            return null;
        }

        if (alvo != null && !podeSerAlvo(alvo))
            EsquecerAlvo();

        bool alvoVisivel = alvo != null && estaVisivel(alvo);
        if (alvoVisivel)
        {
            ultimaPosicaoConhecida = alvo.GetTruePosition();
            ultimoAvistamento = agora;
            possuiPosicaoConhecida = true;
            EstadoAtual = EstadoIAAssassino.Perseguindo;
        }
        else if (alvo != null)
        {
            float tempoSemVer = agora - ultimoAvistamento;
            if (tempoSemVer <= TempoMemoriaVisual && possuiPosicaoConhecida)
            {
                EstadoAtual = EstadoIAAssassino.Investigando;

                // Ao chegar ao último ponto visto, encerra a investigação.
                if (Vector2.Distance(origem, ultimaPosicaoConhecida) <= DistanciaChegadaInvestigacao)
                    EsquecerAlvo();
            }
            else
            {
                EsquecerAlvo();
            }
        }

        if (agora < proximaAvaliacao && alvo != null)
            return alvo;

        proximaAvaliacao = agora + IntervaloAvaliacaoAlvo;

        PlayerControl melhor = null;
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
                melhor = jogador;
            }
        }

        if (alvo == null)
        {
            DefinirAlvo(melhor, agora);
        }
        else if (alvoVisivel)
        {
            float distanciaAtual = Vector2.Distance(origem, alvo.GetTruePosition());

            // Só troca se o novo alvo estiver claramente mais perto.
            // Isso evita alternância de alvo quando dois jogadores estão próximos.
            if (melhor != null && melhor != alvo &&
                menorDistancia + MargemTrocaAlvo < distanciaAtual)
                DefinirAlvo(melhor, agora);
        }
        else if (alvo != null && melhor != null)
        {
            float distanciaInvestigacao =
                Vector2.Distance(origem, ultimaPosicaoConhecida);

            // Um jogador visível que se aproxima muito mais pode interromper
            // a investigação do alvo anterior.
            if (menorDistancia + MargemTrocaAlvo < distanciaInvestigacao)
                DefinirAlvo(melhor, agora);
        }

        if (alvo == null)
        {
            EstadoAtual = EstadoIAAssassino.Patrulhando;
            return null;
        }

        if (estaVisivel(alvo))
        {
            ultimaPosicaoConhecida = alvo.GetTruePosition();
            ultimoAvistamento = agora;
            possuiPosicaoConhecida = true;
            EstadoAtual = EstadoIAAssassino.Perseguindo;
        }
        else if (possuiPosicaoConhecida &&
                 agora - ultimoAvistamento <= TempoMemoriaVisual)
        {
            EstadoAtual = EstadoIAAssassino.Investigando;
        }
        else
        {
            EsquecerAlvo();
            EstadoAtual = EstadoIAAssassino.Patrulhando;
        }

        return alvo;
    }

    private void DefinirAlvo(PlayerControl novoAlvo, float agora)
    {
        alvo = novoAlvo;
        proximaAvaliacao = agora + IntervaloAvaliacaoAlvo;

        if (alvo == null)
        {
            possuiPosicaoConhecida = false;
            ultimaPosicaoConhecida = Vector2.zero;
            ultimoAvistamento = 0f;
            EstadoAtual = EstadoIAAssassino.Patrulhando;
            return;
        }

        ultimaPosicaoConhecida = alvo.GetTruePosition();
        ultimoAvistamento = agora;
        possuiPosicaoConhecida = true;
        EstadoAtual = EstadoIAAssassino.Perseguindo;
    }

    public void EsquecerAlvo()
    {
        alvo = null;
        ultimaPosicaoConhecida = Vector2.zero;
        ultimoAvistamento = 0f;
        proximaAvaliacao = 0f;
        possuiPosicaoConhecida = false;
        EstadoAtual = EstadoIAAssassino.Patrulhando;
    }

    public void DefinirEstado(EstadoIAAssassino estado)
    {
        EstadoAtual = estado;
    }

    public void Resetar()
    {
        EsquecerAlvo();
        EstadoAtual = EstadoIAAssassino.Inativo;
    }
}

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
/// A IA só escolhe alvos que consegue ver, mantém a última posição observada
/// e separa perseguição de investigação. A movimentação e os RPCs de mapa
/// continuam sob responsabilidade do controlador do evento.
/// </summary>
internal sealed class InteligenciaArtificialAssassino
{
    private const float TempoMemoriaVisual = 4.0f;
    private const float IntervaloAvaliacaoAlvo = 0.20f;
    private const float MargemTrocaAlvo = 1.25f;
    private const float DistanciaChegadaInvestigacao = 0.35f;

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

    public bool EstaPerseguindo =>
        EstadoAtual == EstadoIAAssassino.Perseguindo;

    public Vector2 UltimaPosicaoConhecida => ultimaPosicaoConhecida;

    public Vector2 DestinoAtual
    {
        get
        {
            if (EstaInvestigando && possuiPosicaoConhecida)
                return ultimaPosicaoConhecida;

            return alvo != null
                ? alvo.GetTruePosition()
                : possuiPosicaoConhecida ? ultimaPosicaoConhecida : Vector2.zero;
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

        // Primeiro valida o alvo atual. Um jogador morto, desconectado ou
        // protegido por um estado especial nunca permanece como alvo.
        if (alvo != null && !podeSerAlvo(alvo))
            EsquecerAlvo();

        bool alvoVisivel = alvo != null && estaVisivel(alvo);

        if (alvoVisivel)
        {
            RegistrarAvistamento(alvo, agora);
            EstadoAtual = EstadoIAAssassino.Perseguindo;
        }
        else if (alvo != null)
        {
            float tempoSemVer = agora - ultimoAvistamento;

            if (!possuiPosicaoConhecida ||
                tempoSemVer > TempoMemoriaVisual)
            {
                EsquecerAlvo();
            }
            else if (Vector2.Distance(origem, ultimaPosicaoConhecida) <=
                     DistanciaChegadaInvestigacao)
            {
                // Chegou ao local onde viu o jogador pela última vez.
                // Não continua seguindo a posição atual através das paredes.
                EsquecerAlvo();
            }
            else
            {
                EstadoAtual = EstadoIAAssassino.Investigando;
            }
        }

        // A percepção é limitada a 5 avaliações por segundo. O alvo atual,
        // entretanto, continua tendo sua visibilidade atualizada a cada tick.
        if (agora < proximaAvaliacao)
        {
            if (alvo == null)
                EstadoAtual = EstadoIAAssassino.Patrulhando;

            return alvo;
        }

        proximaAvaliacao = agora + IntervaloAvaliacaoAlvo;

        PlayerControl melhorAlvoVisivel = null;
        float menorDistancia = float.MaxValue;

        foreach (PlayerControl jogador in jogadores)
        {
            if (jogador == null || jogador == npc ||
                !podeSerAlvo(jogador) || !estaVisivel(jogador))
                continue;

            float distancia = Vector2.Distance(
                origem, jogador.GetTruePosition());

            if (distancia < menorDistancia)
            {
                menorDistancia = distancia;
                melhorAlvoVisivel = jogador;
            }
        }

        if (alvo == null)
        {
            DefinirAlvo(melhorAlvoVisivel, agora);
        }
        else if (alvoVisivel)
        {
            float distanciaAtual = Vector2.Distance(
                origem, alvo.GetTruePosition());

            // Mantém o alvo atual para evitar alternância constante.
            // Só troca quando outro jogador estiver claramente mais perto.
            if (melhorAlvoVisivel != null &&
                melhorAlvoVisivel != alvo &&
                menorDistancia + MargemTrocaAlvo < distanciaAtual)
            {
                DefinirAlvo(melhorAlvoVisivel, agora);
            }
        }
        else if (melhorAlvoVisivel != null)
        {
            float distanciaInvestigacao = Vector2.Distance(
                origem, ultimaPosicaoConhecida);

            // Um jogador visível pode interromper uma investigação se estiver
            // significativamente mais próximo que o último local investigado.
            if (menorDistancia + MargemTrocaAlvo < distanciaInvestigacao)
                DefinirAlvo(melhorAlvoVisivel, agora);
        }

        if (alvo == null)
        {
            EstadoAtual = EstadoIAAssassino.Patrulhando;
            return null;
        }

        // Confirma a visibilidade do alvo escolhido. Se perdeu visão durante
        // a avaliação, conserva somente a posição que foi realmente observada.
        if (estaVisivel(alvo))
        {
            RegistrarAvistamento(alvo, agora);
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

    private void RegistrarAvistamento(PlayerControl jogador, float agora)
    {
        if (jogador == null)
            return;

        ultimaPosicaoConhecida = jogador.GetTruePosition();
        ultimoAvistamento = agora;
        possuiPosicaoConhecida = true;
    }

    private void DefinirAlvo(PlayerControl novoAlvo, float agora)
    {
        alvo = novoAlvo;
        proximaAvaliacao = agora + IntervaloAvaliacaoAlvo;

        if (alvo == null)
        {
            LimparMemoriaVisual();
            EstadoAtual = EstadoIAAssassino.Patrulhando;
            return;
        }

        RegistrarAvistamento(alvo, agora);
        EstadoAtual = EstadoIAAssassino.Perseguindo;
    }

    private void LimparMemoriaVisual()
    {
        ultimaPosicaoConhecida = Vector2.zero;
        ultimoAvistamento = 0f;
        possuiPosicaoConhecida = false;
    }

    public void EsquecerAlvo()
    {
        alvo = null;
        LimparMemoriaVisual();
        proximaAvaliacao = 0f;
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

using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Navegação em grade independente da IA do NPC.
/// Recebe consultas de colisão do chamador e devolve uma rota suavizada.
/// </summary>
internal static class PathfinderSeguidorMortal
{
    private static readonly Vector2Int[] Direcoes =
    {
        new(1, 0), new(0, 1), new(-1, 0), new(0, -1),
        new(1, 1), new(-1, 1), new(-1, -1), new(1, -1)
    };

    public static List<Vector2> Calcular(
        Vector2 inicio,
        Vector2 destino,
        Bounds limites,
        float tamanhoCelula,
        Func<Vector2, bool> celulaLivre,
        Func<Vector2, Vector2, bool> segmentoLivre,
        Func<Vector2, float> custoAdicional = null)
    {
        if (tamanhoCelula <= 0f || celulaLivre == null || segmentoLivre == null)
            return null;

        if (Vector2.Distance(inicio, destino) <= 0.08f)
            return new List<Vector2> { destino };

        if (segmentoLivre(inicio, destino) &&
            celulaLivre(destino))
            return new List<Vector2> { destino };

        // Ajusta a resolução ao tamanho real do mapa. Uma grade fixa de 0,30
        // pode ultrapassar o limite de células em mapas maiores e fazer o NPC
        // declarar que não existe rota mesmo antes de iniciar o A*.
        float areaMapa = Mathf.Max(1f, limites.size.x * limites.size.y);
        float tamanhoCelulaEfetivo = Mathf.Max(
            tamanhoCelula,
            Mathf.Sqrt(areaMapa / 36000f));

        // Não deixa a grade ficar tão grossa que passe por cima de corredores
        // estreitos. Se o mapa exigir mais que o dobro da resolução base,
        // a navegação falha com segurança e o controlador usa o desvio local.
        if (tamanhoCelulaEfetivo > tamanhoCelula * 2f)
            return null;

        limites.Expand(tamanhoCelulaEfetivo * 2f);
        Vector2 minimo = limites.min;
        int largura = Mathf.CeilToInt(limites.size.x / tamanhoCelulaEfetivo);
        int altura = Mathf.CeilToInt(limites.size.y / tamanhoCelulaEfetivo);

        if (largura < 2 || altura < 2 || (long)largura * altura > 45000)
            return null;

        int total = largura * altura;
        bool[] testada = new bool[total];
        bool[] livre = new bool[total];
        float[] custos = new float[total];
        int[] anteriores = new int[total];
        bool[] fechado = new bool[total];

        Array.Fill(custos, float.PositiveInfinity);
        Array.Fill(anteriores, -1);

        int inicioId = ParaIndice(inicio, minimo, tamanhoCelulaEfetivo, largura, altura);
        int destinoId = ParaIndice(destino, minimo, tamanhoCelulaEfetivo, largura, altura);

        if (inicioId < 0 || destinoId < 0)
            return null;

        if (!EncontrarCelulaLivre(ref inicioId, inicio, minimo, tamanhoCelulaEfetivo,
                largura, altura, livre, testada, celulaLivre, segmentoLivre) ||
            !EncontrarCelulaLivre(ref destinoId, destino, minimo, tamanhoCelulaEfetivo,
                largura, altura, livre, testada, celulaLivre, segmentoLivre))
            return null;

        PriorityQueue<int, float> abertos = new();
        custos[inicioId] = 0f;
        abertos.Enqueue(inicioId, Heuristica(inicioId, destinoId, largura));

        int expandidos = 0;
        const int limiteExpansoes = 24000;

        while (abertos.Count > 0 && expandidos < limiteExpansoes)
        {
            int atual = abertos.Dequeue();
            if (fechado[atual])
                continue;

            fechado[atual] = true;
            expandidos++;

            if (atual == destinoId)
            {
                List<Vector2> rota = Reconstruir(
                    inicioId, destinoId, anteriores, minimo,
                    tamanhoCelulaEfetivo, largura);

                if (rota == null || rota.Count == 0)
                    return null;

                if (segmentoLivre(rota[rota.Count - 1], destino))
                    rota.Add(destino);
                else
                    return null;

                return Suavizar(rota, segmentoLivre);
            }

            int x = atual % largura;
            int y = atual / largura;
            Vector2 pontoAtual = Centro(x, y, minimo, tamanhoCelulaEfetivo);

            for (int i = 0; i < Direcoes.Length; i++)
            {
                Vector2Int direcao = Direcoes[i];
                int nx = x + direcao.x;
                int ny = y + direcao.y;

                if (nx < 0 || ny < 0 || nx >= largura || ny >= altura)
                    continue;

                int vizinho = ny * largura + nx;
                if (fechado[vizinho] || !CelulaLivre(vizinho, minimo,
                        tamanhoCelulaEfetivo, largura, livre, testada, celulaLivre))
                    continue;

                // Impede cortar quinas: ambos os lados da diagonal precisam estar livres.
                if (direcao.x != 0 && direcao.y != 0)
                {
                    int lateralX = y * largura + nx;
                    int lateralY = ny * largura + x;

                    if (!CelulaLivre(lateralX, minimo, tamanhoCelulaEfetivo,
                            largura, livre, testada, celulaLivre) ||
                        !CelulaLivre(lateralY, minimo, tamanhoCelulaEfetivo,
                            largura, livre, testada, celulaLivre))
                        continue;
                }

                Vector2 pontoVizinho = Centro(nx, ny, minimo, tamanhoCelulaEfetivo);
                if (!segmentoLivre(pontoAtual, pontoVizinho))
                    continue;

                float passo = direcao.x != 0 && direcao.y != 0
                    ? 1.41421356f : 1f;
                // O mapa salvo orienta a rota, mas seu custo é apenas uma
                // preferência: um trecho mapeado como obstáculo ainda pode ser
                // usado quando a geometria real do jogo permitir e for necessário.
                float penalidadeMapa = custoAdicional != null
                    ? Mathf.Max(0f, custoAdicional(pontoVizinho))
                    : 0f;
                float novoCusto = custos[atual] + passo + penalidadeMapa;

                if (novoCusto >= custos[vizinho])
                    continue;

                custos[vizinho] = novoCusto;
                anteriores[vizinho] = atual;
                float prioridade = novoCusto +
                    Heuristica(vizinho, destinoId, largura);
                abertos.Enqueue(vizinho, prioridade);
            }
        }

        return null;
    }

    private static bool EncontrarCelulaLivre(
        ref int indice,
        Vector2 origem,
        Vector2 minimo,
        float passo,
        int largura,
        int altura,
        bool[] livre,
        bool[] testada,
        Func<Vector2, bool> celulaLivre,
        Func<Vector2, Vector2, bool> segmentoLivre)
    {
        if (CelulaLivre(indice, minimo, passo, largura, livre, testada, celulaLivre))
        {
            Vector2 centroInicial = Centro(indice % largura, indice / largura, minimo, passo);
            if (segmentoLivre(origem, centroInicial))
                return true;
        }

        int x0 = indice % largura;
        int y0 = indice / largura;

        for (int raio = 1; raio <= 10; raio++)
        {
            float menorDistancia = float.PositiveInfinity;
            int melhor = -1;

            for (int dx = -raio; dx <= raio; dx++)
            for (int dy = -raio; dy <= raio; dy++)
            {
                if (Mathf.Abs(dx) != raio && Mathf.Abs(dy) != raio)
                    continue;

                int x = x0 + dx;
                int y = y0 + dy;
                if (x < 0 || y < 0 || x >= largura || y >= altura)
                    continue;

                int candidato = y * largura + x;
                if (!CelulaLivre(candidato, minimo, passo, largura,
                        livre, testada, celulaLivre))
                    continue;

                Vector2 ponto = Centro(x, y, minimo, passo);
                if (!PodeConectarOrigem(origem, ponto, celulaLivre, segmentoLivre, out float distancia))
                    continue;

                if (distancia < menorDistancia)
                {
                    menorDistancia = distancia;
                    melhor = candidato;
                }
            }

            if (melhor >= 0)
            {
                indice = melhor;
                return true;
            }
        }

        return false;
    }

    // A ligação da posição real até o centro da célula também precisa estar livre.
    private static bool PodeConectarOrigem(
        Vector2 origem, Vector2 ponto, Func<Vector2, bool> celulaLivre,
        Func<Vector2, Vector2, bool> segmentoLivre, out float distancia)
    {
        distancia = Vector2.Distance(origem, ponto);
        return celulaLivre(ponto) && segmentoLivre(origem, ponto);
    }

    private static bool CelulaLivre(
        int indice,
        Vector2 minimo,
        float passo,
        int largura,
        bool[] livre,
        bool[] testada,
        Func<Vector2, bool> consulta)
    {
        if (indice < 0 || indice >= livre.Length)
            return false;

        if (!testada[indice])
        {
            int x = indice % largura;
            int y = indice / largura;
            livre[indice] = consulta(Centro(x, y, minimo, passo));
            testada[indice] = true;
        }

        return livre[indice];
    }

    private static int ParaIndice(
        Vector2 ponto, Vector2 minimo, float passo, int largura, int altura)
    {
        int x = Mathf.FloorToInt((ponto.x - minimo.x) / passo);
        int y = Mathf.FloorToInt((ponto.y - minimo.y) / passo);

        if (x < 0 || y < 0 || x >= largura || y >= altura)
            return -1;

        return y * largura + x;
    }

    private static Vector2 Centro(int x, int y, Vector2 minimo, float passo)
        => minimo + new Vector2((x + 0.5f) * passo, (y + 0.5f) * passo);

    private static float Heuristica(int a, int b, int largura)
    {
        int ax = a % largura;
        int ay = a / largura;
        int bx = b % largura;
        int by = b / largura;
        int dx = Mathf.Abs(ax - bx);
        int dy = Mathf.Abs(ay - by);
        return Mathf.Max(dx, dy) + 0.41421356f * Mathf.Min(dx, dy);
    }

    private static List<Vector2> Reconstruir(
        int inicio, int fim, int[] anteriores,
        Vector2 minimo, float passo, int largura)
    {
        List<Vector2> rota = new();
        int atual = fim;
        int limite = anteriores.Length + 1;

        while (atual != inicio && limite-- > 0)
        {
            int x = atual % largura;
            int y = atual / largura;
            rota.Add(Centro(x, y, minimo, passo));
            atual = anteriores[atual];
            if (atual < 0)
                return null;
        }

        if (limite <= 0)
            return null;

        rota.Add(Centro(inicio % largura, inicio / largura, minimo, passo));
        rota.Reverse();
        return rota;
    }

    private static List<Vector2> Suavizar(
        List<Vector2> rota,
        Func<Vector2, Vector2, bool> segmentoLivre)
    {
        if (rota == null || rota.Count < 3)
            return rota;

        List<Vector2> resultado = new() { rota[0] };
        int atual = 0;

        while (atual < rota.Count - 1)
        {
            int escolhido = atual + 1;

            // Procura o ponto mais distante alcançável em linha reta.
            for (int candidato = rota.Count - 1; candidato > atual + 1; candidato--)
            {
                if (segmentoLivre(rota[atual], rota[candidato]))
                {
                    escolhido = candidato;
                    break;
                }
            }

            resultado.Add(rota[escolhido]);
            atual = escolhido;
        }

        return resultado;
    }
}

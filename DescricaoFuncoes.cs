using System;
using System.Collections.Generic;

namespace YouTubeVoteBridge;

/// <summary>
/// Textos curtos apresentados ao jogador pelo comando /m.
/// As funções não cadastradas recebem uma mensagem neutra em vez de
/// uma descrição inventada para funções de outros mods.
/// </summary>
internal static class DescricaoFuncoes
{
    private static readonly Dictionary<string, string> descricoes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Tripulante"] =
                "Complete suas tarefas e ajude a identificar os impostores. " +
                "Discuta as pistas e vote para expulsar quem estiver sabotando a nave.",

            ["Crewmate"] =
                "Complete suas tarefas e ajude a identificar os impostores. " +
                "Discuta as pistas e vote para expulsar quem estiver sabotando a nave.",

            ["Impostor"] =
                "Elimine os tripulantes sem ser descoberto. Use sabotagens, " +
                "aproveite oportunidades e evite levantar suspeitas.",

            ["Cientista"] =
                "Use o painel de sinais vitais para acompanhar quem está vivo " +
                "e perceber quando uma morte acontece. A bateria limita o uso do painel.",

            ["Scientist"] =
                "Use o painel de sinais vitais para acompanhar quem está vivo " +
                "e perceber quando uma morte acontece. A bateria limita o uso do painel.",

            ["Engenheiro"] =
                "Você pode usar os dutos para se deslocar pelo mapa. " +
                "A utilização dos dutos possui limitações de tempo.",

            ["Engineer"] =
                "Você pode usar os dutos para se deslocar pelo mapa. " +
                "A utilização dos dutos possui limitações de tempo.",

            ["Anjo da Guarda"] =
                "Mesmo depois de morrer, você pode proteger um jogador vivo " +
                "com um escudo temporário contra uma tentativa de assassinato.",

            ["Guardian Angel"] =
                "Mesmo depois de morrer, você pode proteger um jogador vivo " +
                "com um escudo temporário contra uma tentativa de assassinato.",

            ["Metamorfo"] =
                "Você é um impostor que pode assumir temporariamente a aparência " +
                "de outro jogador para confundir os tripulantes.",

            ["Shapeshifter"] =
                "Você é um impostor que pode assumir temporariamente a aparência " +
                "de outro jogador para confundir os tripulantes.",

            ["Sirene"] =
                "Quando você é eliminado, emite um sinal que alerta os jogadores " +
                "próximos sobre o local da morte.",

            ["Noisemaker"] =
                "Quando você é eliminado, emite um sinal que alerta os jogadores " +
                "próximos sobre o local da morte.",

            ["Rastreador"] =
                "Use sua habilidade para acompanhar a localização de outro jogador " +
                "e tentar descobrir quem está agindo de forma suspeita.",

            ["Tracker"] =
                "Use sua habilidade para acompanhar a localização de outro jogador " +
                "e tentar descobrir quem está agindo de forma suspeita.",

            ["Fantasma"] =
                "Você é um impostor com a habilidade de ficar invisível " +
                "temporariamente para se aproximar ou escapar sem ser visto.",

            ["Phantom"] =
                "Você é um impostor com a habilidade de ficar invisível " +
                "temporariamente para se aproximar ou escapar sem ser visto.",

            ["Detetive"] =
                "Investigue os acontecimentos e use as pistas disponíveis " +
                "para descobrir quem está cometendo os assassinatos.",

            ["Detective"] =
                "Investigue os acontecimentos e use as pistas disponíveis " +
                "para descobrir quem está cometendo os assassinatos.",

            ["Víbora"] =
                "Você é um impostor cuja eliminação dissolve o corpo da vítima " +
                "com ácido depois de um curto período, dificultando a descoberta.",

            ["Viper"] =
                "Você é um impostor cuja eliminação dissolve o corpo da vítima " +
                "com ácido depois de um curto período, dificultando a descoberta.",

            ["Juiz"] =
                "Durante a reunião, use sua habilidade especial de julgamento " +
                "para decidir o destino de um jogador, conforme as regras da partida.",

            ["Judge"] =
                "Durante a reunião, use sua habilidade especial de julgamento " +
                "para decidir o destino de um jogador, conforme as regras da partida.",

            ["Anjo Traidor"] =
                "Você pertence ao time impostor. Ao desaparecer, sua função passa " +
                "a se comportar como a de um Anjo da Guarda no seu cliente. " +
                "Sua habilidade de proteção puxa o alvo até você; uma nova ativação " +
                "pode eliminá-lo.",

            ["Fantasma Impostor"] =
                "Você foi eliminado e agora é um fantasma do time impostor. " +
                "Você não pode vencer sozinho: a vitória depende do seu time.",

            ["Fantasma Tripulante"] =
                "Você foi eliminado e agora é um fantasma do time dos tripulantes. " +
                "Continue acompanhando a partida enquanto os vivos concluem as tarefas."
        };

    internal static string Obter(PlayerControl jogador)
    {
        if (jogador == null || jogador.Data == null)
            return "Não foi possível identificar sua função nesta partida.";

        string nomeFuncao = SistemaNomesLocais.ObterNomeFuncao(jogador);
        string nomeBase = nomeFuncao ?? "Função desconhecida";
        bool oportunista = EventoOportunista.EhOportunista(jogador);

        // O atributo é apresentado separadamente para não confundir
        // Oportunista com uma função que substitui a função original.
        if (oportunista)
            nomeBase = nomeBase.Replace(" + Oportunista", string.Empty, StringComparison.OrdinalIgnoreCase);

        string descricao;
        if (!descricoes.TryGetValue(nomeBase, out descricao))
        {
            descricao =
                "Esta função personalizada não possui uma descrição cadastrada " +
                "no YouTubeVoteBridge. Consulte as informações do mod que a adicionou.";
        }

        string resultado =
            "<color=#FFD54A> Sua função: " + nomeBase + "</color>\n" +
            descricao;

        if (oportunista)
        {
            resultado +=
                "\n\n<color=#B56BFF>Atributo: Oportunista</color>\n" +
                "Se você continuar vivo até o fim, vence junto com o time vencedor. " +
                "Esse atributo não substitui sua função principal.";
        }

        return resultado;
    }
}

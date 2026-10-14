using System;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

// Usa o mesmo envio individual de LogicOptions dos eventos de velocidade:
// aplica as opções localmente no host e envia um pacote de opções específico
// para cada cliente remoto, sem depender de um broadcast global.
internal sealed class EventoVisaoReduzida : EventoVotacao
{
    private const float DuracaoSegundos = 20f;
    private const float MultiplicadorVisao = 0.15f;
    private const float IntervaloReenvio = 0.75f;
    private const float IntervaloRestauracao = 0.5f;
    private const float DuracaoTentativasRestauracao = 3f;

    private static float? VisaoTripulanteOriginal;
    private static float? VisaoImpostorOriginal;
    private static float ExpiraEm;
    private static float ProximoReenvio;
    private static float RestauracaoAte;
    private static float ProximaTentativaRestauracao;

    public override string Nome => "Visão reduzida para todos por 20 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        // Não captura a visão já reduzida como se fosse a configuração original.
        if (ExpiraEm > 0f || RestauracaoAte > 0f)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Visão] Evento ignorado: a alteração anterior ainda está ativa ou sendo restaurada.");
            return;
        }

        try
        {
            var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
            if (opcoes == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "[Visão] Evento cancelado: opções da partida indisponíveis.");
                return;
            }

            if (!VisaoTripulanteOriginal.HasValue || !VisaoImpostorOriginal.HasValue)
            {
                VisaoTripulanteOriginal = opcoes.GetFloat(FloatOptionNames.CrewLightMod);
                VisaoImpostorOriginal = opcoes.GetFloat(FloatOptionNames.ImpostorLightMod);
            }

            float tripulante = Mathf.Clamp(VisaoTripulanteOriginal.Value * MultiplicadorVisao, 0.05f, 5f);
            float impostor = Mathf.Clamp(VisaoImpostorOriginal.Value * MultiplicadorVisao, 0.05f, 5f);

            AplicarConfiguracaoLocal(tripulante, impostor);
            EnviarConfiguracaoParaClientes(tripulante, impostor);

            ExpiraEm = Time.realtimeSinceStartup + DuracaoSegundos;
            ProximoReenvio = Time.realtimeSinceStartup + IntervaloReenvio;

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Visão] Ativada por {DuracaoSegundos}s. Crew={tripulante:0.####}, " +
                $"Impostor={impostor:0.####}. O envio individual será repetido a cada {IntervaloReenvio}s.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Visão] Falha ao ativar: " + ex);
        }
    }

    public static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        float agora = Time.realtimeSinceStartup;

        // Depois do efeito, reenviar a visão original por alguns segundos.
        // Isso combate outras atualizações de opções que possam sobrescrever
        // o primeiro pacote de restauração.
        if (RestauracaoAte > 0f)
        {
            if (agora >= ProximaTentativaRestauracao)
            {
                ProximaTentativaRestauracao = agora + IntervaloRestauracao;
                TentarRestaurarConfiguracao();
            }

            if (agora >= RestauracaoAte)
            {
                RestauracaoAte = 0f;
                ProximaTentativaRestauracao = 0f;
                ExpiraEm = 0f;
                ProximoReenvio = 0f;
                VisaoTripulanteOriginal = null;
                VisaoImpostorOriginal = null;

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[Visão] Ciclo de restauração concluído.");
            }

            return;
        }

        if (ExpiraEm <= 0f)
            return;

        if (agora >= ExpiraEm)
        {
            ExpiraEm = 0f;
            RestauracaoAte = agora + DuracaoTentativasRestauracao;
            ProximaTentativaRestauracao = 0f;
            TentarRestaurarConfiguracao();
            ProximaTentativaRestauracao = agora + IntervaloRestauracao;
            return;
        }

        if (agora >= ProximoReenvio)
        {
            ProximoReenvio = agora + IntervaloReenvio;
            if (VisaoTripulanteOriginal.HasValue && VisaoImpostorOriginal.HasValue)
            {
                float tripulante = Mathf.Clamp(VisaoTripulanteOriginal.Value * MultiplicadorVisao, 0.05f, 5f);
                float impostor = Mathf.Clamp(VisaoImpostorOriginal.Value * MultiplicadorVisao, 0.05f, 5f);
                AplicarConfiguracaoLocal(tripulante, impostor);
                EnviarConfiguracaoParaClientes(tripulante, impostor);
            }
        }
    }

    private static void TentarRestaurarConfiguracao()
    {
        if (!VisaoTripulanteOriginal.HasValue || !VisaoImpostorOriginal.HasValue)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Visão] Não há valores originais salvos para restaurar.");
            return;
        }

        try
        {
            float tripulante = VisaoTripulanteOriginal.Value;
            float impostor = VisaoImpostorOriginal.Value;

            AplicarConfiguracaoLocal(tripulante, impostor);
            EnviarConfiguracaoParaClientes(tripulante, impostor);

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"[Visão] Reenvio de restauração: Crew={tripulante:0.####}, " +
                $"Impostor={impostor:0.####}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Visão] Falha ao restaurar configuração original: " + ex);
        }
    }

    private static void AplicarConfiguracaoLocal(float tripulante, float impostor)
    {
        var opcoes = GameOptionsManager.Instance?.CurrentGameOptions;
        if (opcoes == null)
            return;

        opcoes.SetFloat(FloatOptionNames.CrewLightMod, tripulante);
        opcoes.SetFloat(FloatOptionNames.ImpostorLightMod, impostor);

        if (GameManager.Instance?.LogicComponents != null)
        {
            foreach (GameLogicComponent componente in GameManager.Instance.LogicComponents)
            {
                if (componente is LogicOptions logicOptions)
                    logicOptions.SetGameOptions(opcoes);
            }
        }

        GameOptionsManager.Instance.CurrentGameOptions = opcoes;
    }

    private static void EnviarConfiguracaoParaClientes(float tripulante, float impostor)
    {
        if (PlayerControl.AllPlayerControls == null)
            return;

        int enviados = 0;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador?.Data == null || jogador.Data.Disconnected || jogador.AmOwner)
                continue;

            if (EventoVelocidadeJogadores.EnviarVisaoBruta(jogador, tripulante, impostor))
                enviados++;
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[Visão] Pacotes individuais enviados para {enviados} cliente(s) remoto(s); " +
            "isso confirma envio pelo host, não recebimento/aplicação pelo jogo.");
    }
}

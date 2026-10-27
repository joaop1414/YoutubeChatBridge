using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Mata jogadores que estão encostados na passagem quando o jogo fecha portas.
/// O efeito é controlado pelo host e só fica ativo quando selecionado no F7.
/// </summary>
internal sealed class EventoMorrerQuandoPortaFechar : EventoVotacao
{
    private static bool armado;
    private static bool ativo;

    public override string Nome => "Morra Quando a Porta Fechar na Sua Cara";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        armado = true;
        ativo = LobbyTagManager.PartidaIniciada();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Porta na cara] Evento armado; ativo=" + ativo + ".");
    }

    internal static void IniciarPartida()
    {
        if (armado)
        {
            ativo = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Porta na cara] Evento ativado no início da partida.");
        }
    }

    internal static void AoFecharPortas(SystemTypes salaFechada)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null || MeetingHud.Instance != null)
            return;

        try
        {
            // CloseDoorsOfType recebe a sala cujas portas estão sendo fechadas.
            // Restringe a checagem à área dessa sala para não atingir jogadores
            // perto de portas de outras salas (ex.: Elétrica vs. Motor).
            if (ShipStatus.Instance.FastRooms == null ||
                !ShipStatus.Instance.FastRooms.TryGetValue(salaFechada, out PlainShipRoom sala) ||
                sala == null || sala.roomArea == null)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[F7/Porta na cara] Não foi possível resolver a área da sala " + salaFechada + ".");
                return;
            }

            List<Collider2D> colisoresPortas = new();
            foreach (Collider2D colisor in UnityEngine.Object.FindObjectsOfType<Collider2D>())
            {
                if (colisor == null || !colisor.enabled || !PareceSerPorta(colisor.transform))
                    continue;
                colisoresPortas.Add(colisor);
            }

            if (colisoresPortas.Count == 0)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                    "[F7/Porta na cara] Nenhum colisor de porta reconhecido no fechamento.");
                return;
            }

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.Data == null ||
                    jogador.Data.IsDead || jogador.Data.Disconnected)
                    continue;

                Vector2 posicao = jogador.transform.position;
                // A posição do jogador precisa estar dentro da área da sala
                // associada ao fechamento que acabou de ocorrer.
                if (!sala.roomArea.OverlapPoint(posicao))
                    continue;

                bool atingido = false;
                foreach (Collider2D porta in colisoresPortas)
                {
                    if (porta == null) continue;
                    Vector2 pontoMaisProximo = porta.ClosestPoint(posicao);
                    if (Vector2.Distance(posicao, pontoMaisProximo) <= 0.32f)
                    {
                        atingido = true;
                        break;
                    }
                }

                if (!atingido) continue;

                try
                {
                    jogador.RpcMurderPlayer(jogador, true);
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Porta na cara] " + jogador.Data.PlayerName +
                        " foi atingido por uma porta fechando e morreu.");
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "[F7/Porta na cara] Não foi possível matar PlayerId=" +
                        jogador.PlayerId + ": " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Porta na cara] Falha ao verificar jogadores próximos às portas: " + ex);
        }
    }

    private static bool PareceSerPorta(Transform transform)
    {
        for (Transform atual = transform; atual != null; atual = atual.parent)
        {
            string nome = atual.name ?? string.Empty;
            if (nome.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("porta", StringComparison.OrdinalIgnoreCase) >= 0 ||
                nome.IndexOf("decon", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            foreach (Component componente in atual.GetComponents<Component>())
            {
                if (componente == null) continue;
                string tipo = componente.GetType().Name;
                if (tipo.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    tipo.IndexOf("decon", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        return false;
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
    }
}

// O jogo chama CloseDoorsOfType quando solicita o fechamento de portas de uma sala.
[HarmonyPatch(typeof(ShipStatus), nameof(ShipStatus.CloseDoorsOfType))]
internal static class EventoMorrerQuandoPortaFecharPatch
{
    public static void Postfix(SystemTypes __0)
    {
        EventoMorrerQuandoPortaFechar.AoFecharPortas(__0);
    }
}

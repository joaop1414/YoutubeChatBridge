using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoDutosInstaveis : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static readonly HashSet<byte> alterandoDuto = new();
    public override string Nome => "Dutos instáveis: você pode sair por outro duto ou ser expulso sozinho";

    private static void Log(string mensagem) =>
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Dutos instáveis] " + mensagem);

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
        {
            Log("Executar ignorado: este cliente não é o host.");
            return;
        }

        armado = true;
        bool partidaIniciada = LobbyTagManager.PartidaIniciada();
        if (partidaIniciada) ativo = true;
        Log($"Evento selecionado/armado. PartidaIniciada={partidaIniciada}, armado={armado}, ativo={ativo}.");
    }

    internal static void IniciarPartida()
    {
        if (armado)
        {
            ativo = true;
            Log("IniciarPartida: evento ativado.");
        }
        else
        {
            Log("IniciarPartida chamado, mas o evento não estava armado.");
        }
    }

    internal static void AoEntrarNoDuto(PlayerPhysics fisica, Vent duto)
    {
        if (!ativo)
        {
            Log($"Entrada detectada, mas evento INATIVO (armado={armado}). Nenhum sorteio feito.");
            return;
        }

        bool ehHost = YouTubeVoteBridgePlugin.EhHost();
        if (!ehHost)
        {
            Log("Entrada detectada, mas este cliente não é o host. Nenhum sorteio feito.");
            return;
        }
        if (fisica == null) { Log("Entrada ignorada: PlayerPhysics nulo."); return; }
        if (duto == null) { Log("Entrada ignorada: Vent nulo."); return; }
        if (ShipStatus.Instance == null) { Log("Entrada ignorada: ShipStatus.Instance nulo."); return; }
        if (MeetingHud.Instance != null) { Log("Entrada ignorada: reunião em andamento."); return; }

        PlayerControl jogador = null;
        foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            if (p != null && p.MyPhysics == fisica) { jogador = p; break; }

        if (jogador == null)
        {
            Log("Entrada ignorada: nenhum PlayerControl corresponde ao PlayerPhysics recebido.");
            return;
        }
        if (jogador.Data == null) { Log($"Entrada ignorada: Data nulo para PlayerId={jogador.PlayerId}."); return; }
        if (jogador.Data.IsDead) { Log($"Entrada ignorada: PlayerId={jogador.PlayerId} está morto."); return; }
        if (jogador.Data.Disconnected) { Log($"Entrada ignorada: PlayerId={jogador.PlayerId} está desconectado."); return; }
        if (alterandoDuto.Contains(jogador.PlayerId))
        {
            Log($"Entrada ignorada: PlayerId={jogador.PlayerId} já está sendo movido pelo evento.");
            return;
        }

        // 35% de chance de expulsão imediata; 30% de sair por outro duto; 35% sem efeito.
        int sorteio = System.Random.Shared.Next(100);
        string resultado = sorteio < 35 ? "EXPULSÃO (35%)" :
            sorteio < 65 ? "OUTRO DUTO (30%)" : "SEM EFEITO (35%)";
        Log($"SORTEIO: Jogador={jogador.Data.PlayerName}, PlayerId={jogador.PlayerId}, " +
            $"Duto={duto.Id} ({duto.name}), número={sorteio}, resultado={resultado}.");

        if (sorteio >= 65) return;

        try
        {
            alterandoDuto.Add(jogador.PlayerId);
            if (sorteio < 35)
            {
                Log($"Executando expulsão: PlayerId={jogador.PlayerId}, VentId={duto.Id}.");
                fisica.RpcExitVent(duto.Id);
                Log($"RPC RpcExitVent enviado para PlayerId={jogador.PlayerId}.");
                return;
            }

            List<Vent> candidatos = new();
            foreach (Vent v in ShipStatus.Instance.AllVents)
                if (v != null && v.Id != duto.Id)
                    candidatos.Add(v);

            if (candidatos.Count == 0)
            {
                Log($"Sorteado OUTRO DUTO, mas não há dutos de destino. Duto atual={duto.Id}.");
                return;
            }

            Vent destino = candidatos[System.Random.Shared.Next(candidatos.Count)];
            Log($"Executando troca: PlayerId={jogador.PlayerId}, origem={duto.Id}, destino={destino.Id} ({destino.name}).");
            fisica.RpcExitVent(duto.Id);
            Vector3 pos = destino.transform.position;
            if (jogador.NetTransform != null)
            {
                jogador.NetTransform.RpcSnapTo(new Vector2(pos.x, pos.y));
                Log($"RpcSnapTo enviado para PlayerId={jogador.PlayerId}, posição=({pos.x}, {pos.y}).");
            }
            else
            {
                Log($"NetTransform nulo para PlayerId={jogador.PlayerId}; não foi possível enviar RpcSnapTo.");
            }

            fisica.RpcEnterVent(destino.Id);
            Log($"RPC RpcEnterVent enviado para PlayerId={jogador.PlayerId}, destino={destino.Id}.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Dutos instáveis] Exceção durante o efeito: " + ex);
        }
        finally
        {
            alterandoDuto.Remove(jogador.PlayerId);
        }
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        alterandoDuto.Clear();
        Log("Evento resetado: ativo=false, armado=false.");
    }
}

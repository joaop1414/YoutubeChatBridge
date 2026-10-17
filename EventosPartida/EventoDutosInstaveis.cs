using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoDutosInstaveis : EventoVotacao
{
    private static bool armado;
    private static bool ativo;
    private static readonly HashSet<byte> alterandoDuto = new();
    private sealed class TrocaDutoPendente
    {
        public byte PlayerId;
        public int VentId;
        public float EntrarEm;
        public float LiberarEm = -1f;
    }
    private static readonly Dictionary<byte, TrocaDutoPendente> trocasPendentes = new();
    private sealed class ExpulsaoPendente
    {
        public int VentId;
        public float ExecutarEm;
        public float LiberarEm = -1f;
    }
    private static readonly Dictionary<byte, ExpulsaoPendente> expulsoesPendentes = new();
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

    internal static void Atualizar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            (trocasPendentes.Count == 0 && expulsoesPendentes.Count == 0))
            return;

        float agora = Time.realtimeSinceStartup;

        // A expulsão usa o mesmo RpcBootFromVent dos eventos que expulsam
        // um jogador aleatório ou todos dos dutos. A espera evita expulsar
        // o jogador no mesmo instante em que ele entra e ficar preso no duto.
        List<byte> idsExpulsao = new(expulsoesPendentes.Keys);
        foreach (byte playerId in idsExpulsao)
        {
            if (!expulsoesPendentes.TryGetValue(playerId, out ExpulsaoPendente expulsao))
                continue;

            PlayerControl jogadorExpulsao = null;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
                if (p != null && p.PlayerId == playerId) { jogadorExpulsao = p; break; }

            if (jogadorExpulsao == null || jogadorExpulsao.Data == null ||
                jogadorExpulsao.Data.IsDead || jogadorExpulsao.Data.Disconnected ||
                ShipStatus.Instance == null)
            {
                expulsoesPendentes.Remove(playerId);
                alterandoDuto.Remove(playerId);
                continue;
            }

            if (expulsao.LiberarEm >= 0f)
            {
                if (agora >= expulsao.LiberarEm)
                {
                    expulsoesPendentes.Remove(playerId);
                    alterandoDuto.Remove(playerId);
                    Log($"Expulsão concluída para PlayerId={playerId}; bloqueio liberado.");
                }
                continue;
            }

            if (agora < expulsao.ExecutarEm)
                continue;

            try
            {
                jogadorExpulsao.MyPhysics?.RpcBootFromVent(expulsao.VentId);
                expulsao.LiberarEm = agora + 0.8f;
                Log($"RpcBootFromVent enviado após espera para PlayerId={playerId}, VentId={expulsao.VentId}.");
            }
            catch (Exception ex)
            {
                expulsoesPendentes.Remove(playerId);
                alterandoDuto.Remove(playerId);
                Log($"Falha ao expulsar PlayerId={playerId} do duto: {ex.Message}");
            }
        }
        List<byte> ids = new(trocasPendentes.Keys);
        foreach (byte playerId in ids)
        {
            if (!trocasPendentes.TryGetValue(playerId, out TrocaDutoPendente troca))
                continue;

            PlayerControl jogador = null;
            foreach (PlayerControl p in PlayerControl.AllPlayerControls)
            {
                if (p != null && p.PlayerId == playerId)
                {
                    jogador = p;
                    break;
                }
            }

            if (jogador == null || jogador.Data == null || jogador.Data.IsDead ||
                jogador.Data.Disconnected || ShipStatus.Instance == null)
            {
                trocasPendentes.Remove(playerId);
                alterandoDuto.Remove(playerId);
                Log($"Troca pendente cancelada para PlayerId={playerId}: jogador indisponível.");
                continue;
            }

            if (troca.LiberarEm >= 0f)
            {
                if (agora >= troca.LiberarEm)
                {
                    trocasPendentes.Remove(playerId);
                    alterandoDuto.Remove(playerId);
                    Log($"Troca concluída para PlayerId={playerId}; bloqueio liberado.");
                }
                continue;
            }

            if (agora < troca.EntrarEm)
                continue;

            Vent destino = null;
            foreach (Vent v in ShipStatus.Instance.AllVents)
            {
                if (v != null && v.Id == troca.VentId)
                {
                    destino = v;
                    break;
                }
            }

            if (destino == null)
            {
                trocasPendentes.Remove(playerId);
                alterandoDuto.Remove(playerId);
                Log($"Troca pendente cancelada: duto destino {troca.VentId} não existe mais.");
                continue;
            }

            try
            {
                // Espera após o SnapTo para o cliente estabilizar a posição antes de entrar.
                jogador.MyPhysics?.RpcEnterVent(destino.Id);
                troca.LiberarEm = agora + 0.5f;
                Log($"RpcEnterVent enviado após SnapTo para PlayerId={playerId}, destino={destino.Id}.");
            }
            catch (Exception ex)
            {
                trocasPendentes.Remove(playerId);
                alterandoDuto.Remove(playerId);
                Log($"Falha ao entrar no duto destino para PlayerId={playerId}: {ex.Message}");
            }
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

        bool manterBloqueio = false;
        try
        {
            alterandoDuto.Add(jogador.PlayerId);
            if (sorteio < 35)
            {
                // Dá tempo para a animação/estado de entrada no duto se estabelecer.
                expulsoesPendentes[jogador.PlayerId] = new ExpulsaoPendente
                {
                    VentId = duto.Id,
                    ExecutarEm = Time.realtimeSinceStartup + 1.25f
                };
                manterBloqueio = true;
                Log($"Expulsão agendada para PlayerId={jogador.PlayerId}, VentId={duto.Id}, atraso=1,25s; será usado RpcBootFromVent.");
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
            // Primeiro sai do duto atual, depois força a posição do jogador para o destino.
            fisica.RpcExitVent(duto.Id);
            Vector3 pos = destino.transform.position;
            if (jogador.NetTransform != null)
            {
                Vector2 destinoLocal = new(pos.x, pos.y);
                // RPC sincroniza os clientes remotos; aplicar a posição localmente
                // também é necessário quando o alvo é o próprio host.
                jogador.NetTransform.RpcSnapTo(destinoLocal);
                jogador.transform.position = new Vector3(pos.x, pos.y, jogador.transform.position.z);
                Log($"RpcSnapTo enviado e posição local aplicada para PlayerId={jogador.PlayerId}, posição=({pos.x}, {pos.y}).");
            }
            else
            {
                Log($"NetTransform nulo para PlayerId={jogador.PlayerId}; não foi possível enviar RpcSnapTo.");
                return;
            }

            // Não entra imediatamente: dá tempo para o SnapTo ser aplicado antes do RPC do duto.
            trocasPendentes[jogador.PlayerId] = new TrocaDutoPendente
            {
                PlayerId = jogador.PlayerId,
                VentId = destino.Id,
                EntrarEm = Time.realtimeSinceStartup + 0.16f
            };
            manterBloqueio = true;
            Log($"Entrada no duto {destino.Id} agendada para daqui a 0,16s após SnapTo.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("[F7/Dutos instáveis] Exceção durante o efeito: " + ex);
        }
        finally
        {
            if (!manterBloqueio)
                alterandoDuto.Remove(jogador.PlayerId);
        }
    }

    internal static void Resetar()
    {
        ativo = false;
        armado = false;
        alterandoDuto.Clear();
        trocasPendentes.Clear();
        expulsoesPendentes.Clear();
        Log("Evento resetado: ativo=false, armado=false.");
    }
}

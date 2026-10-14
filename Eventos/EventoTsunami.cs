using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using AmongUs.GameOptions;
using Hazel;
using HarmonyLib;
using InnerNet;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoTsunami : EventoVotacao
{
    public override string Nome => "Tsunami";
    public static bool Ativo => ativo;
    public static bool AvisoAtivo => ativo || avisoLocalAtivo;
    public static bool FlashReatorCianoAtivo => ativo && flashReatorCianoAtivo;
    private const byte PlayerIdIndicador = 254;
    private const float Velocidade = 3.0f;
    private const float Intervalo = 0.08f;
    private const float ProtecaoDuto = 1.0f;
    private const float AlcanceDutoTsunami = 0.65f;
    private const float AlcanceIdentificarDutoTsunami = 0.85f;
    private const float Alcance = 0.85f;
    private const float IntervaloCicloReator = 1.5f;
    private const float AtrasoInicialReator = 1.0f;
    private const float AtrasoMorteTsunami = 2.0f;

    private static PlayerControl indicador;
    private static Vector2 posicao;
    private static float proximaAtualizacao;
    private static float fimProtecao;
    private static bool ativo;
    // Inicialização de rede do indicador em etapas, como no NPC seguidor/EHR.
    private static int etapaInicializacaoIndicador;
    private static float proximaEtapaInicializacaoIndicador;
    private static float proximaSabotagem;
    private static float repararReatorAte;
    private static bool reparoPendente;
    private static bool flashReatorCianoAtivo;
    private static readonly Dictionary<byte, string> nomesOriginais = new();
    private static readonly HashSet<byte> nomesAlteradosRede = new();
    private static readonly HashSet<byte> atingidos = new();
    // Só jogadores que realmente chegaram à etapa de troca para Ghost podem gerar corpo falso.
    private static readonly HashSet<byte> mortosPeloTsunami = new();
    private static readonly Dictionary<byte, float> mortesPendentes = new();
    private static readonly Dictionary<byte, float> alturaAoAtingir = new();
    private static readonly Dictionary<byte, bool> movimentoOriginal = new();
    private static readonly Dictionary<byte, float> proximoSnapJogador = new();
    private static bool reuniaoAguardandoFim;
    private static bool reuniaoFoiAberta;
    private static readonly Dictionary<byte, float> protegidos = new();
    private static readonly Dictionary<byte, int> dutosProtegidos = new();

    private const string NomeOnda =
        "<color=#0099d1><size=25><line-height=97%><cspace=0.16em><mark=#0099d1>W\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\nW\n</mark></cspace></line-height></size></color>";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            PlayerControl.LocalPlayer == null || ShipStatus.Instance == null ||
            MeetingHud.Instance != null)
            return;

        if (ativo)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("O tsunami já está ativo.");
            return;
        }

        try
        {
            LimparLocal();
            protegidos.Clear();
            dutosProtegidos.Clear();
            fimProtecao = 0f;
            posicao = ObterInicio();
            indicador = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, posicao, Quaternion.identity);
            indicador.PlayerId = PlayerIdIndicador;
            indicador.isNew = false;
            indicador.notRealPlayer = true;
            indicador.NetTransform.SnapTo(posicao);

            // Criação de rede centralizada no helper portado do EHR.
            CustomNetObject.EnviarSpawn(indicador);

            if (PlayerControl.AllPlayerControls.Contains(indicador))
                PlayerControl.AllPlayerControls.Remove(indicador);

            // Espera o spawn chegar aos clientes antes de associar o PlayerId falso.
            // A aparência será publicada numa etapa posterior, após outra pequena pausa.
            etapaInicializacaoIndicador = 1;
            proximaEtapaInicializacaoIndicador = Time.realtimeSinceStartup + 0.1f;
            AplicarAvisoNomes();

            ativo = true;
            proximaAtualizacaoAvisoRede = 0f;
            proximaSabotagem = Time.realtimeSinceStartup + AtrasoInicialReator;
            reparoPendente = false;
            flashReatorCianoAtivo = false;
            proximaAtualizacao = Time.realtimeSinceStartup;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Tsunami iniciado. Indicador visual separado dos jogadores reais.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError("Falha ao iniciar tsunami: " + ex);
            Limpar();
        }
    }

    private static void PrepararIndicadorVisual()
    {
        PlayerControl host = PlayerControl.LocalPlayer;
        if (host == null || host.Data == null || indicador == null ||
            AmongUsClient.Instance == null) return;

        indicador.CachedPlayerData = host.Data;

        try
        {
            // O corpo físico do prefab é ocultado de propósito: o tsunami é
            // desenhado pelo texto do nome, como nos CustomNetObject do EHR.
            var body = indicador.cosmetics?.currentBodySprite?.BodySprite;
            if (body != null)
            {
                PlayerMaterial.SetColors(0, body);
                body.color = Color.clear;
                body.enabled = true;
            }

            var textoCor = indicador.cosmetics?.colorBlindText;
            if (textoCor != null)
                textoCor.color = Color.clear;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: preparação do renderer falhou: " + ex.Message);
        }

        var outfit = host.Data.Outfits[PlayerOutfitType.Default];
        string nomeOriginal = outfit.PlayerName;
        int corOriginal = outfit.ColorId;
        string chapeuOriginal = outfit.HatId;
        string roupaOriginal = outfit.SkinId;
        string mascoteOriginal = outfit.PetId;
        string visorOriginal = outfit.VisorId;
        string nomeSprite = "<size=14><br></size>" + NomeOnda;

        // IMPORTANTE: os três subpacotes precisam estar no MESMO GameData,
        // exatamente como EHR CustomNetObject.RpcChangeSprite. Se a restauração
        // chegar antes do Shapeshift, o indicador copia as roupas do host e os
        // clientes vanilla continuam vendo Fortegreen/???.
        MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
        try
        {
            writer.StartMessage(5);
            writer.Write(AmongUsClient.Instance.GameId);

            // 1) Publica temporariamente o nome que contém o sprite.
            outfit.PlayerName = nomeSprite;
            outfit.ColorId = 0;
            outfit.HatId = "";
            outfit.SkinId = "";
            outfit.PetId = "";
            outfit.VisorId = "";

            writer.StartMessage(1);
            writer.WritePacked(host.Data.NetId);
            host.Data.Serialize(writer, false);
            writer.EndMessage();

            // O Shapeshift local precisa acontecer ENQUANTO o outfit temporário
            // ainda está ativo; fazê-lo depois de restaurar copia as roupas.
            try
            {
                indicador.Shapeshift(host, false);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Tsunami: Shapeshift local falhou: " + ex.Message);
            }

            // 2) Shapeshift do objeto falso para os dados temporários do host.
            writer.StartMessage(2);
            writer.WritePacked(indicador.NetId);
            writer.Write((byte)RpcCalls.Shapeshift);
            writer.WriteNetObject(host);
            writer.Write(false);
            writer.EndMessage();

            // 3) Restaura o jogador real dentro do MESMO pacote, depois do RPC.
            outfit.PlayerName = nomeOriginal;
            outfit.ColorId = corOriginal;
            outfit.HatId = chapeuOriginal;
            outfit.SkinId = roupaOriginal;
            outfit.PetId = mascoteOriginal;
            outfit.VisorId = visorOriginal;

            writer.StartMessage(1);
            writer.WritePacked(host.Data.NetId);
            host.Data.Serialize(writer, false);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer = null;

            CustomNetObject.EnviarPosicao(indicador, posicao);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Tsunami: aparência enviada no pacote único do EHR; NetId={indicador.NetId}, " +
                $"PlayerId={indicador.PlayerId}, nomeSprite={nomeSprite.Length} caracteres.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Tsunami: falha ao publicar a aparência do indicador: " + ex);
        }
        finally
        {
            // Garante que o outfit do host nunca fique modificado, mesmo se
            // ocorrer uma exceção durante a montagem do pacote.
            outfit.PlayerName = nomeOriginal;
            outfit.ColorId = corOriginal;
            outfit.HatId = chapeuOriginal;
            outfit.SkinId = roupaOriginal;
            outfit.PetId = mascoteOriginal;
            outfit.VisorId = visorOriginal;
            writer?.Recycle();
        }
    }

    private static bool avisoLocalAtivo;
    private static float proximaAtualizacaoAvisoRede;

    public static void Atualizar()
    {
        if (AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        if (!YouTubeVoteBridgePlugin.EhHost())
        {
            AtualizarAvisoNomesCliente();
            return;
        }

        // A sequência é deliberadamente dividida em duas atualizações:
        // 1) associação temporária do objeto a cada cliente (EHR);
        // 2) aparência e posição inicial depois que a associação foi enviada.
        if (indicador != null && etapaInicializacaoIndicador == 1 &&
            Time.realtimeSinceStartup >= proximaEtapaInicializacaoIndicador)
        {
            try
            {
                int mensagens = CustomNetObject.AssociarClientes(indicador, PlayerIdIndicador);
                etapaInicializacaoIndicador = 2;
                proximaEtapaInicializacaoIndicador = Time.realtimeSinceStartup + 0.15f;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    $"Tsunami: associação EHR enviada para {mensagens} clientes vanilla; preparando aparência.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha na associação EHR do indicador: " + ex);
                etapaInicializacaoIndicador = 0;
            }
        }

        if (indicador != null && etapaInicializacaoIndicador == 2 &&
            Time.realtimeSinceStartup >= proximaEtapaInicializacaoIndicador)
        {
            PrepararIndicadorVisual();
            etapaInicializacaoIndicador = 3;
        }

        if (reuniaoAguardandoFim)
        {
            if (MeetingHud.Instance != null)
                reuniaoFoiAberta = true;
            else if (reuniaoFoiAberta)
            {
                atingidos.Clear();
                mortosPeloTsunami.Clear();
                reuniaoAguardandoFim = false;
                reuniaoFoiAberta = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami: lista de atingidos limpa após o encerramento da reunião.");
            }
        }

        // Os jogadores atingidos continuam sendo carregados mesmo que a onda
        // já tenha saído da tela. A morte/transformação só ocorre após o atraso.
        AtualizarJogadoresAtingidos(Time.realtimeSinceStartup);

        if (!ativo)
        {
            if (avisoLocalAtivo)
            {
                avisoLocalAtivo = false;
                RestaurarAvisoNomes();
            }
            return;
        }

        avisoLocalAtivo = true;

        if (MeetingHud.Instance != null)
        {
            Limpar();
            return;
        }

        float agora = Time.realtimeSinceStartup;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == indicador || jogador.Data == null ||
                jogador.Data.Disconnected)
                continue;
            if (!nomesOriginais.ContainsKey(jogador.PlayerId))
            {
                try
                {
                    nomesOriginais[jogador.PlayerId] =
                        jogador.cosmetics.nameText.text ?? jogador.Data.PlayerName ?? "";
                }
                catch { nomesOriginais[jogador.PlayerId] = jogador.Data.PlayerName ?? ""; }
            }
            AtualizarNomeLocal(jogador, agora);
        }
        AtualizarAvisoNomesVanilla(agora);
        AtualizarSabotagemReator(agora);
        if (agora < proximaAtualizacao) return;
        proximaAtualizacao = agora + Intervalo;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == indicador || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;

            if (protegidos.TryGetValue(jogador.PlayerId, out float protecaoAte))
            {
                if (agora < protecaoAte) continue;
                protegidos.Remove(jogador.PlayerId);
                try
                {
                    if (jogador.inVent && jogador.MyPhysics != null &&
                        dutosProtegidos.TryGetValue(jogador.PlayerId, out int idDuto))
                        jogador.MyPhysics.RpcExitVent(idDuto);
                    dutosProtegidos.Remove(jogador.PlayerId);
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Tsunami: falha ao retirar jogador do duto: " + ex.Message);
                }
            }

            if (jogador.inVent)
            {
                if (!protegidos.ContainsKey(jogador.PlayerId))
                {
                    protegidos[jogador.PlayerId] = agora + ProtecaoDuto;
                    try
                    {
                        foreach (Vent duto in ShipStatus.Instance.AllVents)
                            if (duto != null && Vector2.Distance(jogador.GetTruePosition(), (Vector2)duto.transform.position) < AlcanceIdentificarDutoTsunami)
                            { dutosProtegidos[jogador.PlayerId] = duto.Id; break; }
                    }
                    catch { }
                }
                continue;
            }

            if (atingidos.Contains(jogador.PlayerId)) continue;
            Vector2 p = jogador.GetTruePosition();

            // A entrada no duto é antecipada sem antecipar o dano:
            // jogadores à frente da onda ganham uma janela extra para se esconder,
            // mas só são atingidos quando a onda chega ao alcance normal.
            float distanciaX = p.x - posicao.x;
            const float AlcanceAntecipadoDuto = 1.35f;
            if (distanciaX < -Alcance || distanciaX > AlcanceAntecipadoDuto)
                continue;

            bool entrouNoDuto = false;
            try
            {
                foreach (Vent duto in ShipStatus.Instance.AllVents)
                {
                    if (duto == null || Vector2.Distance(p, (Vector2)duto.transform.position) > AlcanceDutoTsunami) continue;
                    if (jogador.MyPhysics == null) break;
                    jogador.MyPhysics.RpcEnterVent(duto.Id);
                    protegidos[jogador.PlayerId] = agora + ProtecaoDuto;
                    dutosProtegidos[jogador.PlayerId] = duto.Id;
                    entrouNoDuto = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Tsunami: falha ao entrar no duto: " + ex.Message);
            }
            if (entrouNoDuto) continue;

            if (Mathf.Abs(distanciaX) > Alcance)
                continue;

            try
            {
                // A onda prende o jogador na altura atual e o carrega no eixo X.
                // A troca para Ghost só acontece depois do atraso configurado.
                byte id = jogador.PlayerId;
                atingidos.Add(id);
                mortesPendentes[id] = agora + AtrasoMorteTsunami;
                alturaAoAtingir[id] = p.y;
                movimentoOriginal[id] = jogador.moveable;
                jogador.moveable = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami atingiu " + jogador.Data.PlayerName +
                    " (PlayerId=" + id + "); movimento bloqueado, Y preservado e SnapTo acompanhando a onda por " +
                    AtrasoMorteTsunami + "s antes da troca para Ghost.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao iniciar o transporte/morte atrasada: " + ex);
            }
        }

        if (indicador == null)
        {
            Limpar();
            return;
        }

        posicao.x += Velocidade * Intervalo;
        if (posicao.x > LimiteDireito() + 1.5f)
        {
            // A onda visual terminou, mas os jogadores já atingidos ainda
            // precisam completar o atraso antes da troca para Ghost.
            Limpar(preservarMortesPendentes: true);
            return;
        }

        try
        {
            CustomNetObject.EnviarPosicao(indicador, posicao);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha ao mover indicador: " + ex.Message);
        }
    }

    private static void AtualizarJogadoresAtingidos(float agora)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || mortesPendentes.Count == 0)
            return;

        foreach (byte id in new List<byte>(mortesPendentes.Keys))
        {
            PlayerControl jogador = null;
            foreach (PlayerControl candidato in PlayerControl.AllPlayerControls)
            {
                if (candidato != null && candidato.PlayerId == id)
                {
                    jogador = candidato;
                    break;
                }
            }

            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
            {
                mortesPendentes.Remove(id);
                alturaAoAtingir.Remove(id);
                movimentoOriginal.Remove(id);
                proximoSnapJogador.Remove(id);
                continue;
            }

            if (MeetingHud.Instance == null && ativo && indicador != null && jogador.MyPhysics != null)
            {
                try
                {
                    float y = alturaAoAtingir.TryGetValue(id, out float altura)
                        ? altura : jogador.GetTruePosition().y;
                    Vector2 destino = new Vector2(posicao.x, y);

                    // Reutiliza a mesma chamada de teleporte do evento de duto,
                    // que já foi validada neste projeto: RpcSnapTo publica o SnapTo
                    // pela API do jogo em vez de montar o RPC manualmente.
                    // Limita a frequência para não inundar a rede (12,5 atualizações/s).
                    if (!proximoSnapJogador.TryGetValue(id, out float proximo) || agora >= proximo)
                    {
                        jogador.NetTransform.RpcSnapTo(destino);
                        proximoSnapJogador[id] = agora + Intervalo;

                        YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                            "Tsunami: RpcSnapTo de PlayerId=" + id +
                            " para X=" + destino.x.ToString("0.00") +
                            ", Y=" + destino.y.ToString("0.00") + ".");
                    }
                }
                catch (Exception ex)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Tsunami: falha ao carregar PlayerId=" + id + ": " + ex.Message);
                }
            }

            if (agora < mortesPendentes[id] && MeetingHud.Instance == null)
                continue;

            try
            {
                bool eraImpostor = jogador.Data.Role != null && jogador.Data.Role.IsImpostor;
                jogador.RpcSetRole(
                    eraImpostor ? RoleTypes.ImpostorGhost : RoleTypes.CrewmateGhost,
                    true);
                mortosPeloTsunami.Add(id);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami: atraso concluído; PlayerId=" + id +
                    " trocado para " + (eraImpostor ? "ImpostorGhost" : "CrewmateGhost") +
                    ". Não é enviado MurderPlayer para evitar deixar corpo real.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao trocar PlayerId=" + id + " para Ghost: " + ex);
            }
            finally
            {
                try
                {
                    if (movimentoOriginal.TryGetValue(id, out bool moveable))
                        jogador.moveable = moveable;
                }
                catch { }
                mortesPendentes.Remove(id);
                alturaAoAtingir.Remove(id);
                movimentoOriginal.Remove(id);
                proximoSnapJogador.Remove(id);
            }
        }
    }

    private static void AtualizarAvisoNomesCliente()
    {
        bool indicadorEncontrado = false;
        try
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador != null && jogador.PlayerId == PlayerIdIndicador)
                {
                    indicadorEncontrado = true;
                    break;
                }
            }
        }
        catch { }

        if (indicadorEncontrado)
        {
            avisoLocalAtivo = true;
            float agora = Time.realtimeSinceStartup;
            PlayerControl local = PlayerControl.LocalPlayer;
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador != local ||
                    jogador.PlayerId == PlayerIdIndicador || jogador.Data == null ||
                    jogador.Data.Disconnected)
                    continue;

                if (!nomesOriginais.ContainsKey(jogador.PlayerId))
                {
                    try
                    {
                        nomesOriginais[jogador.PlayerId] =
                            jogador.cosmetics.nameText.text ?? jogador.Data.PlayerName ?? "";
                    }
                    catch { nomesOriginais[jogador.PlayerId] = jogador.Data.PlayerName ?? ""; }
                }
                AtualizarNomeLocal(jogador, agora);
            }
        }
        else if (avisoLocalAtivo)
        {
            avisoLocalAtivo = false;
            RestaurarAvisoNomes();
        }
    }

    private static void AplicarAvisoNomes()
    {
        avisoLocalAtivo = true;
        nomesOriginais.Clear();
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null || local.Data == null || local.Data.Disconnected)
            return;
        try
        {
            PlayerControl jogador = local;
            string nomeOriginal = jogador.cosmetics.nameText.text ?? jogador.Data.PlayerName ?? "";
            nomesOriginais[jogador.PlayerId] = nomeOriginal;
            AtualizarNomeLocal(jogador, Time.realtimeSinceStartup);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: não foi possível aplicar o aviso local: " + ex.Message);
        }
    }

    private static void AtualizarNomeLocal(PlayerControl jogador, float agora)
    {
        if ((!ativo && !avisoLocalAtivo) || jogador == null ||
            jogador.cosmetics == null || jogador.cosmetics.nameText == null)
            return;

        if (!nomesOriginais.TryGetValue(jogador.PlayerId, out string nomeOriginal))
            nomeOriginal = jogador.Data?.PlayerName ?? "";

        string cor = ((int)(agora * 2f) % 2 == 0) ? "red" : "yellow";
        jogador.cosmetics.nameText.text =
            nomeOriginal + "\n<color=" + cor + ">Um tsunami está vindo\nVá para o duto mais próximo</color>";
    }

    private static void AtualizarAvisoNomesVanilla(float agora)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            (!ativo && !avisoLocalAtivo) || agora < proximaAtualizacaoAvisoRede)
            return;

        proximaAtualizacaoAvisoRede = agora + 0.5f;
        string cor = ((int)(agora * 2f) % 2 == 0) ? "red" : "yellow";
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected ||
                jogador.OwnerId < 0 || jogador.OwnerId == AmongUsClient.Instance.ClientId ||
                YouTubeVoteBridgePlugin.ClienteTemMod(jogador.OwnerId))
                continue;

            if (!nomesOriginais.TryGetValue(jogador.PlayerId, out string nomeOriginal))
            {
                nomeOriginal = jogador.Data.PlayerName ?? "";
                nomesOriginais[jogador.PlayerId] = nomeOriginal;
            }

            YouTubeVoteBridgePlugin.DefinirNomeLocal(
                jogador, nomeOriginal + "\n<color=" + cor + ">Um tsunami está vindo\nVá para o duto mais próximo</color>");
            nomesAlteradosRede.Add(jogador.PlayerId);
        }
    }

    private static void RestaurarAvisoNomes()
    {
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null)
                continue;

            if (nomesOriginais.TryGetValue(jogador.PlayerId, out string nomeOriginal))
            {
                try
                {
                    if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                        jogador.cosmetics.nameText.text = nomeOriginal;
                }
                catch { }

                if (YouTubeVoteBridgePlugin.EhHost() && AmongUsClient.Instance != null &&
                    jogador.OwnerId >= 0 && jogador.OwnerId != AmongUsClient.Instance.ClientId &&
                    nomesAlteradosRede.Contains(jogador.PlayerId))
                    YouTubeVoteBridgePlugin.DefinirNomeLocal(jogador, nomeOriginal);
            }
        }
        nomesOriginais.Clear();
        nomesAlteradosRede.Clear();
    }

    private static void AtualizarSabotagemReator(float agora)
    {
        if (!ativo || !YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null || AmongUsClient.Instance == null)
            return;

        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (!navio.Systems.ContainsKey(SystemTypes.Sabotage) ||
                !navio.Systems.ContainsKey(SystemTypes.Reactor))
                return;

            SabotageSystemType sistema = navio.Systems[SystemTypes.Sabotage]
                .Cast<SabotageSystemType>();
            if (sistema == null) return;

            if (reparoPendente)
            {
                if (agora < repararReatorAte) return;

                navio.RpcUpdateSystem(SystemTypes.Reactor, 16);
                navio.RpcUpdateSystem(SystemTypes.Reactor, 17);
                flashReatorCianoAtivo = false;
                FlashOverlay.DefinirFlashTsunami(false);
                reparoPendente = false;
                proximaSabotagem = agora + IntervaloCicloReator;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami: reparo enviado; aguardando antes da próxima sabotagem.");
                return;
            }

            if (agora < proximaSabotagem) return;
            if (sistema.AnyActive)
            {
                proximaSabotagem = agora + 0.5f;
                return;
            }

            sistema.Timer = 0f;
            sistema.IsDirty = true;
            navio.RpcUpdateSystem(SystemTypes.Sabotage, (byte)SystemTypes.Reactor);
            flashReatorCianoAtivo = true;
            reparoPendente = true;
            repararReatorAte = agora + 0.5f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Tsunami: sabotagem do reator iniciada; reparo programado para 0,5 segundo.");
        }
        catch (Exception ex)
        {
            proximaSabotagem = agora + 0.5f;
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha no ciclo de sabotagem/reparo do reator: " + ex.Message);
        }
    }

    private static Vector2 ObterInicio()
    {
        // Começa um pouco atrás do jogador vivo mais à esquerda,
        // usando a posição atual da partida em vez de uma coordenada fixa.
        PlayerControl maisAtras = null;
        float menorX = float.MaxValue;

        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == indicador || jogador.Data == null ||
                jogador.Data.Disconnected || jogador.Data.IsDead)
                continue;

            float x = jogador.GetTruePosition().x;
            if (x < menorX)
            {
                menorX = x;
                maisAtras = jogador;
            }
        }

        if (maisAtras == null)
            return new Vector2(-10f, 0f);

        Vector2 posicaoJogador = maisAtras.GetTruePosition();
        const float DistanciaAtras = 2.4f;
        return new Vector2(posicaoJogador.x - DistanciaAtras, posicaoJogador.y);
    }

    private static float LimiteEsquerdo()
    {
        float x = 0f;
        bool encontrado = false;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected) continue;
            float px = jogador.transform.position.x;
            if (!encontrado || px < x) { x = px; encontrado = true; }
        }
        return encontrado ? x - 5f : -10f;
    }

    private static float LimiteDireito()
    {
        float x = 0f;
        bool encontrado = false;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected) continue;
            float px = jogador.transform.position.x;
            if (!encontrado || px > x) { x = px; encontrado = true; }
        }
        return encontrado ? x + 5f : 10f;
    }

    public static void PrepararAntesDaReuniao()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null)
            return;

        if (atingidos.Count == 0)
        {
            // Só encerra o evento se ele ainda estiver ativo; não envia RPCs
            // de reparo do reator em reuniões sem tsunami.
            if (ativo || avisoLocalAtivo || indicador != null)
                Limpar();
            else
                RestaurarAvisoNomes();
            return;
        }

        // Não force a morte de quem ainda está dentro dos 2 segundos.
        // Processa apenas os prazos que já venceram no relógio real.
        AtualizarJogadoresAtingidos(Time.realtimeSinceStartup);

        // Corpos falsos só para jogadores cuja troca para Ghost já foi solicitada
        // após o atraso normal. Os demais não devem virar corpos só por abrir reunião.
        List<byte> ids = new(mortosPeloTsunami);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Tsunami: criando " + ids.Count +
            " corpo(s) falso(s) de jogadores convertidos para Ghost. Atingidos=" +
            atingidos.Count + ", pendentes=" + mortesPendentes.Count + ".");

        // Para a onda e restaura os nomes antes de publicar os corpos.
        if (ativo || avisoLocalAtivo || indicador != null)
            Limpar();
        else
            RestaurarAvisoNomes();

        foreach (byte playerId in ids)
        {
            try
            {
                EventoCriarCorpoFalso.ExecutarParaJogador(playerId);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao criar corpo falso para PlayerId=" +
                    playerId + ": " + ex);
            }
        }

        reuniaoAguardandoFim = true;
        reuniaoFoiAberta = false;
    }

    public static void Limpar(bool preservarMortesPendentes = false)
    {
        if (YouTubeVoteBridgePlugin.EhHost())
            ForcarReparoReator();

        ativo = false;
        avisoLocalAtivo = false;
        reparoPendente = false;
        flashReatorCianoAtivo = false;
        FlashOverlay.DefinirFlashTsunami(false);
        RestaurarAvisoNomes();
        protegidos.Clear();
        dutosProtegidos.Clear();
        // Ao terminar normalmente, preserve os atrasos já iniciados.
        // Em interrupções/reuniões, restaure o movimento e cancele pendências.
        if (!preservarMortesPendentes)
        {
            foreach (byte id in new List<byte>(mortesPendentes.Keys))
            {
                foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
                {
                    if (jogador == null || jogador.PlayerId != id) continue;
                    try
                    {
                        if (movimentoOriginal.TryGetValue(id, out bool moveable))
                            jogador.moveable = moveable;
                    }
                    catch { }
                    break;
                }
            }
            mortesPendentes.Clear();
            alturaAoAtingir.Clear();
            movimentoOriginal.Clear();
            proximoSnapJogador.Clear();
        }
        if (indicador != null && AmongUsClient.Instance != null &&
            YouTubeVoteBridgePlugin.EhHost())
        {
            try
            {
                MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(5);
                writer.WritePacked(indicador.NetId);
                writer.EndMessage();
                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer.Recycle();
                AmongUsClient.Instance.RemoveNetObject(indicador);
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Tsunami: falha ao remover indicador: " + ex.Message);
            }
        }
        LimparLocal();
    }

    private static void ForcarReparoReator()
    {
        try
        {
            ShipStatus navio = ShipStatus.Instance;
            if (navio == null || !navio.Systems.ContainsKey(SystemTypes.Reactor))
                return;

            navio.RpcUpdateSystem(SystemTypes.Reactor, 16);
            navio.RpcUpdateSystem(SystemTypes.Reactor, 17);
            reparoPendente = false;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "Tsunami: sinais finais de reparo do reator enviados.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha ao forçar o reparo final do reator: " + ex.Message);
        }
    }

    private static void LimparLocal()
    {
        if (indicador != null)
        {
            try
            {
                if (PlayerControl.AllPlayerControls.Contains(indicador))
                    PlayerControl.AllPlayerControls.Remove(indicador);
            }
            catch { }
            try { UnityEngine.Object.Destroy(indicador.gameObject); }
            catch { }
        }
        indicador = null;
        etapaInicializacaoIndicador = 0;
        proximaEtapaInicializacaoIndicador = 0f;
    }
}

// O flash ciano substitui o vermelho durante o tsunami, mas só fica ativo
// enquanto a sabotagem do reator está realmente em andamento.
[HarmonyPatch(typeof(HudManager), nameof(HudManager.StartReactorFlash))]
internal static class EventoTsunamiFlashReatorPatch
{
    public static bool Prefix(HudManager __instance)
    {
        if (!EventoTsunami.AvisoAtivo)
            return true;

        if (__instance.ReactorFlash == null)
            __instance.ReactorFlash = __instance.StartCoroutine(FlashCiano().WrapToIl2Cpp());
        return false;
    }

    private static IEnumerator FlashCiano()
    {
        if (!HudManager.InstanceExists)
            yield break;

        HudManager hud = HudManager.Instance;
        const float DuracaoFlashReator = 0.5f;
        float terminarEm = Time.realtimeSinceStartup + DuracaoFlashReator;
        WaitForSeconds espera = new WaitForSeconds(0.25f);
        bool mostrar = true;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("Tsunami: flash ciano limitado à duração do pulso do reator.");

        while (Time.realtimeSinceStartup < terminarEm)
        {
            if (hud == null) break;
            FlashOverlay.DefinirFlashTsunami(mostrar);
            try
            {
                if (mostrar && ShipStatus.Instance != null && SoundManager.Instance != null)
                    SoundManager.Instance.PlaySound(ShipStatus.Instance.SabotageSound, false, 1f);
            }
            catch { }
            mostrar = !mostrar;
            yield return espera;
        }

        FlashOverlay.DefinirFlashTsunami(false);
        if (hud != null)
            hud.ReactorFlash = null;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class EventoTsunamiFlashReatorCorPatch
{
    public static void Postfix(HudManager __instance)
    {
        if (!EventoTsunami.AvisoAtivo || __instance == null)
        {
            FlashOverlay.DefinirFlashTsunami(false);
            return;
        }

        // Não reinicia o flash a cada frame: o jogo inicia um pulso por sabotagem.
        // Reiniciar aqui mantinha o overlay ciano ativo por toda a duração do tsunami.
    }
}

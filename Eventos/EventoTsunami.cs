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
    private const byte PlayerIdIndicador = 253;
    private const float Velocidade = 3.0f;
    private const float Intervalo = 0.08f;
    private const float ProtecaoDuto = 1.0f;
    private const float AlcanceDutoTsunami = 0.75f;
    private const float AlcanceIdentificarDutoTsunami = 0.9f;
    private const float Alcance = 0.85f;
    private const float IntervaloCicloReator = 1.5f;
    private const float AtrasoInicialReator = 1.0f;

    private static PlayerControl indicador;
    private static Vector2 posicao;
    private static float proximaAtualizacao;
    private static float fimProtecao;
    private static bool ativo;
    private static float proximaSabotagem;
    private static float repararReatorAte;
    private static bool reparoPendente;
    private static readonly Dictionary<byte, string> nomesOriginais = new();
    private static readonly HashSet<byte> nomesAlteradosRede = new();
    private static readonly HashSet<byte> atingidos = new();
    private static bool reuniaoAguardandoFim;
    private static bool reuniaoFoiAberta;
    private static readonly Dictionary<byte, float> protegidos = new();
    private static readonly Dictionary<byte, int> dutosProtegidos = new();

    private const string NomeOnda =
        "<color=#0099d1><size=4><line-height=97%><cspace=0.16em><mark=#000000>WWWWWWWWWWWWWWWWWWWWWWWW</mark></cspace></line-height></size></color>";

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

            AmongUsClient.Instance.NetIdCnt += 1U;
            MessageWriter writer = MessageWriter.Get(SendOption.Reliable);
            try
            {
                writer.StartMessage(5);
                writer.Write(AmongUsClient.Instance.GameId);
                writer.StartMessage(4);
                var spawn = AmongUsClient.Instance.CreateSpawnMessage(indicador, -2, SpawnFlags.None);
                spawn.SerializeValues(writer);
                writer.EndMessage();

                for (uint i = 1; i <= 3; i++)
                {
                    writer.StartMessage(4);
                    writer.WritePacked(2U);
                    writer.WritePacked(-2);
                    writer.Write((byte)SpawnFlags.None);
                    writer.WritePacked(1);
                    writer.WritePacked(AmongUsClient.Instance.NetIdCnt - i);
                    writer.StartMessage(1);
                    writer.EndMessage();
                    writer.EndMessage();
                }

                writer.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(writer);
                writer = null;
            }
            finally { writer?.Recycle(); }

            if (PlayerControl.AllPlayerControls.Contains(indicador))
                PlayerControl.AllPlayerControls.Remove(indicador);

            PrepararIndicadorVisual();
            AplicarAvisoNomes();

            ativo = true;
            proximaAtualizacaoAvisoRede = 0f;
            proximaSabotagem = Time.realtimeSinceStartup + AtrasoInicialReator;
            reparoPendente = false;
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
        if (host == null || host.Data == null || indicador == null) return;

        try
        {
            MessageWriter association = MessageWriter.Get(SendOption.Reliable);
            association.StartMessage(26);
            association.WritePacked(AmongUsClient.Instance.GameId);

            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador == host || jogador.Data == null || jogador.Data.Disconnected)
                    continue;

                association.StartMessage(6);
                association.Write(AmongUsClient.Instance.GameId);
                association.WritePacked(jogador.OwnerId);
                association.StartMessage(1);
                association.WritePacked(indicador.NetId);
                association.Write(jogador.PlayerId);
                association.EndMessage();
                association.StartMessage(2);
                association.WritePacked(indicador.NetId);
                association.Write((byte)RpcCalls.MurderPlayer);
                association.WriteNetObject(indicador);
                association.Write((int)MurderResultFlags.FailedError);
                association.EndMessage();
                association.StartMessage(1);
                association.WritePacked(indicador.NetId);
                association.Write(PlayerIdIndicador);
                association.EndMessage();
                association.EndMessage();
            }

            association.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(association);
            association.Recycle();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Tsunami: associação visual incompleta: " + ex.Message);
        }

        try
        {
            if (indicador.Data == null)
                throw new InvalidOperationException("Os dados próprios do NPC não foram inicializados.");
            var outfit = indicador.Data.Outfits[PlayerOutfitType.Default];
            outfit.PlayerName = NomeOnda;
            outfit.ColorId = 0;
            outfit.HatId = "";
            outfit.SkinId = "";
            outfit.PetId = "";
            outfit.VisorId = "";
            if (indicador.cosmetics != null && indicador.cosmetics.nameText != null)
                indicador.cosmetics.nameText.text = NomeOnda;

            MessageWriter visual = MessageWriter.Get(SendOption.Reliable);
            try
            {
                visual.StartMessage(5);
                visual.Write(AmongUsClient.Instance.GameId);
                visual.StartMessage(1);
                visual.WritePacked(indicador.Data.NetId);
                indicador.Data.Serialize(visual, false);
                visual.EndMessage();
                visual.EndMessage();
                AmongUsClient.Instance.SendOrDisconnect(visual);
                visual = null;
            }
            finally { visual?.Recycle(); }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: não foi possível publicar o nome personalizado do NPC: " + ex.Message);
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

        if (reuniaoAguardandoFim)
        {
            if (MeetingHud.Instance != null)
                reuniaoFoiAberta = true;
            else if (reuniaoFoiAberta)
            {
                atingidos.Clear();
                reuniaoAguardandoFim = false;
                reuniaoFoiAberta = false;
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami: lista de atingidos limpa após o encerramento da reunião.");
            }
        }

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
                            if (duto != null && Vector2.Distance(jogador.transform.position, duto.transform.position) < AlcanceIdentificarDutoTsunami)
                            { dutosProtegidos[jogador.PlayerId] = duto.Id; break; }
                    }
                    catch { }
                }
                continue;
            }

            if (atingidos.Contains(jogador.PlayerId)) continue;
            Vector2 p = jogador.transform.position;
            if (Mathf.Abs(p.x - posicao.x) > Alcance)
                continue;

            bool entrouNoDuto = false;
            try
            {
                foreach (Vent duto in ShipStatus.Instance.AllVents)
                {
                    if (duto == null || Vector2.Distance(p, duto.transform.position) > AlcanceDutoTsunami) continue;
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

            try
            {
                // Etapa atual: somente troca o papel. Não mata nem chama animação de kill.
                bool eraImpostor = jogador.Data.Role != null && jogador.Data.Role.IsImpostor;
                jogador.RpcSetRole(
                    eraImpostor ? RoleTypes.ImpostorGhost : RoleTypes.CrewmateGhost,
                    true);
                atingidos.Add(jogador.PlayerId);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami atingiu " + jogador.Data.PlayerName +
                    "; papel alterado para " +
                    (eraImpostor ? "ImpostorGhost" : "CrewmateGhost") +
                    " sem executar morte.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao matar ou aplicar papel de fantasma: " + ex);
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
            Limpar();
            return;
        }

        try
        {
            indicador.NetTransform.SnapTo(posicao,
                (ushort)(indicador.NetTransform.lastSequenceId + 1U));
            MessageWriter move = AmongUsClient.Instance.StartRpcImmediately(
                indicador.NetTransform.NetId, (byte)RpcCalls.SnapTo, SendOption.None);
            move.Write(posicao.x);
            move.Write(posicao.y);
            move.Write((ushort)(indicador.NetTransform.lastSequenceId + 2U));
            AmongUsClient.Instance.FinishRpcImmediately(move);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha ao mover indicador: " + ex.Message);
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
                jogador, nomeOriginal + "\n<color=" + cor + ">Um tsunami está vindo</color>");
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
        float y = 0f;
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador != null && jogador.Data != null && !jogador.Data.Disconnected)
            {
                y = jogador.transform.position.y;
                break;
            }
        }
        return new Vector2(LimiteEsquerdo() - 1.5f, y);
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

        List<byte> ids = new(atingidos);
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Tsunami: criando " + ids.Count +
            " corpo(s) falso(s) antes da reunião. MeetingHud ativo=" + (MeetingHud.Instance != null) + ".");

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

    public static void Limpar()
    {
        if (YouTubeVoteBridgePlugin.EhHost())
            ForcarReparoReator();

        ativo = false;
        avisoLocalAtivo = false;
        reparoPendente = false;
        RestaurarAvisoNomes();
        protegidos.Clear();
        dutosProtegidos.Clear();
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
    }
}

// O flash padrão do jogo usa vermelho fixo. Durante o tsunami, substituímos
// apenas o flash do reator por ciano nos clientes que têm este mod.
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
        WaitForSeconds espera = new WaitForSeconds(1f);
        hud.FullScreen.color = new Color(0f, 1f, 1f, 0.37254903f);
        while (EventoTsunami.AvisoAtivo)
        {
            hud.FullScreen.enabled = !hud.FullScreen.enabled;
            try
            {
                if (ShipStatus.Instance != null && SoundManager.Instance != null)
                    SoundManager.Instance.PlaySound(ShipStatus.Instance.SabotageSound, false, 1f);
            }
            catch { }
            yield return espera;
        }

        hud.ReactorFlash = null;
    }
}


[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class EventoTsunamiFlashReatorCorPatch
{
    public static void Postfix(HudManager __instance)
    {
        if (!EventoTsunami.AvisoAtivo || __instance == null)
            return;

        try
        {
            if (__instance.ReactorFlash == null)
                __instance.StartReactorFlash();
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
                "Tsunami: não foi possível iniciar o flash ciano: " + ex.Message);
        }

        if (__instance.ReactorFlash != null && __instance.FullScreen != null)
            __instance.FullScreen.color = new Color(0f, 1f, 1f, 0.37254903f);
    }
}

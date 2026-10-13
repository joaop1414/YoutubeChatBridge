using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Unity.IL2CPP.Utils.Collections;
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
            atingidos.Clear();
            protegidos.Clear();
            dutosProtegidos.Clear();
            fimProtecao = 0f;
            posicao = ObterInicio();
            indicador = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, posicao, Quaternion.identity);
            indicador.PlayerId = PlayerIdIndicador;
            indicador.isNew = false;
            indicador.notRealPlayer = true;
            indicador.CachedPlayerData = PlayerControl.LocalPlayer.Data;
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

            // Inicializa o objeto falso nos clientes e aplica o nome visual da onda.
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

        var outfit = host.Data.Outfits[PlayerOutfitType.Default];
        string nome = outfit.PlayerName;
        int cor = outfit.ColorId;
        string chapeu = outfit.HatId;
        string roupa = outfit.SkinId;
        string mascote = outfit.PetId;
        string visor = outfit.VisorId;
        MessageWriter visual = MessageWriter.Get(SendOption.Reliable);
        try
        {
            visual.StartMessage(5);
            visual.Write(AmongUsClient.Instance.GameId);
            outfit.PlayerName = NomeOnda;
            outfit.ColorId = 0;
            outfit.HatId = "";
            outfit.SkinId = "";
            outfit.PetId = "";
            outfit.VisorId = "";

            visual.StartMessage(1);
            visual.WritePacked(host.Data.NetId);
            host.Data.Serialize(visual, false);
            visual.EndMessage();

            try { indicador.Shapeshift(host, false); } catch { }
            visual.StartMessage(2);
            visual.WritePacked(indicador.NetId);
            visual.Write((byte)RpcCalls.Shapeshift);
            visual.WriteNetObject(host);
            visual.Write(false);
            visual.EndMessage();

            outfit.PlayerName = nome;
            outfit.ColorId = cor;
            outfit.HatId = chapeu;
            outfit.SkinId = roupa;
            outfit.PetId = mascote;
            outfit.VisorId = visor;
            visual.StartMessage(1);
            visual.WritePacked(host.Data.NetId);
            host.Data.Serialize(visual, false);
            visual.EndMessage();
            visual.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(visual);
            visual = null;
        }
        finally
        {
            visual?.Recycle();
            outfit.PlayerName = nome;
            outfit.ColorId = cor;
            outfit.HatId = chapeu;
            outfit.SkinId = roupa;
            outfit.PetId = mascote;
            outfit.VisorId = visor;
        }
    }

    private static bool avisoLocalAtivo;
    private static float proximaAtualizacaoAvisoRede;

    public static void Atualizar()
    {
        if (AmongUsClient.Instance == null || ShipStatus.Instance == null)
            return;

        // Cada cliente com o mod detecta localmente o indicador de rede do tsunami.
        // A simulação/mortes/sabotagens continuam exclusivamente no host.
        if (!YouTubeVoteBridgePlugin.EhHost())
        {
            AtualizarAvisoNomesCliente();
            return;
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
                    // Saída de emergência: só tenta sair se o jogo ainda o considera dentro de um duto.
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
                // Entrar manualmente num duto depende da API e do estado de cada mapa.
                if (!protegidos.ContainsKey(jogador.PlayerId))
                {
                    protegidos[jogador.PlayerId] = agora + ProtecaoDuto;
                    try
                    {
                        foreach (Vent duto in ShipStatus.Instance.AllVents)
                            if (duto != null && Vector2.Distance(jogador.transform.position, duto.transform.position) < 1.8f)
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
                    if (duto == null || Vector2.Distance(p, duto.transform.position) > 1.5f) continue;
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
                // Morte real registrada antes da reunião. O RPC padrão pode tocar animação.
                jogador.RpcMurderPlayer(jogador, true);
                atingidos.Add(jogador.PlayerId);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami atingiu " + jogador.Data.PlayerName + ".");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao matar jogador: " + ex);
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
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null || jogador.PlayerId == PlayerIdIndicador ||
                    jogador.Data == null || jogador.Data.Disconnected)
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
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador.Data == null || jogador.Data.Disconnected)
                continue;
            try
            {
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
    }

    private static void AtualizarNomeLocal(PlayerControl jogador, float agora)
    {
        if ((!ativo && !avisoLocalAtivo) || jogador == null ||
            jogador.cosmetics == null || jogador.cosmetics.nameText == null)
            return;

        if (!nomesOriginais.TryGetValue(jogador.PlayerId, out string nomeOriginal))
            nomeOriginal = jogador.Data?.PlayerName ?? "";

        string cor = ((int)(agora * 2f) % 2 == 0) ? "red" : "yellow";
        // A quebra de linha é explícita; <b> apenas aplica negrito.
        jogador.cosmetics.nameText.text =
            nomeOriginal + "\n<color=" + cor + ">Um tsunami está vindo</color>";
    }

    private static void AtualizarAvisoNomesVanilla(float agora)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            (!ativo && !avisoLocalAtivo) || agora < proximaAtualizacaoAvisoRede)
            return;

        // O mesmo SetName direcionado usado pelo placar: cada cliente vanilla recebe
        // o aviso somente no nick que ele próprio controla.
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

                // Restaura também no cliente vanilla, usando o RPC SetName direcionado.
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

                // Os dois sinais correspondem às duas mãos/terminais do reparo do reator clássico.
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
                // Não interfere numa sabotagem que já estava ativa antes do ciclo do tsunami.
                proximaSabotagem = agora + 0.5f;
                return;
            }

            // Ignora o cooldown global da sabotagem.
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

    public static void Limpar()
    {
        // O evento pode acabar antes do timer de reparo. Sempre envia os dois
        // sinais de conclusão do reator antes de desativar o estado do tsunami.
        if (YouTubeVoteBridgePlugin.EhHost())
            ForcarReparoReator();

        ativo = false;
        avisoLocalAtivo = false;
        reparoPendente = false;
        RestaurarAvisoNomes();
        atingidos.Clear();
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

            // Repetir o par de atualizações no encerramento evita deixar a
            // sabotagem ativa caso a onda termine durante o ciclo de 0,5 s.
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

        // Se o evento terminar sem o jogo ter encerrado o flash, libera o campo.
        hud.ReactorFlash = null;
    }
}

[HarmonyPatch(typeof(HudManager), nameof(HudManager.Update))]
internal static class EventoTsunamiFlashReatorCorPatch
{
    public static void Postfix(HudManager __instance)
    {
        if (EventoTsunami.AvisoAtivo && __instance.ReactorFlash != null &&
            __instance.FullScreen != null)
            __instance.FullScreen.color = new Color(0f, 1f, 1f, 0.37254903f);
    }
}

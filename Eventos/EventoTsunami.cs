using MoreGamemodes;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    private const byte PlayerIdIndicador = CompatibilidadeClientes.PlayerIdIndicadorTsunami;
    private const float Velocidade = 3.0f;
    // Atualizações mais frequentes reduzem os saltos visuais ao carregar jogadores.
    // 25 atualizações/s; Velocidade * Intervalo mantém a velocidade física da onda.
    private const float Intervalo = 0.04f;
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
    // Mantém o SetName temporário por um curto intervalo para dar tempo ao cliente vanilla de processá-lo.
    private const float DuracaoNomeTemporarioTsunami = 0.75f;
    private static float restaurarNomesTemporariosEm;
    private static readonly List<KeyValuePair<PlayerControl, string>> nomesTemporariosPendentes = new();
    private static float proximaSabotagem;
    private static float repararReatorAte;
    private static bool reparoPendente;
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
    // Adia o RPC de entrada para o SnapTo chegar primeiro aos clientes vanilla.
    private static readonly Dictionary<byte, float> entradasDutoPendentes = new();
    // X do jogador quando se abrigou; o prazo de saída só começa após a onda passar.
    private static readonly Dictionary<byte, float> posicaoXProtegida = new();
    // Evita que o jogador seja enviado novamente ao mesmo duto após a saída.
    private static readonly HashSet<byte> bloqueadosReentradaDuto = new();
    private static readonly Dictionary<byte, float> prazoSaidaDuto = new();

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
            // O indicador nasce fora da área visível. Não alteramos o nome nem
            // o outfit do host: isso pode disparar a serialização dos dados do
            // jogador real com o texto longo/formatado da onda.
            Vector2 posicaoOculta = posicao + new Vector2(-1000f, 0f);
            indicador = UnityEngine.Object.Instantiate(
                AmongUsClient.Instance.PlayerPrefab, posicaoOculta, Quaternion.identity);
            indicador.PlayerId = PlayerIdIndicador;
            indicador.isNew = false;
            indicador.notRealPlayer = true;
            indicador.NetTransform.SnapTo(posicaoOculta);

            // O nome serializado no spawn deve ser curto. O texto visual completo
            // será aplicado depois, por RPC SetName separado.
            var outfitIndicador = indicador?.Data?.Outfits[PlayerOutfitType.Default];
            if (outfitIndicador != null)
                outfitIndicador.PlayerName = "TSUNAMI";

            // Etapa -1: no próximo Update, enviaremos o spawn oculto e usaremos
            // SetName direcionado para cada cliente vanilla, sem alterar o host.
            etapaInicializacaoIndicador = -1;
            proximaEtapaInicializacaoIndicador = Time.realtimeSinceStartup + 0.05f;

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
        if (host == null || host.Data == null || indicador == null ||
            AmongUsClient.Instance == null) return;

        // Mantém os renderers físicos invisíveis: o indicador é desenhado
        // pelo texto do nome, não por um boneco colorido.
        try
        {
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

        try
        {
            // Usa a técnica de sprite do EHR: publica o outfit temporário do host,
            // aplica-o ao NPC por Shapeshift e restaura o outfit original do host.
            // O nome visual da onda é parte do sprite, não o nome de rede do NPC.
            CustomNetObject.EnviarAparenciaSprite(indicador, NomeOnda, out _);
            // Shapeshift pode sobrescrever o nome do NPC com o outfit do host;
            // reenvia SetName depois dele para que o nome da onda prevaleça.
            CustomNetObject.EnviarSetName(indicador, NomeOnda);
            CustomNetObject.EnviarPosicao(indicador, posicao);
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Tsunami: nome aplicado diretamente ao NPC; NetId={indicador.NetId}, PlayerId={indicador.PlayerId}.");

            // No host, não dependemos do nome propagado pelo Shapeshift:
            // desenhamos a onda diretamente no TextMeshPro do nome do indicador.
            try
            {
                if (indicador.cosmetics?.nameText != null)
                    indicador.cosmetics.nameText.text = NomeOnda;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                    "Tsunami: não foi possível definir o texto local da onda: " + ex.Message);
            }

            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                $"Tsunami: aparência compatível enviada pelo CustomNetObject; NetId={indicador.NetId}, " +
                $"PlayerId={indicador.PlayerId}, nomeSprite={NomeOnda.Length} caracteres.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "Tsunami: falha ao publicar a aparência do indicador: " + ex);
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

        // Restaura os nomes apenas depois de mantê-los ativos por 0,75 s.
        // O RPC é direcionado ao proprietário; o nome do host nunca é alterado.
        if (nomesTemporariosPendentes.Count > 0 &&
            Time.realtimeSinceStartup >= restaurarNomesTemporariosEm)
        {
            foreach (var item in nomesTemporariosPendentes)
                DefinirNomeLocalParaCliente(item.Key, item.Value);
            nomesTemporariosPendentes.Clear();
            restaurarNomesTemporariosEm = 0f;
        }

        // Usa o mecanismo já existente do mod: o SetName é direcionado apenas
        // ao cliente proprietário de cada jogador vanilla. O host não tem seu
        // próprio nome/outfit modificado nem serializado com o texto da onda.
        if (indicador != null && etapaInicializacaoIndicador == -1 &&
            Time.realtimeSinceStartup >= proximaEtapaInicializacaoIndicador)
        {
            var nomesTemporariosClientes = new List<KeyValuePair<PlayerControl, string>>();
            try
            {
                foreach (PlayerControl jogador in PlayerControl.AllPlayerControls.ToArray())
                {
                    if (jogador == null || jogador == indicador || jogador.Data == null ||
                        jogador.Data.Disconnected || jogador.OwnerId < 0 ||
                        jogador.OwnerId == AmongUsClient.Instance.ClientId ||
                        YouTubeVoteBridgePlugin.ClienteTemMod(jogador.OwnerId))
                        continue;

                    // Guarda o nome real antes de enviar o SetName temporário.
                    // O texto visual (nameText) pode já conter o aviso/onda e não é uma fonte segura.
                    string nomeOriginal = jogador.Data.PlayerName ?? string.Empty;
                    if (!nomesOriginais.ContainsKey(jogador.PlayerId))
                        nomesOriginais[jogador.PlayerId] = nomeOriginal;

                    if (DefinirNomeLocalParaCliente(jogador, NomeOnda))
                        nomesTemporariosClientes.Add(
                            new KeyValuePair<PlayerControl, string>(jogador, nomeOriginal));
                }

                var outfitIndicador = indicador.Data?.Outfits[PlayerOutfitType.Default];
                if (outfitIndicador != null)
                    outfitIndicador.PlayerName = "TSUNAMI";

                CustomNetObject.EnviarSpawn(indicador);
                if (PlayerControl.AllPlayerControls.Contains(indicador))
                    PlayerControl.AllPlayerControls.Remove(indicador);

                // Não restaura no mesmo Update: deixa o nome do tsunami visível
                // por 0,75 s para o cliente vanilla processar e renderizar o SetName.
                nomesTemporariosPendentes.Clear();
                nomesTemporariosPendentes.AddRange(nomesTemporariosClientes);
                restaurarNomesTemporariosEm = Time.realtimeSinceStartup + DuracaoNomeTemporarioTsunami;

                etapaInicializacaoIndicador = 1;
                proximaEtapaInicializacaoIndicador = Time.realtimeSinceStartup + 0.1f;
                AplicarAvisoNomes();
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami: spawn oculto enviado; SetName temporário direcionado aos clientes vanilla e restaurado.");
            }
            catch (Exception ex)
            {
                foreach (var item in nomesTemporariosClientes)
                    DefinirNomeLocalParaCliente(item.Key, item.Value);
                nomesTemporariosPendentes.Clear();
                restaurarNomesTemporariosEm = 0f;
                etapaInicializacaoIndicador = 0;
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Tsunami: falha ao enviar spawn oculto: " + ex);
            }
        }

        // O jogo pode recalcular o nome do indicador durante Update; reafirma
        // o TextMeshPro local do host sem depender da aparência do boneco.
        if (indicador != null)
        {
            try
            {
                if (indicador.cosmetics?.nameText != null)
                    indicador.cosmetics.nameText.text = NomeOnda;
            }
            catch { }
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
                // Prefere o nome de rede original; o texto visual pode já ter
                // recebido o aviso em uma atualização anterior.
                nomesOriginais[jogador.PlayerId] = jogador.Data.PlayerName ??
                    jogador.cosmetics?.nameText?.text ?? "";
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

            Vector2 p = jogador.GetTruePosition();

            // Primeiro envia a posição e, numa atualização posterior, solicita
            // a entrada. SnapTo e EnterVent no mesmo frame podem chegar fora de
            // ordem visual nos clientes vanilla, deixando o host em pé no chão.
            if (entradasDutoPendentes.TryGetValue(jogador.PlayerId, out float entrarEm))
            {
                if (agora < entrarEm)
                    continue;

                if (dutosProtegidos.TryGetValue(jogador.PlayerId, out int ventPendente) &&
                    jogador.MyPhysics != null && jogador.NetTransform != null)
                {
                    try
                    {
                        Vent dutoPendente = null;
                        foreach (Vent candidato in ShipStatus.Instance.AllVents)
                        {
                            if (candidato != null && candidato.Id == ventPendente)
                            {
                                dutoPendente = candidato;
                                break;
                            }
                        }

                        if (dutoPendente != null)
                        {
                            Vector2 centroDuto = (Vector2)dutoPendente.transform.position;
                            // Reafirma a posição antes do RPC de entrada.
                            jogador.NetTransform.RpcSnapTo(centroDuto);
                            jogador.MyPhysics.RpcEnterVent(ventPendente);

                            protegidos[jogador.PlayerId] = float.MaxValue;
                            posicaoXProtegida[jogador.PlayerId] = p.x;
                            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                                "Tsunami: entrada no duto enviada após SnapTo; PlayerId=" +
                                jogador.PlayerId + ", ventId=" + ventPendente +
                                ", host=" + jogador.AmOwner + ", inVentLocal=" + jogador.inVent + ".");
                        }
                        else
                        {
                            dutosProtegidos.Remove(jogador.PlayerId);
                        }
                    }
                    catch (Exception ex)
                    {
                        YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                            "Tsunami: falha na entrada atrasada do duto para PlayerId=" +
                            jogador.PlayerId + ": " + ex.Message);
                        dutosProtegidos.Remove(jogador.PlayerId);
                    }
                }

                entradasDutoPendentes.Remove(jogador.PlayerId);
                if (!protegidos.ContainsKey(jogador.PlayerId) &&
                    dutosProtegidos.ContainsKey(jogador.PlayerId))
                    protegidos[jogador.PlayerId] = float.MaxValue;
                continue;
            }

            // Quem já está abrigado permanece no duto até a onda passar da
            // posição em que entrou; só então começa a contar 1 segundo para sair.
            if (protegidos.TryGetValue(jogador.PlayerId, out float protecaoAte))
            {
                if (protecaoAte == float.MaxValue &&
                    posicaoXProtegida.TryGetValue(jogador.PlayerId, out float xAbrigo) &&
                    posicao.x > xAbrigo + Alcance)
                {
                    protegidos[jogador.PlayerId] = agora + ProtecaoDuto;
                    protecaoAte = agora + ProtecaoDuto;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "Tsunami passou pelo jogador " + jogador.Data.PlayerName +
                        "; saída do duto em 1 segundo.");
                }

                if (agora < protecaoAte) continue;

                // Solicita a saída uma vez e mantém a reentrada bloqueada até
                // o jogador afastar-se do duto. Isso evita o ciclo entrar/sair.
                byte idJogadorDuto = jogador.PlayerId;
                if (jogador.inVent && jogador.MyPhysics != null &&
                    dutosProtegidos.TryGetValue(idJogadorDuto, out int dutoSaida))
                {
                    if (!prazoSaidaDuto.ContainsKey(idJogadorDuto))
                    {
                        try { jogador.MyPhysics.RpcExitVent(dutoSaida); }
                        catch (Exception ex)
                        {
                            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                                "Tsunami: falha ao retirar jogador do duto: " + ex.Message);
                        }
                        prazoSaidaDuto[idJogadorDuto] = agora + 1.0f;
                    }

                    // Não repita o RPC para sempre. Mesmo que o jogo demore a
                    // atualizar inVent, libera a proteção e impede reentrada.
                    if (agora < prazoSaidaDuto[idJogadorDuto])
                        continue;
                }

                bloqueadosReentradaDuto.Add(idJogadorDuto);
                protegidos.Remove(idJogadorDuto);
                dutosProtegidos.Remove(idJogadorDuto);
                posicaoXProtegida.Remove(idJogadorDuto);
                prazoSaidaDuto.Remove(idJogadorDuto);
            }

            // O bloqueio só termina quando o jogador realmente se afasta
            // de todos os dutos. Esta verificação precisa ocorrer ANTES de
            // ignorar jogadores bloqueados; caso contrário, o código de liberação
            // abaixo nunca é alcançado.
            if (bloqueadosReentradaDuto.Contains(jogador.PlayerId))
            {
                bool pertoDeAlgumDuto = false;
                foreach (Vent duto in ShipStatus.Instance.AllVents)
                {
                    if (duto != null &&
                        Vector2.Distance(p, (Vector2)duto.transform.position) <= AlcanceDutoTsunami + 0.5f)
                    {
                        pertoDeAlgumDuto = true;
                        break;
                    }
                }

                if (pertoDeAlgumDuto)
                    continue;

                bloqueadosReentradaDuto.Remove(jogador.PlayerId);
            }

            if (jogador.inVent)
            {
                if (!protegidos.ContainsKey(jogador.PlayerId))
                {
                    // Se já estava no duto, espera a onda passar por essa posição.
                    protegidos[jogador.PlayerId] = float.MaxValue;
                    posicaoXProtegida[jogador.PlayerId] = p.x;
                    try
                    {
                        foreach (Vent duto in ShipStatus.Instance.AllVents)
                            if (duto != null && Vector2.Distance(p, (Vector2)duto.transform.position) < AlcanceIdentificarDutoTsunami)
                            { dutosProtegidos[jogador.PlayerId] = duto.Id; break; }
                    }
                    catch { }
                }
                continue;
            }

            if (atingidos.Contains(jogador.PlayerId)) continue;

            // Entrar no duto depende apenas de tocar/chegar perto dele, não da
            // distância do tsunami. Assim o jogador pode se esconder com antecedência.
            bool entrouNoDuto = false;
            try
            {
                foreach (Vent duto in ShipStatus.Instance.AllVents)
                {
                    if (duto == null || Vector2.Distance(p, (Vector2)duto.transform.position) > AlcanceDutoTsunami) continue;
                    if (jogador.MyPhysics == null || jogador.NetTransform == null) break;

                    // Primeiro publica a posição exata. O RPC de entrada é
                    // agendado para outra atualização (0,16 s depois), dando tempo
                    // aos clientes vanilla para processarem o SnapTo.
                    Vector2 posicaoDuto = (Vector2)duto.transform.position;
                    jogador.NetTransform.RpcSnapTo(posicaoDuto);
                    dutosProtegidos[jogador.PlayerId] = duto.Id;
                    entradasDutoPendentes[jogador.PlayerId] = agora + 0.16f;
                    entrouNoDuto = true;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "Tsunami: SnapTo enviado; entrada no duto agendada para " +
                        jogador.Data.PlayerName + ", ventId=" + duto.Id + ".");
                    break;
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogWarning("Tsunami: falha ao entrar no duto: " + ex.Message);
            }
            if (entrouNoDuto) continue;

            float distanciaX = p.x - posicao.x;
            if (distanciaX < -Alcance || distanciaX > Alcance)
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
                // Segue o fluxo usado pelo /exe do EHR: envia o RPC de exílio
                // nativo do jogo e marca o jogador como morto. RpcSetRole para
                // CrewmateGhost/ImpostorGhost não é um fluxo de morte vanilla e
                // pode fazer clientes sem o mod rejeitarem a sincronização.
                jogador.RpcExiled();
                jogador.Data.IsDead = true;
                mortosPeloTsunami.Add(id);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Tsunami: atraso concluído; PlayerId=" + id +
                    " eliminado pelo RPC nativo RpcExiled (fluxo compatível com vanilla/EHR).");
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
            // Atualiza os nomes de todos os jogadores neste cliente, não só
            // o nome local: o próprio nome geralmente não aparece sobre o próprio boneco.
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador == null ||
                    jogador.PlayerId == PlayerIdIndicador || jogador.Data == null ||
                    jogador.Data.Disconnected)
                    continue;

                // O SetName temporário da onda pode chegar antes do aviso.
                // Não memorize esse texto como se fosse o nome real do jogador.
                string nomeRede = jogador.Data.PlayerName ?? string.Empty;
                string nomeVisual = jogador.cosmetics?.nameText?.text ?? string.Empty;
                if (string.Equals(nomeRede, NomeOnda, StringComparison.Ordinal) ||
                    string.Equals(nomeVisual, NomeOnda, StringComparison.Ordinal) ||
                    nomeRede.Contains("<mark=#0099d1>") ||
                    nomeVisual.Contains("<mark=#0099d1>"))
                    continue;

                if (!nomesOriginais.ContainsKey(jogador.PlayerId))
                    nomesOriginais[jogador.PlayerId] = nomeRede.Length > 0
                        ? nomeRede
                        : nomeVisual;
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
        // Não limpar nomesOriginais aqui: o estágio de spawn já salvou os nomes
        // reais antes de enviar o SetName temporário aos clientes vanilla.
        PlayerControl local = PlayerControl.LocalPlayer;
        if (local == null || local.Data == null || local.Data.Disconnected)
            return;
        try
        {
            PlayerControl jogador = local;

            // Nunca sobrescreve um nome original já guardado com o texto visual:
            // nameText pode já conter o aviso do tsunami, causando restauração do próprio aviso.
            if (!nomesOriginais.ContainsKey(jogador.PlayerId))
            {
                string nomeOriginal = jogador.Data.Outfits[PlayerOutfitType.Default].PlayerName;
                if (string.IsNullOrEmpty(nomeOriginal))
                    nomeOriginal = jogador.Data.PlayerName ?? string.Empty;

                // Evita registrar o aviso como nome original se outro Update já o aplicou.
                if (nomeOriginal.Contains("Um tsunami está vindo") ||
                    nomeOriginal.Contains("Vá para o duto mais próximo"))
                    nomeOriginal = jogador.Data.PlayerName ?? string.Empty;

                nomesOriginais[jogador.PlayerId] = nomeOriginal;
            }

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
        string nomeAviso = nomeOriginal +
            "\n<color=" + cor + ">Um tsunami está vindo\nVá para o duto mais próximo</color>";

        // Para o próprio jogador, usa o mesmo caminho SetName local dos comandos.
        // Isso atualiza o nome pelo PlayerControl, em vez de alterar só o texto
        // visual, que o jogo pode sobrescrever durante a partida.
        if (jogador.AmOwner)
        {
            YouTubeVoteBridgePlugin.DefinirNomeLocal(jogador, nomeAviso);
            return;
        }

        // Os outros jogadores são alterados apenas na interface deste cliente.
        jogador.cosmetics.nameText.text = nomeAviso;
    }

    // Altera o nome apenas na tela do cliente dono do jogador.
    // Usa o mesmo GameDataTo + SetName direcionado do envio de mensagens do mod,
    // sem publicar a mudança de nome para todos os clientes.
    private static bool DefinirNomeLocalParaCliente(PlayerControl jogador, string nome)
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || AmongUsClient.Instance == null ||
            jogador == null || jogador.Data == null || jogador.OwnerId < 0 ||
            jogador.OwnerId == AmongUsClient.Instance.ClientId)
            return false;

        MessageWriter writer = null;
        try
        {
            writer = MessageWriter.Get(SendOption.Reliable);
            writer.StartMessage(6); // GameDataTo: somente o cliente proprietário
            writer.Write(AmongUsClient.Instance.GameId);
            writer.WritePacked(jogador.OwnerId);

            writer.StartMessage(2);
            writer.WritePacked(jogador.NetId);
            writer.Write((byte)RpcCalls.SetName);
            writer.Write(jogador.Data.NetId);
            writer.Write(nome);
            writer.EndMessage();

            writer.EndMessage();
            AmongUsClient.Instance.SendOrDisconnect(writer);
            writer.Recycle();
            writer = null;
            return true;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha ao definir nome local para PlayerId=" +
                jogador.PlayerId + ": " + ex.Message);
            return false;
        }
        finally
        {
            if (writer != null)
            {
                try { writer.Recycle(); } catch { }
            }
        }
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

            string nomeAviso = nomeOriginal +
                "\n<color=" + cor + ">Um tsunami está vindo\nVá para o duto mais próximo</color>";

            // Reenvia periodicamente porque o jogo pode reaplicar o nome original.
            if (DefinirNomeLocalParaCliente(jogador, nomeAviso))
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
                    if (jogador.AmOwner)
                        YouTubeVoteBridgePlugin.DefinirNomeLocal(jogador, nomeOriginal);

                    if (jogador.cosmetics != null && jogador.cosmetics.nameText != null)
                        jogador.cosmetics.nameText.text = nomeOriginal;
                }
                catch { }

                if (YouTubeVoteBridgePlugin.EhHost() && AmongUsClient.Instance != null &&
                    jogador.OwnerId >= 0 && jogador.OwnerId != AmongUsClient.Instance.ClientId &&
                    nomesAlteradosRede.Contains(jogador.PlayerId))
                    DefinirNomeLocalParaCliente(jogador, nomeOriginal);
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
        const float DistanciaAtras = 7.0f;
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
        FlashOverlay.DefinirFlashTsunami(false);
        RestaurarAvisoNomes();
        protegidos.Clear();
        dutosProtegidos.Clear();
        entradasDutoPendentes.Clear();
        posicaoXProtegida.Clear();
        bloqueadosReentradaDuto.Clear();
        prazoSaidaDuto.Clear();
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

    public static void LimparEstadoPartida(string motivo)
    {
        // Restaura explicitamente o movimento antes de Limpar() apagar os registros.
        // Limpar() também faz essa restauração para mortes pendentes; esta passagem
        // protege qualquer jogador que ainda esteja registrado durante a transição.
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null) continue;
            if (movimentoOriginal.TryGetValue(jogador.PlayerId, out bool moveable))
            {
                try { jogador.moveable = moveable; } catch { }
            }
        }

        Limpar();

        atingidos.Clear();
        mortosPeloTsunami.Clear();
        mortesPendentes.Clear();
        alturaAoAtingir.Clear();
        movimentoOriginal.Clear();
        proximoSnapJogador.Clear();
        protegidos.Clear();
        dutosProtegidos.Clear();
        posicaoXProtegida.Clear();
        bloqueadosReentradaDuto.Clear();
        prazoSaidaDuto.Clear();
        reuniaoAguardandoFim = false;
        reuniaoFoiAberta = false;

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "Tsunami: registros de jogadores mortos/atingidos limpos porque " + motivo + ".");
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

// Limpa os registros de jogadores convertidos em Ghost pelo tsunami ao iniciar
// uma nova partida ou quando a partida atual termina.
internal static class EventoTsunamiLimpezaPartida
{
    public static void LimparRegistros(string motivo)
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        try
        {
            EventoTsunami.LimparEstadoPartida(motivo);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "Tsunami: falha ao limpar registros da partida: " + ex.Message);
        }
    }
}

[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.BeginGame))]
internal static class EventoTsunamiLimparAoIniciarPatch
{
    public static void Prefix()
    {
        EventoTsunamiLimpezaPartida.LimparRegistros("host iniciou a partida");
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.RpcEndGame))]
internal static class EventoTsunamiLimparAoTerminarPatch
{
    public static void Prefix()
    {
        EventoTsunamiLimpezaPartida.LimparRegistros("partida terminou");
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

        while (EventoTsunami.AvisoAtivo && Time.realtimeSinceStartup < terminarEm)
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

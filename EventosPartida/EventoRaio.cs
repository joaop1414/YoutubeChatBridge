using System;
using System.Collections.Generic;
using System.Linq;
using MoreGamemodes;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Raios: dois indicadores de rede (aviso e descarga) são criados sob demanda
/// no primeiro disparo e reutilizados/ocultados até o fim da partida. O som é local,
/// portanto só é reproduzido por clientes que possuem este mod.
/// </summary>
internal sealed class EventoRaio : EventoVotacao
{
    private const byte PlayerIdAviso = 252;
    private const byte PlayerIdRaio = 251;
    private const float IntervaloMinimo = 5f;
    private const float IntervaloMaximo = 10f;
    private const float AlcanceAtingido = 0.85f;
    private const float AlcanceSom = 5.5f;
    private const float DuracaoAviso = 1.15f;
    private const float OcultoX = -1000f;

    private static bool armado;
    private static bool ativo;
    private static float proximoRaioEm;
    private static float instanteImpacto;
    private static Vector2 localDoRaio;
    private static bool descargaPendente;
    private static bool somTocadoNestaDescarga;
    private static PlayerControl bonecoAviso;
    private static PlayerControl bonecoRaio;
    private static int etapaInicializacao;
    private static float proximaEtapa;
    private static AudioClip somRaio;
    private static AudioSource fonteSom;
    private static bool tentativaCriarSom;
    private static bool alertaNoisemakerAtivo;
    private static Vector2 ultimaPosicaoAlerta;
    private static readonly System.Random Sorteador = new();

    // Cinco variações textuais provisórias. Podem ser trocadas pelos cinco
    // desenhos rich-text definitivos quando forem adicionados ao projeto.
    private static readonly string[] DesenhosRaio =
    {
        "<color=#FFFFFF><size=5><line-height=80%>W\nW\nW\nW\nW\nW</line-height></size></color>",
        "<color=#EAF8FF><size=5><line-height=80%>W\n W\nW\n W\nW\n W</line-height></size></color>",
        "<color=#FFFFFF><size=5><line-height=80%>W\n W\n W\nW\n W\nW</line-height></size></color>",
        "<color=#D7F2FF><size=5><line-height=80%>W\nW\n W\n W\nW\n W</line-height></size></color>",
        "<color=#FFFFFF><size=5><line-height=80%>W\n W\nW\nW\n W\nW</line-height></size></color>"
    };
    private const string DesenhoAviso =
        "<color=#FFFF00><size=3><b>!</b></size></color>";

    public override string Nome => "Raio";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        armado = true;
        if (ShipStatus.Instance != null && LobbyTagManager.PartidaIniciada())
            IniciarPartida();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo("[F7/Raio] Evento armado.");
    }

    internal static void IniciarPartida()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() ||
            ShipStatus.Instance == null || AmongUsClient.Instance == null)
            return;

        if (!armado)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Raio] IniciarPartida ignorado: o evento não estava armado quando o mapa iniciou.");
            return;
        }

        if (ativo) return;

        // A ativação do evento não cria PlayerPrefab. Os indicadores só serão
        // instanciados quando chegar a hora do primeiro raio.
        ativo = true;
        descargaPendente = false;
        ocultarDepoisDoImpacto = false;
        etapaInicializacao = (bonecoAviso != null && bonecoRaio != null) ? 3 : 0;
        CriarSomLocal();
        AgendarProximoRaio();
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Evento ativado; os indicadores só serão criados quando o primeiro raio for disparado.");
    }

    private static void CriarBonecosPersistentes()
    {
        if (bonecoAviso != null && bonecoRaio != null) return;
        Vector2 oculto = new(OcultoX, 0f);
        if (bonecoAviso == null)
            bonecoAviso = CriarBoneco(PlayerIdAviso, "RAIO_AVISO", oculto);
        if (bonecoRaio == null)
            bonecoRaio = CriarBoneco(PlayerIdRaio, "RAIO_VISUAL", oculto);
        etapaInicializacao = 1;
        proximaEtapa = Time.realtimeSinceStartup + 0.12f;
    }

    private static PlayerControl CriarBoneco(byte playerId, string nome, Vector2 posicao)
    {
        PlayerControl boneco = UnityEngine.Object.Instantiate(
            AmongUsClient.Instance.PlayerPrefab, posicao, Quaternion.identity);
        boneco.PlayerId = playerId;
        boneco.isNew = false;
        boneco.notRealPlayer = true;
        boneco.NetTransform.SnapTo(posicao);
        var outfit = boneco.Data?.Outfits[PlayerOutfitType.Default];
        if (outfit != null) outfit.PlayerName = nome;
        try
        {
            if (boneco.cosmetics?.currentBodySprite?.BodySprite != null)
                boneco.cosmetics.currentBodySprite.BodySprite.color = Color.clear;
            if (boneco.cosmetics?.colorBlindText != null)
                boneco.cosmetics.colorBlindText.color = Color.clear;
        }
        catch { }
        return boneco;
    }

    internal static void Atualizar()
    {
        if (AmongUsClient.Instance == null || ShipStatus.Instance == null) return;

        // O áudio é criado localmente em cada cliente com o mod; o controle do
        // evento e das mortes continua exclusivo do host.
        CriarSomLocal();
        AtualizarSomLocal();
        AtualizarAlertaNoisemakerLocal();

        if (!YouTubeVoteBridgePlugin.EhHost() || !ativo) return;

        if (etapaInicializacao == 1 && Time.realtimeSinceStartup >= proximaEtapa)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Raio] Enviando spawn oculto dos dois indicadores; canto reservado X=" + OcultoX + ".");
            InicializarBonecoRede(bonecoAviso);
            InicializarBonecoRede(bonecoRaio);
            etapaInicializacao = 2;
            proximaEtapa = Time.realtimeSinceStartup + 0.15f;
            return;
        }
        if (etapaInicializacao == 2 && Time.realtimeSinceStartup >= proximaEtapa)
        {
            PrepararBoneco(bonecoAviso, DesenhoAviso);
            PrepararBoneco(bonecoRaio, DesenhosRaio[0]);
            OcultarBoneco(bonecoAviso);
            OcultarBoneco(bonecoRaio);
            etapaInicializacao = 3;
            return;
        }
        if (etapaInicializacao < 3 || MeetingHud.Instance != null) return;

        if (ocultarDepoisDoImpacto && Time.realtimeSinceStartup >= proximoRaioEm)
        {
            ocultarDepoisDoImpacto = false;
            OcultarBoneco(bonecoRaio);
            AgendarProximoRaio();
            return;
        }

        if (descargaPendente)
        {
            if (Time.realtimeSinceStartup >= instanteImpacto)
                AplicarImpacto();
            return;
        }

        if (Time.realtimeSinceStartup >= proximoRaioEm)
            IniciarDescarga();
    }

    private static void InicializarBonecoRede(PlayerControl boneco)
    {
        if (boneco == null) return;
        CustomNetObject.EnviarSpawn(boneco);
        if (PlayerControl.AllPlayerControls.Contains(boneco))
            PlayerControl.AllPlayerControls.Remove(boneco);
        CustomNetObject.AssociarClientes(boneco, boneco.PlayerId);
    }

    private static void PrepararBoneco(PlayerControl boneco, string arte)
    {
        if (boneco == null) return;
        try
        {
            CustomNetObject.EnviarAparenciaSprite(boneco, arte, out _);
            CustomNetObject.EnviarSetName(boneco, arte);
            if (boneco.cosmetics?.nameText != null)
                boneco.cosmetics.nameText.text = arte;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Raio] Aparência do indicador não pôde ser enviada: " + ex.Message);
        }
    }

    private static void IniciarDescarga()
    {
        // Spawn sob demanda: nenhum boneco existe enquanto não for necessário.
        // A inicialização de rede acontece em etapas no Atualizar(), como no EHR.
        if (bonecoAviso == null || bonecoRaio == null || etapaInicializacao < 3)
        {
            try
            {
                if (bonecoAviso == null || bonecoRaio == null)
                {
                    etapaInicializacao = 0;
                    CriarBonecosPersistentes();
                    proximoRaioEm = Time.realtimeSinceStartup;
                    YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                        "[F7/Raio] Primeiro raio solicitado: criando indicadores agora, sob demanda.");
                }
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Raio] Falha ao criar indicadores sob demanda: " + ex);
                AgendarProximoRaio();
            }
            return;
        }

        List<PlayerControl> vivos = new();
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            if (jogador != null && jogador != bonecoAviso && jogador != bonecoRaio &&
                jogador.Data != null && !jogador.Data.IsDead && !jogador.Data.Disconnected)
                vivos.Add(jogador);
        if (vivos.Count == 0) { AgendarProximoRaio(); return; }

        PlayerControl referencia = vivos[Sorteador.Next(vivos.Count)];
        Vector2 centro = referencia.transform.position;
        localDoRaio = centro + new Vector2(
            (float)(Sorteador.NextDouble() * 2.8 - 1.4),
            (float)(Sorteador.NextDouble() * 2.0 - 1.0));

        int variacao = Sorteador.Next(DesenhosRaio.Length);
        PrepararBoneco(bonecoRaio, DesenhosRaio[variacao]);
        CustomNetObject.EnviarPosicao(bonecoAviso, localDoRaio);
        CustomNetObject.EnviarSetName(bonecoAviso, DesenhoAviso);
        CustomNetObject.EnviarPosicao(bonecoRaio, localDoRaio);
        CustomNetObject.EnviarSetName(bonecoRaio, DesenhosRaio[variacao]);
        definirTextoLocal(bonecoAviso, DesenhoAviso);
        definirTextoLocal(bonecoRaio, DesenhosRaio[variacao]);
        MostrarBoneco(bonecoAviso);
        OcultarBoneco(bonecoRaio);

        descargaPendente = true;
        somTocadoNestaDescarga = false;
        instanteImpacto = Time.realtimeSinceStartup + DuracaoAviso;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Aviso exibido; descarga em " + DuracaoAviso + " s.");
    }

    private static void AplicarImpacto()
    {
        MostrarBoneco(bonecoRaio);
        OcultarBoneco(bonecoAviso);
        foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
        {
            if (jogador == null || jogador == bonecoAviso || jogador == bonecoRaio ||
                jogador.Data == null || jogador.Data.IsDead || jogador.Data.Disconnected)
                continue;
            if (Vector2.Distance((Vector2)jogador.transform.position, localDoRaio) > AlcanceAtingido)
                continue;
            try
            {
                jogador.RpcMurderPlayer(jogador, true);
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Raio] " + jogador.Data.PlayerName + " foi atingido e cometeu suicídio.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "[F7/Raio] Falha ao aplicar suicídio: " + ex.Message);
            }
        }
        // Deixa o desenho do raio visível brevemente e depois apenas o esconde.
        proximoRaioEm = Time.realtimeSinceStartup + 0.35f;
        descargaPendente = false;
        ocultarDepoisDoImpacto = true;
    }

    private static bool ocultarDepoisDoImpacto;

    private static void AgendarProximoRaio()
    {
        proximoRaioEm = Time.realtimeSinceStartup +
            (float)(IntervaloMinimo + Sorteador.NextDouble() * (IntervaloMaximo - IntervaloMinimo));
    }

    private static void MostrarBoneco(PlayerControl boneco)
    {
        if (boneco == null) return;
        CustomNetObject.EnviarPosicao(boneco, localDoRaio);
    }

    private static void OcultarBoneco(PlayerControl boneco)
    {
        if (boneco == null) return;
        try { CustomNetObject.EnviarSetName(boneco, "<color=#00000000>.</color>"); } catch { }
        try { CustomNetObject.EnviarPosicao(boneco, new Vector2(OcultoX, 0f)); } catch { }
        definirTextoLocal(boneco, "<color=#00000000>.</color>");
    }

    private static void definirTextoLocal(PlayerControl boneco, string texto)
    {
        try { if (boneco?.cosmetics?.nameText != null) boneco.cosmetics.nameText.text = texto; } catch { }
    }

    // Reproduz o indicador nativo do Noisemaker em cada cliente com o mod.
    // O aviso replicado pelo host determina o local; não é necessário matar
    // alguém nem atribuir a função Noisemaker a um jogador.
    private static void AtualizarAlertaNoisemakerLocal()
    {
        PlayerControl aviso = bonecoAviso;
        if (aviso == null || aviso.PlayerId != PlayerIdAviso)
        {
            try
            {
                foreach (PlayerControl candidato in UnityEngine.Object.FindObjectsOfType<PlayerControl>())
                {
                    if (candidato != null && candidato.PlayerId == PlayerIdAviso)
                    {
                        aviso = candidato;
                        break;
                    }
                }
            }
            catch { }
        }

        if (aviso == null) return;
        Vector2 posicao = aviso.transform.position;
        if (posicao.x < -500f)
        {
            alertaNoisemakerAtivo = false;
            return;
        }

        // Um único alerta por aviso/descarga, sem recriá-lo a cada frame.
        if (alertaNoisemakerAtivo &&
            Vector2.Distance(posicao, ultimaPosicaoAlerta) < 0.2f)
            return;

        alertaNoisemakerAtivo = true;
        ultimaPosicaoAlerta = posicao;

        try
        {
            var role = RoleManager.Instance?.GetRole(RoleTypes.Noisemaker);
            if (role == null) return;

            var tipoRole = role.GetType();
            var campoPrefab = tipoRole.GetField("deathArrowPrefab");
            object prefab = campoPrefab != null ? campoPrefab.GetValue(role) :
                tipoRole.GetProperty("deathArrowPrefab")?.GetValue(role);
            if (prefab is not GameObject prefabObjeto || prefabObjeto == null) return;

            GameObject setaObjeto = UnityEngine.Object.Instantiate(
                prefabObjeto, Vector3.zero, Quaternion.identity);
            if (setaObjeto == null) return;

            Component seta = setaObjeto.GetComponent("NoisemakerArrow");
            if (seta == null)
            {
                UnityEngine.Object.Destroy(setaObjeto);
                return;
            }

            var tipoSeta = seta.GetType();
            var metodoDuracao = tipoSeta.GetMethod("SetDuration");
            metodoDuracao?.Invoke(seta, new object[] { DuracaoAviso });
            var campoAlvo = tipoSeta.GetField("target");
            if (campoAlvo != null) campoAlvo.SetValue(seta, posicao);
            else tipoSeta.GetProperty("target")?.SetValue(seta, posicao);
            setaObjeto.SetActive(true);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Raio] Não foi possível mostrar o alerta nativo Noisemaker: " + ex.Message);
        }
    }

    private static void CriarSomLocal()
    {
        if (somRaio != null || tentativaCriarSom) return;
        tentativaCriarSom = true;
        try
        {
            const int taxa = 22050;
            const int amostras = 11025;
            float[] dados = new float[amostras];
            uint estado = 0x12345678;
            for (int i = 0; i < amostras; i++)
            {
                estado = estado * 1664525u + 1013904223u;
                float ruido = ((estado >> 8) / 8388608f) - 1f;
                float envelope = (float)Math.Exp(-i / 1700.0);
                float tom = (float)Math.Sin(2.0 * Math.PI * (95.0 + 30.0 * Math.Sin(i / 800.0)) * i / taxa);
                dados[i] = (ruido * 0.62f + tom * 0.38f) * envelope * 0.75f;
            }
            somRaio = AudioClip.Create("YouTubeVoteBridge_Raio", amostras, 1, taxa, false);
            somRaio.SetData(dados, 0);
            GameObject objetoSom = new("YouTubeVoteBridge_RaioAudio");
            UnityEngine.Object.DontDestroyOnLoad(objetoSom);
            fonteSom = objetoSom.AddComponent<AudioSource>();
            fonteSom.clip = somRaio;
            fonteSom.playOnAwake = false;
            fonteSom.spatialBlend = 0f;
            fonteSom.volume = 0.8f;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Raio] Não foi possível preparar o som local: " + ex.Message);
        }
    }

    private static bool somGlobalTocadoNestaDescarga;

    private static void AtualizarSomLocal()
    {
        if (PlayerControl.LocalPlayer == null) return;

        // O som deve acompanhar a DESCARGA, não o aviso amarelo anterior.
        // O Tsunami usa o mesmo padrão de PlayerControl falso sincronizado:
        // encontrar o objeto pelo PlayerId e observar sua posição replicada.
        PlayerControl raio = bonecoRaio;
        if (raio == null || raio.PlayerId != PlayerIdRaio)
        {
            try
            {
                foreach (PlayerControl candidato in UnityEngine.Object.FindObjectsOfType<PlayerControl>())
                {
                    if (candidato != null && candidato.PlayerId == PlayerIdRaio)
                    {
                        raio = candidato;
                        break;
                    }
                }
            }
            catch { }
        }

        if (raio == null) return;

        Vector2 posicaoRaio = raio.transform.position;
        bool visivel = posicaoRaio.x >= -500f;
        if (!visivel)
        {
            somGlobalTocadoNestaDescarga = false;
            return;
        }

        if (somGlobalTocadoNestaDescarga) return;

        // Se a criação inicial do AudioSource falhou, tenta novamente.
        if (fonteSom == null || somRaio == null)
        {
            tentativaCriarSom = false;
            CriarSomLocal();
        }
        if (fonteSom == null || somRaio == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Raio] O indicador apareceu, mas o áudio não está pronto; o trovão não foi reproduzido.");
            return;
        }

        try
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Raio] Trovão detectado! PlayerId=" + raio.PlayerId +
                ", posição=" + posicaoRaio + ". Tentando reproduzir áudio local.");
            fonteSom.PlayOneShot(somRaio, 1f);
            somGlobalTocadoNestaDescarga = true;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Raio] PlayOneShot chamado com sucesso para o trovão.");
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Raio] Falha ao reproduzir o trovão: " + ex);
        }
    }

    internal static void Resetar(bool preservarArmado = false)
    {
        bool estavaArmado = armado;
        ativo = false;
        armado = preservarArmado && estavaArmado;
        descargaPendente = false;
        ocultarDepoisDoImpacto = false;
        alertaNoisemakerAtivo = false;
        somGlobalTocadoNestaDescarga = false;
        somTocadoNestaDescarga = false;
        etapaInicializacao = 0;
        proximoRaioEm = 0f;
        instanteImpacto = 0f;

        // O CoStartGame pode ocorrer depois de ShipStatus.Start. Destrói os
        // objetos antigos e zera as referências para que a próxima chamada de
        // IniciarPartida faça um novo spawn/associação nesta partida.
        DestruirBoneco(ref bonecoAviso);
        DestruirBoneco(ref bonecoRaio);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Estado reiniciado. Armado preservado=" + armado +
            "; aguardando inicialização dos indicadores no mapa.");
    }

    private static void DestruirBoneco(ref PlayerControl boneco)
    {
        if (boneco == null) return;
        try
        {
            if (PlayerControl.AllPlayerControls.Contains(boneco))
                PlayerControl.AllPlayerControls.Remove(boneco);
        }
        catch { }
        try { UnityEngine.Object.Destroy(boneco.gameObject); } catch { }
        boneco = null;
    }
}

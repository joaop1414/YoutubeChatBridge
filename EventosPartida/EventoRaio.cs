using System;
using System.Collections.Generic;
using System.Linq;
using MoreGamemodes;
using AmongUs.GameOptions;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Raios: dois indicadores de rede são criados sob demanda para cada descarga
/// e removidos da rede após o efeito. Nenhum indicador é reutilizado. O som é local.
/// </summary>
internal sealed class EventoRaio : EventoVotacao
{
    private const byte PlayerIdAviso = 252;
    private const byte PlayerIdRaio = 251;
    private const float AtrasoPrimeiroRaio = 5f;
    private const float IntervaloMinimo = 5f;
    private const float IntervaloMaximo = 10f;
    private const float AlcanceAtingido = 0.85f;
    private const float AlcanceSom = 5.5f;
    private const float DuracaoAviso = 1.15f;
    private const float OcultoX = -1000f;

    private static bool armado;
    private static bool ativo;
    private static bool aguardandoFimRoleReveal;
    private static float proximoRaioEm;
    private static float instanteImpacto;
    private static Vector2 localDoRaio;
    private static string desenhoAtualRaio = string.Empty;
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

    // Desenho único do raio, usando as tags rich-text fornecidas.
    private const string DesenhoRaio = "<cspace=-0.16em><line-height=90%><size=100%><color=#00000000>WWWW</color><mark=#fff880>WW</mark><color=#00000000>WW</color>\n<color=#00000000>WWW</color><mark=#ffea00>W</mark><mark=#ffffff>W</mark><mark=#ffea00>W</mark><color=#00000000>WW</color>\n<color=#00000000>WW</color><mark=#ffea00>W</mark><mark=#ffffff>W</mark><mark=#ffea00>W</mark><color=#00000000>WWW</color>\n<color=#00000000>W</color><mark=#ffea00>W</mark><mark=#ffffff>W</mark><mark=#ffea00>W</mark><color=#00000000>WWWW</color>\n<mark=#ffea00>WWWWWWWW</mark>\n<color=#00000000>WWW</color><mark=#ffea00>W</mark><mark=#ffffff>W</mark><mark=#ffea00>W</mark><color=#00000000>WW</color>\n<color=#00000000>WW</color><mark=#ffea00>W</mark><mark=#ffffff>W</mark><mark=#ffea00>W</mark><color=#00000000>WWW</color>\n<color=#00000000>W</color><mark=#ffea00>W</mark><mark=#ffffff>W</mark><color=#00000000>WWWWW</color>\n<mark=#ffea00>W</mark><color=#00000000>WWWWWWW</color>";
    // Mantém o aviso abaixo do limite prático de tamanho do RPC SetName vanilla.
    // Evita desconexão ao transmitir um nome maior que o pacote aceito.
    private const string DesenhoAviso =
        "<cspace=0.12em><line-height=90%><mark=#000000>WWWWWWWW</mark>\n" +
        "<mark=#000000>WW</mark><mark=#ffdd00>WWWW</mark><mark=#000000>WW</mark>\n" +
        "<mark=#000000>WW</mark><mark=#ffdd00>WWWW</mark><mark=#000000>WW</mark>\n" +
        "<mark=#000000>WW</mark><mark=#ffdd00>WWWW</mark><mark=#000000>WW</mark>\n" +
        "<mark=#000000>WWWWWWWW</mark>\n" +
        "<mark=#000000>WW</mark><mark=#ffdd00>WWWW</mark><mark=#000000>WW</mark>\n" +
        "<mark=#000000>WW</mark><mark=#ffdd00>WWWW</mark><mark=#000000>WW</mark>\n" +
        "<mark=#000000>WW</mark><mark=#ffdd00>WWWW</mark><mark=#000000>WW</mark>\n" +
        "<mark=#000000>WWWWWWWW</mark>";

    public override string Nome => "Tempestade elétrca - Raios podem matar os jogadores";

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

        // Como no fluxo do tsunami: os indicadores são criados uma vez e ficam
        // fora do mapa até serem movimentados para cada descarga.
        ativo = true;
        descargaPendente = false;
        ocultarDepoisDoImpacto = false;
        etapaInicializacao = (bonecoAviso != null && bonecoRaio != null) ? 4 : 0;
        CriarSomLocal();
        // A criação começa somente quando a revelação de função terminar.
        aguardandoFimRoleReveal = true;
        proximoRaioEm = float.MaxValue;
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Evento preparado; aguardando o fim da revelação de função.");
    }

    internal static void AoSairDaRoleReveal()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || !armado ||
            ShipStatus.Instance == null || !ativo || !aguardandoFimRoleReveal)
            return;

        aguardandoFimRoleReveal = false;
        proximoRaioEm = float.MaxValue;
        try
        {
            if (bonecoAviso == null || bonecoRaio == null)
            {
                etapaInicializacao = 0;
                CriarBonecosTemporarios();
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "[F7/Raio] Indicadores criados após a revelação; inicializando rede uma única vez.");
            }
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[F7/Raio] Falha ao criar indicadores após a revelação: " + ex);
            AgendarProximoRaio();
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Revelação encerrada; aguardando a inicialização dos indicadores.");
    }

    private static void CriarBonecosTemporarios()
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
            EnviarSpawnBonecoRede(bonecoAviso);
            EnviarSpawnBonecoRede(bonecoRaio);
            etapaInicializacao = 2;
            // O EHR espera cerca de 0,1 s após o Spawn antes de associar o
            // NetId ao PlayerId em cada cliente vanilla.
            proximaEtapa = Time.realtimeSinceStartup + 0.12f;
            return;
        }
        if (etapaInicializacao == 2 && Time.realtimeSinceStartup >= proximaEtapa)
        {
            AssociarBonecoRede(bonecoAviso);
            AssociarBonecoRede(bonecoRaio);
            etapaInicializacao = 3;
            proximaEtapa = Time.realtimeSinceStartup + 0.15f;
            return;
        }
        if (etapaInicializacao == 3 && Time.realtimeSinceStartup >= proximaEtapa)
        {
            PrepararBoneco(bonecoAviso, DesenhoAviso);
            PrepararBoneco(bonecoRaio, DesenhoRaio);
            OcultarBoneco(bonecoAviso);
            OcultarBoneco(bonecoRaio);
            etapaInicializacao = 4;
            proximoRaioEm = Time.realtimeSinceStartup + AtrasoPrimeiroRaio;
            YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                "[F7/Raio] Indicadores prontos; primeiro raio em " + AtrasoPrimeiroRaio + " segundos.");
            return;
        }
        // A etapa 0 é o estado normal antes do primeiro raio ou após o
        // despawn. Não retornar sem chamar IniciarDescarga: ela cria os
        // indicadores sob demanda e inicia a sequência de spawn em etapas.
        if (etapaInicializacao < 4)
        {
            if (etapaInicializacao == 0 &&
                !ocultarDepoisDoImpacto &&
                !descargaPendente &&
                Time.realtimeSinceStartup >= proximoRaioEm &&
                MeetingHud.Instance == null)
                IniciarDescarga();
            return;
        }

        if (MeetingHud.Instance != null) return;

        if (ocultarDepoisDoImpacto && Time.realtimeSinceStartup >= proximoRaioEm)
        {
            ocultarDepoisDoImpacto = false;
            // Igual ao tsunami: mantém os NPCs de rede e apenas os deixa ocultos.
            // O próximo efeito só envia SnapTo para reposicioná-los.
            OcultarBoneco(bonecoAviso);
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

    private static void EnviarSpawnBonecoRede(PlayerControl boneco)
    {
        if (boneco == null) return;
        CustomNetObject.EnviarSpawn(boneco);
        if (PlayerControl.AllPlayerControls.Contains(boneco))
            PlayerControl.AllPlayerControls.Remove(boneco);
    }

    private static void AssociarBonecoRede(PlayerControl boneco)
    {
        if (boneco == null) return;
        int associados = CustomNetObject.AssociarClientes(boneco, boneco.PlayerId);
        try
        {
            // O EHR faz isso após associar o NPC para que os fluxos de cosméticos
            // e Shapeshift encontrem dados de jogador válidos no objeto artificial.
            if (PlayerControl.LocalPlayer?.Data != null)
                boneco.CachedPlayerData = PlayerControl.LocalPlayer.Data;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Raio] Não foi possível configurar CachedPlayerData: " + ex.Message);
        }
        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            $"[F7/Raio] Associação do indicador concluída: PlayerId={boneco.PlayerId}, NetId={boneco.NetId}, clientes={associados}.");
    }

    private static void PrepararBoneco(PlayerControl boneco, string arte)
    {
        if (boneco == null) return;
        try
        {
            CustomNetObject.EnviarAparenciaSprite(boneco, arte, out _);
            // Não transmitir o desenho longo via RPC SetName: o sprite já é
            // enviado pelo fluxo de aparência/Shapeshift usado pelo EHR.
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
        // Os indicadores são persistentes, como no tsunami. Só os criamos caso
        // ainda não existam; durante os raios seguintes apenas mudamos a posição.
        if (bonecoAviso == null || bonecoRaio == null || etapaInicializacao < 4)
        {
            try
            {
                if (bonecoAviso == null || bonecoRaio == null)
                {
                    etapaInicializacao = 0;
                    CriarBonecosTemporarios();
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

        desenhoAtualRaio = DesenhoRaio;
        // A aparência já foi enviada uma única vez durante a inicialização.
        // Não repetir Shapeshift/serialização de cosméticos a cada descarga.
        // Os desenhos foram transmitidos na preparação via aparência/Shapeshift.
        // Aqui só movemos os indicadores; reenviar arte grande em SetName pode
        // fazer clientes vanilla rejeitarem a mensagem e expulsarem o host.
        CustomNetObject.EnviarPosicao(bonecoAviso, localDoRaio);
        CustomNetObject.EnviarPosicao(bonecoRaio, localDoRaio);
        definirTextoLocal(bonecoAviso, DesenhoAviso);
        definirTextoLocal(bonecoRaio, DesenhoRaio);
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
        // OcultarBoneco torna o nome transparente; restauramos o desenho antes
        // de mover o indicador para a posição visível do impacto.
        try
        {
            // O desenho já está aplicado no objeto; não precisa reenviar SetName.
            definirTextoLocal(bonecoRaio, desenhoAtualRaio);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[F7/Raio] Não foi possível restaurar o desenho do impacto: " + ex.Message);
        }
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
        // Mantém o desenho do raio visível brevemente; depois os indicadores
        // são ocultados e reutilizados no próximo raio, como no tsunami.
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
        // Oculte movendo o indicador para fora do mapa. Não sobrescreva o sprite
        // com SetName, pois o desenho é transportado pelo fluxo de aparência.
        try { CustomNetObject.EnviarPosicao(boneco, new Vector2(OcultoX, 0f)); } catch { }
    }

    private static void definirTextoLocal(PlayerControl boneco, string texto)
    {
        try
        {
            if (boneco?.cosmetics?.nameText != null)
                boneco.cosmetics.nameText.text = texto == "<color=#00000000>.</color>"
                    ? texto
                    : "<size=14><br></size>" + texto;
        }
        catch { }
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
        aguardandoFimRoleReveal = false;
        armado = preservarArmado && estavaArmado;
        descargaPendente = false;
        ocultarDepoisDoImpacto = false;
        alertaNoisemakerAtivo = false;
        somGlobalTocadoNestaDescarga = false;
        somTocadoNestaDescarga = false;
        etapaInicializacao = 0;
        proximoRaioEm = 0f;
        instanteImpacto = 0f;

        // O CoStartGame pode ocorrer depois de ShipStatus.Start. Remove os objetos
        // desta partida e zera as referências.
        DestruirBoneco(ref bonecoAviso);
        DestruirBoneco(ref bonecoRaio);

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[F7/Raio] Estado reiniciado. Armado preservado=" + armado +
            "; aguardando inicialização dos indicadores no mapa.");
    }

    private static void DestruirBoneco(ref PlayerControl boneco)
    {
        if (boneco == null) return;
        // O Despawn é enviado pela rede antes da destruição local, para não deixar
        // PlayerControls fantasmas nos clientes vanilla.
        try { CustomNetObject.Despawn(boneco); }
        catch
        {
            try
            {
                if (PlayerControl.AllPlayerControls.Contains(boneco))
                    PlayerControl.AllPlayerControls.Remove(boneco);
            }
            catch { }
            try { UnityEngine.Object.Destroy(boneco.gameObject); } catch { }
        }
        boneco = null;
    }
}

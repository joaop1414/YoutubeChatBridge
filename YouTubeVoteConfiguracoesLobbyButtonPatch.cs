using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Botão e painel de configurações próprios do YouTubeVoteBridge.
/// O painel é ancorado ao centro da câmera e redimensionado para ocupar
/// quase toda a área visível, independentemente da posição do botão Editar.
/// </summary>
[HarmonyPatch(typeof(GameStartManager), nameof(GameStartManager.Update))]
internal static class YouTubeVoteConfiguracoesLobbyButtonPatch
{
    private const string NomeBotao = "YouTubeVoteBridge_ConfigButton";
    private const float LarguraBasePainel = 10f;
    private const float AlturaBasePainel = 5.8f;

    private static GameObject botaoObjeto;
    private static GameObject painelObjeto;
    private static GameStartManager gerenteAssociado;
    private static Sprite sprite;
    private static PassiveButton botaoBase;
    private static readonly Dictionary<string, TextMeshPro> valores = new();
    private static bool painelAberto;
    private static bool erroRegistrado;

    // O botão é uma cópia do prefab real do lobby. O componente PassiveButton
    // permanece habilitado para preservar o sistema de entrada/collider do jogo;
    // este componente apenas encaminha o clique para a ação do mod.
    private sealed class CliqueIndependente : MonoBehaviour
    {
        public Action AoClicar;

        private void OnMouseUpAsButton()
        {
            if (isActiveAndEnabled && gameObject.activeInHierarchy)
                AoClicar?.Invoke();
        }
    }

    private sealed class ConfigItem
    {
        public string Id;
        public string Titulo;
        public Func<string> Ler;
        public Action<int> Alterar;
        public bool Binario;
    }

    public static void Postfix(GameStartManager __instance)
    {
        try
        {
            if (__instance == null) return;

            bool hostLobby = AmongUsClient.Instance != null &&
                AmongUsClient.Instance.AmHost &&
                !AmongUsClient.Instance.IsGameStarted &&
                __instance.EditButton != null;

            if (!hostLobby)
            {
                if (botaoObjeto != null) botaoObjeto.SetActive(false);
                if (painelObjeto != null) painelObjeto.SetActive(false);
                painelAberto = false;
                return;
            }

            if (botaoObjeto == null || gerenteAssociado != __instance)
                CriarBotao(__instance);

            if (painelObjeto == null)
                CriarPainel();

            if (botaoObjeto != null) botaoObjeto.SetActive(true);
            if (painelObjeto != null)
            {
                painelObjeto.SetActive(painelAberto);
                if (painelAberto)
                    AtualizarPosicaoETamanhoPainel();
            }

            erroRegistrado = false;
        }
        catch (Exception ex)
        {
            // Evita inundar o console com a mesma exceção em todos os frames.
            if (!erroRegistrado)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError("[Configurações Unity] " + ex);
                erroRegistrado = true;
            }
        }
    }

    private static void CriarBotao(GameStartManager gerente)
    {
        DestruirObjeto(ref botaoObjeto);
        DestruirObjeto(ref painelObjeto);
        valores.Clear();
        painelAberto = false;

        PassiveButton referencia = gerente.EditButton;
        if (referencia == null || referencia.transform == null) return;

        Transform pai = referencia.transform.parent;
        if (pai == null) return;

        InicializarBotaoBase(referencia);
        if (botaoBase == null)
            throw new InvalidOperationException("Não foi possível preparar o botão nativo do lobby.");

        botaoObjeto = CriarBotaoUnity(
            NomeBotao, pai,
            referencia.transform.localPosition + new Vector3(0f, -0.78f, 0f),
            referencia.transform.localScale * 0.85f,
            new Vector2(2.5f, 0.72f),
            "CONFIG. MOD",
            new Color(0.12f, 0.34f, 0.55f, 1f),
            AlternarPainel);

        gerenteAssociado = gerente;
    }

    private static void CriarPainel()
    {
        Camera camera = Camera.main;
        if (camera == null) return;

        painelObjeto = new GameObject("YouTubeVoteBridge_ConfigPanel");
        painelObjeto.layer = botaoObjeto != null
            ? botaoObjeto.layer
            : 0;

        // O painel fica diretamente na câmera: (0, 0) é sempre o centro da tela.
        painelObjeto.transform.SetParent(camera.transform, false);
        painelObjeto.transform.localPosition = new Vector3(0f, 0f, 10f);
        painelObjeto.transform.localRotation = Quaternion.identity;
        painelObjeto.transform.localScale = Vector3.one;

        SpriteRenderer fundo = painelObjeto.AddComponent<SpriteRenderer>();
        fundo.sprite = ObterSprite();
        fundo.color = new Color(0.035f, 0.05f, 0.085f, 0.985f);
        fundo.drawMode = SpriteDrawMode.Sliced;
        fundo.size = new Vector2(LarguraBasePainel, AlturaBasePainel);
        fundo.sortingOrder = 200;

        CriarTexto(painelObjeto.transform, "CONFIGURAÇÕES DO MOD",
            new Vector3(0f, 2.48f, -0.08f), new Vector2(8.8f, 0.42f),
            2.45f, Color.white, FontStyles.Bold, 201);

        CriarTexto(painelObjeto.transform, "Altere as opções da sala. Somente o anfitrião pode editar.",
            new Vector3(0f, 2.08f, -0.08f), new Vector2(8.8f, 0.3f),
            1.12f, new Color(0.72f, 0.82f, 0.92f), FontStyles.Normal, 201);

        List<ConfigItem> itens = ObterConfiguracoes();
        float y = 1.62f;
        const float espacamento = 0.405f;

        foreach (ConfigItem item in itens)
        {
            CriarTexto(painelObjeto.transform, item.Titulo,
                new Vector3(-1.45f, y, -0.08f), new Vector2(6.0f, 0.34f),
                1.16f, Color.white, FontStyles.Normal, 202);

            TextMeshPro valor = CriarTexto(painelObjeto.transform, item.Ler(),
                new Vector3(2.0f, y, -0.08f), new Vector2(1.55f, 0.34f),
                1.16f, new Color(0.45f, 0.9f, 1f), FontStyles.Bold, 202);
            valores[item.Id] = valor;

            if (item.Binario)
            {
                CriarBotaoUnity("Toggle_" + item.Id, painelObjeto.transform,
                    new Vector3(3.75f, y, -0.1f), Vector3.one,
                    new Vector2(1.35f, 0.34f), "ALTERAR",
                    new Color(0.12f, 0.38f, 0.52f, 1f),
                    () => { item.Alterar(1); AtualizarValores(); }, 203);
            }
            else
            {
                CriarBotaoUnity("Menos_" + item.Id, painelObjeto.transform,
                    new Vector3(3.35f, y, -0.1f), Vector3.one,
                    new Vector2(0.42f, 0.34f), "−",
                    new Color(0.35f, 0.18f, 0.22f, 1f),
                    () => { item.Alterar(-1); AtualizarValores(); }, 203);
                CriarBotaoUnity("Mais_" + item.Id, painelObjeto.transform,
                    new Vector3(3.88f, y, -0.1f), Vector3.one,
                    new Vector2(0.42f, 0.34f), "+",
                    new Color(0.12f, 0.38f, 0.3f, 1f),
                    () => { item.Alterar(1); AtualizarValores(); }, 203);
            }

            y -= espacamento;
        }

        CriarBotaoUnity("Fechar", painelObjeto.transform,
            new Vector3(0f, -2.55f, -0.1f), Vector3.one,
            new Vector2(2.2f, 0.42f), "FECHAR",
            new Color(0.42f, 0.16f, 0.2f, 1f),
            AlternarPainel, 203);

        AtualizarPosicaoETamanhoPainel();
        painelObjeto.SetActive(false);
    }

    private static void AtualizarPosicaoETamanhoPainel()
    {
        if (painelObjeto == null) return;

        Camera camera = Camera.main;
        if (camera == null || !camera.orthographic) return;

        float alturaVisivel = camera.orthographicSize * 2f;
        float larguraVisivel = alturaVisivel * camera.aspect;

        // Mantém margens pequenas e preserva a proporção do layout.
        float escala = Mathf.Min(
            (alturaVisivel * 0.92f) / AlturaBasePainel,
            (larguraVisivel * 0.92f) / LarguraBasePainel);

        escala = Mathf.Max(0.01f, escala);
        painelObjeto.transform.localPosition = new Vector3(0f, 0f, 10f);
        painelObjeto.transform.localScale = Vector3.one * escala;
    }

    private static List<ConfigItem> ObterConfiguracoes()
    {
        return new List<ConfigItem>
        {
            new ConfigItem { Id="TEMPO", Titulo="Tempo da votação", Ler=() => Valor(YouTubeVoteBridgePlugin.TempoVotacao, " s"), Alterar=d => { if (YouTubeVoteBridgePlugin.TempoVotacao != null) YouTubeVoteBridgePlugin.TempoVotacao.Value = Mathf.Clamp(YouTubeVoteBridgePlugin.TempoVotacao.Value + d * 5, 5, 600); } },
            new ConfigItem { Id="INTERVALO", Titulo="Intervalo entre rodadas", Ler=() => Valor(YouTubeVoteBridgePlugin.IntervaloRodadas, " s"), Alterar=d => { if (YouTubeVoteBridgePlugin.IntervaloRodadas != null) YouTubeVoteBridgePlugin.IntervaloRodadas.Value = Mathf.Clamp(YouTubeVoteBridgePlugin.IntervaloRodadas.Value + d * 5, 0, 600); } },
            new ConfigItem { Id="PREPARACAO", Titulo="Preparação do chat", Ler=() => Valor(YouTubeVoteBridgePlugin.PreparacaoChat, " s"), Alterar=d => { if (YouTubeVoteBridgePlugin.PreparacaoChat != null) YouTubeVoteBridgePlugin.PreparacaoChat.Value = Mathf.Clamp(YouTubeVoteBridgePlugin.PreparacaoChat.Value + d * 5, 0, 120); } },
            new ConfigItem { Id="SEMFIM", Titulo="Sem fim de jogo", Ler=() => Bool(YouTubeVoteBridgePlugin.SemFimDeJogo), Alterar=d => { if (YouTubeVoteBridgePlugin.SemFimDeJogo != null) YouTubeVoteBridgePlugin.SemFimDeJogo.Value = !YouTubeVoteBridgePlugin.SemFimDeJogo.Value; }, Binario=true },
            new ConfigItem { Id="JUIZ", Titulo="Pular fase de votos quando o juiz usa sua habilidade", Ler=() => Bool(YouTubeVoteBridgePlugin.PularFaseVotosJuiz), Alterar=d => { if (YouTubeVoteBridgePlugin.PularFaseVotosJuiz != null) YouTubeVoteBridgePlugin.PularFaseVotosJuiz.Value = !YouTubeVoteBridgePlugin.PularFaseVotosJuiz.Value; }, Binario=true },
            new ConfigItem { Id="MULTI", Titulo="Multitransmissão", Ler=() => Bool(YouTubeVoteBridgePlugin.MultiTransmissaoAtiva), Alterar=d => { if (YouTubeVoteBridgePlugin.MultiTransmissaoAtiva != null) YouTubeVoteBridgePlugin.MultiTransmissaoAtiva.Value = !YouTubeVoteBridgePlugin.MultiTransmissaoAtiva.Value; }, Binario=true },
            new ConfigItem { Id="IDS", Titulo="Permitir IDs de outros jogadores", Ler=() => Bool(YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores), Alterar=d => { if (YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores != null) YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores.Value = !YouTubeVoteBridgePlugin.PermitirIdsDeOutrosJogadores.Value; }, Binario=true },
            new ConfigItem { Id="MAPA", Titulo="Escolher mapa aleatório ao iniciar (Dleks: 20%)", Ler=() => Bool(YouTubeVoteBridgePlugin.TrocarMapaAoIniciar), Alterar=d => { if (YouTubeVoteBridgePlugin.TrocarMapaAoIniciar != null) YouTubeVoteBridgePlugin.TrocarMapaAoIniciar.Value = !YouTubeVoteBridgePlugin.TrocarMapaAoIniciar.Value; }, Binario=true },
            new ConfigItem { Id="VENTNPC", Titulo="Permitir que o NPC Assassino use ventilações", Ler=() => Bool(YouTubeVoteBridgePlugin.PermitirVentilacaoNPC), Alterar=d => { if (YouTubeVoteBridgePlugin.PermitirVentilacaoNPC != null) YouTubeVoteBridgePlugin.PermitirVentilacaoNPC.Value = !YouTubeVoteBridgePlugin.PermitirVentilacaoNPC.Value; }, Binario=true }
        };
    }

    private static string Valor<T>(BepInEx.Configuration.ConfigEntry<T> config, string sufixo)
    {
        return config == null ? "--" : Convert.ToString(config.Value) + sufixo;
    }

    private static string Bool(BepInEx.Configuration.ConfigEntry<bool> config)
        => config == null ? "--" : (config.Value ? "Ativado" : "Desativado");

    private static void AtualizarValores()
    {
        foreach (ConfigItem item in ObterConfiguracoes())
        {
            if (valores.TryGetValue(item.Id, out TextMeshPro texto) && texto != null)
                texto.text = item.Ler();
        }

        YouTubeVoteBridgePlugin.Instance?.Config.Save();
    }

    private static void AlternarPainel()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost ||
            AmongUsClient.Instance.IsGameStarted || painelObjeto == null)
            return;

        painelAberto = !painelAberto;
        painelObjeto.SetActive(painelAberto);

        if (painelAberto)
        {
            AtualizarPosicaoETamanhoPainel();
            AtualizarValores();
        }
    }

    private static void InicializarBotaoBase(PassiveButton referencia)
    {
        if (botaoBase != null || referencia == null)
            return;

        GameObject container = new GameObject("YouTubeVoteBridge_ButtonBaseContainer");
        UnityEngine.Object.DontDestroyOnLoad(container);

        botaoBase = UnityEngine.Object.Instantiate(referencia, container.transform);
        if (botaoBase == null)
        {
            UnityEngine.Object.Destroy(container);
            return;
        }

        botaoBase.name = "YouTubeVoteBridge_NativeButtonBase";
        botaoBase.gameObject.SetActive(false);

        // Igual ao SimpleButton do EHR: remove o posicionamento automático,
        // limpa os eventos do botão original e mantém o prefab nativo intacto.
        AspectPosition aspect = botaoBase.GetComponent<AspectPosition>();
        if (aspect != null)
            UnityEngine.Object.Destroy(aspect);

        botaoBase.OnClick = new();

        Transform labelTransform = botaoBase.transform.Find("FontPlacer/Text_TMP");
        if (labelTransform == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurações Unity] O botão nativo não possui FontPlacer/Text_TMP; não foi possível inicializar o modelo EHR.");
            UnityEngine.Object.Destroy(container);
            botaoBase = null;
            return;
        }

        TextMeshPro label = labelTransform.GetComponent<TextMeshPro>();
        if (label == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurações Unity] Text_TMP não contém TextMeshPro; modelo EHR não inicializado.");
            UnityEngine.Object.Destroy(container);
            botaoBase = null;
            return;
        }

        // O componente de tradução do botão Editar não deve sobrescrever
        // os rótulos definidos pelo mod nos botões clonados.
        Component translator = label.GetComponent("TextTranslatorTMP");
        if (translator != null)
            UnityEngine.Object.Destroy(translator);

        label.fontSize = label.fontSizeMin = label.fontSizeMax = 1.65f;
        label.enableWordWrapping = false;
        label.text = "YouTubeVoteBridge";
        label.alignment = TextAlignmentOptions.Center;

        BoxCollider2D collider = botaoBase.GetComponent<BoxCollider2D>();
        if (collider != null)
            collider.offset = Vector2.zero;

        UnityEngine.Object.DontDestroyOnLoad(botaoBase);
    }

    private static GameObject CriarBotaoUnity(
        string nome, Transform pai, Vector3 posicao, Vector3 escala,
        Vector2 tamanho, string titulo, Color cor, Action acao, int ordem = 70)
    {
        if (pai == null || acao == null || botaoBase == null)
            return null;

        // Reutiliza a hierarquia real do botão Editar: PassiveButton, collider,
        // estados nativos e FontPlacer/Text_TMP. Não cria PassiveButton vazio.
        PassiveButton botao = UnityEngine.Object.Instantiate(botaoBase, pai);
        if (botao == null)
            return null;

        botao.name = nome;
        botao.gameObject.SetActive(false);
        botao.transform.localPosition = posicao;
        botao.transform.localRotation = Quaternion.identity;
        botao.transform.localScale = escala;

        // Mantém PassiveButton habilitado, mas não herda a ação do Editar.
        botao.OnClick = new();

        Transform labelTransform = botao.transform.Find("FontPlacer/Text_TMP");
        TextMeshPro label = labelTransform != null
            ? labelTransform.GetComponent<TextMeshPro>()
            : null;

        if (label == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                $"[Configurações Unity] O botão '{nome}' não possui FontPlacer/Text_TMP.");
            UnityEngine.Object.Destroy(botao.gameObject);
            return null;
        }

        Component translator = label.GetComponent("TextTranslatorTMP");
        if (translator != null)
            UnityEngine.Object.Destroy(translator);

        Transform fontContainer = label.transform.parent;
        if (fontContainer != null)
        {
            Vector3 p = fontContainer.localPosition;
            fontContainer.localPosition = new Vector3(0f, p.y, p.z);
        }

        Vector3 labelPosition = label.transform.localPosition;
        label.transform.localPosition = new Vector3(0f, labelPosition.y, labelPosition.z);
        label.alignment = TextAlignmentOptions.Center;
        label.text = titulo;
        label.fontSize = label.fontSizeMin = label.fontSizeMax = 1.65f;
        label.enableWordWrapping = false;
        label.color = Color.white;

        // O PassiveButton nativo conserva o collider e a máscara de clique.
        BoxCollider2D collider = botao.GetComponent<BoxCollider2D>();
        if (collider == null)
            collider = botao.GetComponentInChildren<BoxCollider2D>(true);
        if (collider != null)
        {
            collider.offset = Vector2.zero;
            collider.size = tamanho;
            collider.enabled = true;
        }

        // Mantém a aparência criada pelo mod, mas usa os estados de entrada
        // do prefab nativo. Os sprites originais ficam ocultos.
        SpriteRenderer normalNativo = botao.inactiveSprites != null
            ? botao.inactiveSprites.GetComponent<SpriteRenderer>()
            : null;
        SpriteRenderer hoverNativo = botao.activeSprites != null
            ? botao.activeSprites.GetComponent<SpriteRenderer>()
            : null;

        if (normalNativo != null)
        {
            normalNativo.enabled = false;
            normalNativo.color = cor;
            normalNativo.size = tamanho;
        }
        if (hoverNativo != null)
        {
            hoverNativo.enabled = false;
            hoverNativo.color = Color.Lerp(cor, Color.white, 0.22f);
            hoverNativo.size = tamanho;
        }

        SpriteRenderer fundo = botao.GetComponent<SpriteRenderer>();
        if (fundo == null)
            fundo = botao.gameObject.AddComponent<SpriteRenderer>();

        fundo.sprite = ObterSprite();
        fundo.drawMode = SpriteDrawMode.Sliced;
        fundo.size = tamanho;
        fundo.color = cor;
        fundo.sortingOrder = ordem;

        MeshRenderer textoRenderer = label.GetComponent<MeshRenderer>();
        if (textoRenderer != null)
            textoRenderer.sortingOrder = ordem + 1;

        CliqueIndependente clique = botao.gameObject.AddComponent<CliqueIndependente>();
        clique.AoClicar = () =>
        {
            if (botao != null && botao.gameObject != null &&
                botao.gameObject.activeInHierarchy)
                acao();
        };

        botao.gameObject.SetActive(true);
        return botao.gameObject;
    }

    private static TextMeshPro CriarTexto(
        Transform pai, string conteudo, Vector3 posicao, Vector2 tamanho,
        float fonte, Color cor, FontStyles estilo, int ordem)
    {
        if (pai == null) return null;

        // Usa o Text_TMP do botão Editar como modelo. Ele já possui a fonte,
        // material e configuração de renderização carregados pelo Among Us.
        TextMeshPro modelo = null;
        if (botaoBase != null)
        {
            Transform modeloTransform = botaoBase.transform.Find("FontPlacer/Text_TMP");
            if (modeloTransform != null)
                modelo = modeloTransform.GetComponent<TextMeshPro>();
        }

        if (modelo == null)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                "[Configurações Unity] Não foi possível localizar o Text_TMP nativo para criar os textos do painel.");
            return null;
        }

        TextMeshPro texto = UnityEngine.Object.Instantiate(modelo, pai, false);
        if (texto == null) return null;

        GameObject go = texto.gameObject;
        go.name = "Texto_" + conteudo;
        go.layer = pai.gameObject.layer;
        go.transform.localPosition = posicao;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        Component translator = texto.GetComponent("TextTranslatorTMP");
        if (translator != null)
            UnityEngine.Object.Destroy(translator);

        texto.enabled = true;
        texto.text = conteudo;
        texto.alignment = TextAlignmentOptions.Center;
        texto.fontSize = fonte;
        texto.fontSizeMin = fonte;
        texto.fontSizeMax = fonte;
        texto.fontStyle = estilo;
        texto.color = cor;
        texto.enableWordWrapping = true;
        texto.overflowMode = TextOverflowModes.Overflow;
        texto.rectTransform.sizeDelta = tamanho;

        MeshRenderer mesh = texto.GetComponent<MeshRenderer>();
        if (mesh != null)
        {
            mesh.enabled = true;
            mesh.sortingOrder = ordem;
        }

        return texto;
    }

    private static Sprite ObterSprite()
    {
        if (sprite != null) return sprite;

        // Textura branca com cantos transparentes. O SpriteRenderer aplica
        // a cor de cada botão e o modo Sliced preserva o arredondamento ao
        // redimensionar tanto os botões quanto o painel.
        const int w = 128;
        const int h = 64;
        const int raio = 13;

        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.name = "YouTubeVoteBridge_RoundedButtonTexture";
        tex.filterMode = FilterMode.Bilinear;
        tex.wrapMode = TextureWrapMode.Clamp;

        Color[] pixels = new Color[w * h];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int cx = x < raio ? raio : (x >= w - raio ? w - raio - 1 : x);
                int cy = y < raio ? raio : (y >= h - raio ? h - raio - 1 : y);
                bool canto = (x < raio || x >= w - raio) &&
                    (y < raio || y >= h - raio);
                bool dentro = !canto ||
                    ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= raio * raio);

                pixels[y * w + x] = dentro ? Color.white : new Color(1f, 1f, 1f, 0f);
            }
        }

        tex.SetPixels(pixels);
        tex.Apply(false, true);

        sprite = Sprite.Create(
            tex,
            new Rect(0, 0, w, h),
            new Vector2(0.5f, 0.5f),
            64f,
            0,
            SpriteMeshType.FullRect,
            new Vector4(raio, raio, raio, raio));

        return sprite;
    }

    private static void DestruirObjeto(ref GameObject objeto)
    {
        if (objeto != null)
            UnityEngine.Object.Destroy(objeto);
        objeto = null;
    }
}

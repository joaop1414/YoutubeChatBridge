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
    private static readonly Dictionary<string, TextMeshPro> valores = new();
    private static bool painelAberto;
    private static bool erroRegistrado;

    // Base de botão preparada a partir de um PassiveButton nativo, como no EHR.
    // Nunca adicionamos PassiveButton vazio: clonamos um prefab já inicializado.
    private static PassiveButton botaoBase;
    private static GameObject paiBotaoBase;

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
        if (botaoBase == null) return;

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
        if (botaoBase != null || referencia == null) return;

        paiBotaoBase = new GameObject("YouTubeVoteBridge_ButtonBase");
        UnityEngine.Object.DontDestroyOnLoad(paiBotaoBase);

        botaoBase = UnityEngine.Object.Instantiate(referencia, paiBotaoBase.transform);
        botaoBase.gameObject.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(botaoBase);
        botaoBase.name = "YouTubeVoteBridge_ButtonBase_Prefab";

        // Remove o posicionamento automático herdado e todos os listeners
        // do botão original (por exemplo, abrir o menu Editar).
        AspectPosition aspect = botaoBase.GetComponent<AspectPosition>();
        if (aspect != null) UnityEngine.Object.Destroy(aspect);
        botaoBase.OnClick = new();

        Transform labelTransform = botaoBase.transform.Find("FontPlacer/Text_TMP");
        if (labelTransform != null)
        {
            TextMeshPro label = labelTransform.GetComponent<TextMeshPro>();
            if (label != null)
            {
                Transform container = label.transform.parent;
                if (container != null)
                {
                    Vector3 containerPosition = container.localPosition;
                    containerPosition.x = 0f;
                    container.localPosition = containerPosition;
                }

                Vector3 labelPosition = label.transform.localPosition;
                labelPosition.x = 0f;
                label.transform.localPosition = labelPosition;
                label.horizontalAlignment = HorizontalAlignmentOptions.Center;
                label.fontSize = label.fontSizeMin = label.fontSizeMax = 3.5f;
                label.enableWordWrapping = false;
                label.text = "YTVB BUTTON BASE";
            }
        }

        BoxCollider2D collider = botaoBase.GetComponent<BoxCollider2D>();
        if (collider != null) collider.offset = Vector2.zero;
    }

    private static GameObject CriarBotaoUnity(
        string nome, Transform pai, Vector3 posicao, Vector3 escala,
        Vector2 tamanho, string titulo, Color cor, Action acao, int ordem = 70)
    {
        if (pai == null || acao == null || botaoBase == null)
            return null;

        // Mesmo padrão do EHR: cada botão é uma cópia do PassiveButton-base,
        // com listener próprio, sprites nativos e collider já configurado.
        PassiveButton botao = UnityEngine.Object.Instantiate(botaoBase, pai);
        botao.name = nome;
        botao.gameObject.SetActive(true);
        botao.transform.localPosition = posicao;
        botao.transform.localRotation = Quaternion.identity;
        botao.transform.localScale = escala;
        botao.OnClick = new();
        // No IL2CPP, UnityAction é um wrapper e não aceita lambda diretamente.
        // Convertemos o System.Action gerenciado para o delegate nativo do Unity.
        Action aoClicar = () =>
        {
            if (botao != null && botao.gameObject != null &&
                botao.gameObject.activeInHierarchy)
                acao();
        };

        botao.OnClick.AddListener(
            Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction>(aoClicar));

        SpriteRenderer normal = botao.inactiveSprites != null
            ? botao.inactiveSprites.GetComponent<SpriteRenderer>()
            : null;
        SpriteRenderer hover = botao.activeSprites != null
            ? botao.activeSprites.GetComponent<SpriteRenderer>()
            : null;
        BoxCollider2D collider = botao.GetComponent<BoxCollider2D>();

        if (normal != null)
        {
            normal.color = cor;
            normal.size = tamanho;
            normal.sortingOrder = ordem;
        }

        if (hover != null)
        {
            hover.color = Color.Lerp(cor, Color.white, 0.22f);
            hover.size = tamanho;
            hover.sortingOrder = ordem;
        }

        if (collider != null)
        {
            collider.size = tamanho;
            collider.offset = Vector2.zero;
        }

        Transform labelTransform = botao.transform.Find("FontPlacer/Text_TMP");
        if (labelTransform != null)
        {
            TextMeshPro label = labelTransform.GetComponent<TextMeshPro>();
            if (label != null)
            {
                label.text = titulo;
                label.color = Color.white;
                label.alignment = TextAlignmentOptions.Center;
                label.horizontalAlignment = HorizontalAlignmentOptions.Center;
                label.fontSize = label.fontSizeMin = label.fontSizeMax = 1.65f;
                label.enableWordWrapping = true;
                label.overflowMode = TextOverflowModes.Overflow;

                MeshRenderer textRenderer = label.GetComponent<MeshRenderer>();
                if (textRenderer != null) textRenderer.sortingOrder = ordem + 1;
            }
        }

        return botao.gameObject;
    }

    private static TextMeshPro CriarTexto(
        Transform pai, string conteudo, Vector3 posicao, Vector2 tamanho,
        float fonte, Color cor, FontStyles estilo, int ordem)
    {
        if (pai == null) return null;

        // Usa um TextMeshPro já inicializado pelo próprio Among Us. Um TMP
        // criado vazio pode ficar sem FontAsset/material e resultar apenas no
        // quadrado de fundo visível, sem nenhum texto.
        TextMeshPro modelo = null;

        if (PlayerControl.LocalPlayer != null &&
            PlayerControl.LocalPlayer.cosmetics != null)
            modelo = PlayerControl.LocalPlayer.cosmetics.nameText;

        if (modelo == null && PlayerControl.AllPlayerControls != null)
        {
            foreach (PlayerControl jogador in PlayerControl.AllPlayerControls)
            {
                if (jogador?.cosmetics?.nameText != null)
                {
                    modelo = jogador.cosmetics.nameText;
                    break;
                }
            }
        }

        GameObject go;
        TextMeshPro texto;

        if (modelo != null)
        {
            texto = UnityEngine.Object.Instantiate(modelo, pai, false);
            go = texto.gameObject;
            go.name = "Texto_" + conteudo;
        }
        else
        {
            go = new GameObject("Texto_" + conteudo);
            go.transform.SetParent(pai, false);
            texto = go.AddComponent<TextMeshPro>();
        }

        go.layer = 0;
        go.transform.localPosition = posicao;
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale = Vector3.one;

        if (texto == null) return null;

        texto.enabled = true;
        texto.text = conteudo;
        texto.alignment = TextAlignmentOptions.Center;
        texto.fontSize = fonte;
        texto.fontStyle = estilo;
        texto.color = cor;
        texto.enableWordWrapping = true;
        texto.overflowMode = TextOverflowModes.Overflow;
        texto.rectTransform.sizeDelta = tamanho;

        MeshRenderer mesh = go.GetComponent<MeshRenderer>();
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

        const int w = 64, h = 64;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.name = "YouTubeVoteBridge_UnityUiTexture";
        tex.filterMode = FilterMode.Bilinear;

        Color[] pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = Color.white;

        tex.SetPixels(pixels);
        tex.Apply(false, true);
        sprite = Sprite.Create(tex, new Rect(0, 0, w, h),
            new Vector2(0.5f, 0.5f), 64f, 0,
            SpriteMeshType.FullRect);

        return sprite;
    }

    private static void DestruirObjeto(ref GameObject objeto)
    {
        if (objeto != null)
            UnityEngine.Object.Destroy(objeto);
        objeto = null;
    }
}

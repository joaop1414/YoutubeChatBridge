using UnityEngine;

namespace YouTubeVoteBridge;

internal static class FlashOverlay
{
    private const string NomeOverlay = "YouTubeVoteBridge_FlashOverlay";
    private const string NomeOverlayTsunami = "YouTubeVoteBridge_TsunamiFlashOverlay";

    public static void MostrarVermelho(float duracao = 0.9f) =>
        Mostrar(new Color(1f, 0f, 0f, 0.55f), duracao);

    public static void MostrarCiano(float duracao = 0.9f) =>
        Mostrar(new Color(0f, 0.9f, 1f, 0.62f), duracao);

    // Overlay separado do FullScreen original, que o jogo pode desativar durante a sabotagem.
    public static void DefinirFlashTsunami(bool visivel)
    {
        SpriteRenderer renderer = ObterRenderer(NomeOverlayTsunami);
        if (renderer == null) return;
        renderer.color = new Color(0f, 0.9f, 1f, 0.72f);
        renderer.enabled = visivel;
        renderer.gameObject.SetActive(visivel);
    }

    private static void Mostrar(Color cor, float duracao)
    {
        SpriteRenderer renderer = ObterRenderer(NomeOverlay);
        if (renderer == null) return;
        renderer.gameObject.SetActive(true);
        renderer.enabled = true;
        renderer.color = new Color(cor.r, cor.g, cor.b, 0f);
        HudManager hud = HudManager.Instance;
        hud.StartCoroutine(Effects.Lerp(duracao, (Il2CppSystem.Action<float>)(t =>
        {
            if (renderer == null) return;
            float envelope = Mathf.Clamp01(-2f * Mathf.Abs(t - 0.5f) + 1f);
            renderer.color = new Color(cor.r, cor.g, cor.b, envelope * cor.a);
            if (t >= 1f)
            {
                renderer.enabled = false;
                renderer.gameObject.SetActive(false);
            }
        })));
    }

    private static SpriteRenderer ObterRenderer(string nome)
    {
        if (!HudManager.InstanceExists || HudManager.Instance == null) return null;
        HudManager hud = HudManager.Instance;
        if (hud.FullScreen == null) return null;

        Transform existente = hud.transform.Find(nome);
        GameObject overlay = existente != null ? existente.gameObject : null;
        if (overlay == null)
        {
            overlay = UnityEngine.Object.Instantiate(hud.FullScreen.gameObject, hud.transform);
            overlay.name = nome;
            overlay.transform.localPosition = hud.FullScreen.transform.localPosition;
            overlay.transform.localRotation = hud.FullScreen.transform.localRotation;
            overlay.transform.localScale = hud.FullScreen.transform.localScale;
            overlay.transform.SetAsLastSibling();
        }

        SpriteRenderer renderer = overlay.GetComponent<SpriteRenderer>();
        if (renderer == null) return null;
        renderer.sprite = hud.FullScreen.sprite;
        renderer.sortingLayerID = hud.FullScreen.sortingLayerID;
        renderer.sortingOrder = hud.FullScreen.sortingOrder + 100;
        renderer.enabled = false;
        overlay.SetActive(false);
        return renderer;
    }
}

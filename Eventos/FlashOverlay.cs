using System;
using UnityEngine;

namespace YouTubeVoteBridge;

internal static class FlashOverlay
{
    public static void MostrarVermelho(float duracao = 0.9f) =>
        Mostrar(new Color(1f, 0f, 0f, 0.38f), duracao);

    public static void MostrarCiano(float duracao = 0.9f) =>
        Mostrar(new Color(0f, 0.9f, 1f, 0.42f), duracao);

    private static void Mostrar(Color cor, float duracao)
    {
        if (!HudManager.InstanceExists || HudManager.Instance == null) return;
        HudManager hud = HudManager.Instance;
        if (hud.FullScreen == null) return;

        GameObject overlay = hud.transform.Find("YouTubeVoteBridge_FlashOverlay")?.gameObject;
        if (overlay == null)
        {
            overlay = UnityEngine.Object.Instantiate(hud.FullScreen.gameObject, hud.transform);
            overlay.name = "YouTubeVoteBridge_FlashOverlay";
            overlay.transform.SetAsLastSibling();
        }

        SpriteRenderer renderizador = overlay.GetComponent<SpriteRenderer>();
        if (renderizador == null) return;
        overlay.SetActive(true);
#if IL2CPP
        hud.StartCoroutine(Effects.Lerp(duracao, (Il2CppSystem.Action<float>)(t =>
        {
            if (overlay == null || renderizador == null) return;
            float intensidade = Mathf.Clamp01((-2f * Mathf.Abs(t - 0.5f) + 1f) * cor.a / 2f);
            renderizador.color = new Color(cor.r, cor.g, cor.b, intensidade);
            overlay.SetActive(Math.Abs(t - 1f) > 0.1f);
        })));
#else
        hud.StartCoroutine(Effects.Lerp(duracao, t =>
        {
            if (overlay == null || renderizador == null) return;
            float intensidade = Mathf.Clamp01((-2f * Mathf.Abs(t - 0.5f) + 1f) * cor.a / 2f);
            renderizador.color = new Color(cor.r, cor.g, cor.b, intensidade);
            overlay.SetActive(Math.Abs(t - 1f) > 0.1f);
        }));
#endif
    }
}
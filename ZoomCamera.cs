using System;
using UnityEngine;

namespace YouTubeVoteBridge;

/// <summary>
/// Zoom local da câmera. É ativado/desativado pelo botão do controlador F8.
/// Mantém câmera principal e câmera da interface sincronizadas.
/// </summary>
internal static class ZoomCamera
{
    private const float TAMANHO_PADRAO = 3.0f;
    private const float TAMANHO_MAXIMO = 18.0f;
    private const float FATOR_ZOOM = 1.5f;

    private static Camera cameraPrincipal;
    private static Camera cameraInterface;
    private static bool ativo;
    // O estado do recurso é independente da existência temporária das câmeras.
    // Assim, se a HUD/câmera for recriada, o zoom continua ativo.
    private static float tamanhoZoomAtual = TAMANHO_PADRAO;

    public static bool Ativo => ativo;

    public static bool Alternar()
    {
        ativo = !ativo;

        if (ativo)
        {
            tamanhoZoomAtual = TAMANHO_PADRAO;
            cameraPrincipal = null;
            cameraInterface = null;
            SincronizarCameras();
        }
        else
        {
            Restaurar();
        }

        YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
            "[Zoom] " + (ativo ? "Ativado pelo controlador F8." : "Desativado pelo controlador F8."));

        return ativo;
    }

    public static void Desativar()
    {
        if (!ativo && cameraPrincipal == null)
            return;

        ativo = false;
        Restaurar();
    }

    public static void Atualizar()
    {
        try
        {
            if (!ativo)
                return;

            // Não desligamos o recurso quando uma reunião, transição ou HUD
            // substitui temporariamente as câmeras. Apenas aguardamos e reaplicamos.
            if (!SincronizarCameras())
                return;

            float scroll = Input.mouseScrollDelta.y;

            // Dois dedos: pinça para aproximar/afastar em dispositivos touch.
            if (Input.touchSupported && Input.touchCount == 2)
            {
                Touch primeiro = Input.GetTouch(0);
                Touch segundo = Input.GetTouch(1);

                Vector2 primeiroAnterior = primeiro.position - primeiro.deltaPosition;
                Vector2 segundoAnterior = segundo.position - segundo.deltaPosition;

                float distanciaAnterior = (primeiroAnterior - segundoAnterior).magnitude;
                float distanciaAtual = (primeiro.position - segundo.position).magnitude;
                float diferenca = distanciaAtual - distanciaAnterior;

                if (Mathf.Abs(diferenca) > 0.01f)
                    scroll = diferenca > 0f ? 1f : -1f;
            }

            if (scroll > 0f)
                AplicarZoom(false);
            else if (scroll < 0f)
                AplicarZoom(true);
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Zoom] Falha ao atualizar a câmera: " + ex.Message);
        }
    }

    private static void AplicarZoom(bool afastar)
    {
        if (cameraPrincipal == null || !HudManager.InstanceExists ||
            HudManager.Instance == null || HudManager.Instance.UICamera == null)
            return;

        float fator = afastar ? FATOR_ZOOM : 1f / FATOR_ZOOM;
        tamanhoZoomAtual = Mathf.Clamp(
            tamanhoZoomAtual * fator, TAMANHO_PADRAO, TAMANHO_MAXIMO);

        SincronizarCameras();
        AtualizarSombra();
    }

    // Retorna false somente enquanto uma das câmeras ainda não existe.
    // Quando elas são recriadas, reaplica o último nível de zoom armazenado.
    private static bool SincronizarCameras()
    {
        if (!ativo)
            return false;

        Camera principalAtual = Camera.main;
        if (principalAtual != null && principalAtual != cameraPrincipal)
            cameraPrincipal = principalAtual;

        Camera interfaceAtual = HudManager.InstanceExists &&
            HudManager.Instance != null
                ? HudManager.Instance.UICamera
                : null;

        if (interfaceAtual != null && interfaceAtual != cameraInterface)
            cameraInterface = interfaceAtual;

        if (cameraPrincipal == null || cameraInterface == null)
            return false;

        cameraPrincipal.orthographicSize = tamanhoZoomAtual;
        cameraInterface.orthographicSize = tamanhoZoomAtual;
        return true;
    }

    private static void Restaurar()
    {
        try
        {
            if (cameraPrincipal == null)
                cameraPrincipal = Camera.main;

            if (cameraPrincipal != null)
                cameraPrincipal.orthographicSize = TAMANHO_PADRAO;

            if (HudManager.InstanceExists && HudManager.Instance != null)
            {
                if (HudManager.Instance.UICamera != null)
                    HudManager.Instance.UICamera.orthographicSize = TAMANHO_PADRAO;

                if (HudManager.Instance.Chat != null)
                    HudManager.Instance.Chat.transform.localScale = Vector3.one;

                if (HudManager.Instance.ShadowQuad != null)
                    HudManager.Instance.ShadowQuad.gameObject.SetActive(
                        PlayerControl.LocalPlayer != null &&
                        PlayerControl.LocalPlayer.Data != null &&
                        !PlayerControl.LocalPlayer.Data.IsDead);
            }

            tamanhoZoomAtual = TAMANHO_PADRAO;
            cameraInterface = null;
        }
        catch (Exception ex)
        {
            YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                "[Zoom] Falha ao restaurar a câmera: " + ex.Message);
        }
    }

    private static void AtualizarSombra()
    {
        if (!HudManager.InstanceExists || HudManager.Instance == null ||
            HudManager.Instance.ShadowQuad == null)
            return;

        bool tamanhoNormal = cameraPrincipal != null &&
            Mathf.Approximately(cameraPrincipal.orthographicSize, TAMANHO_PADRAO);

        bool jogadorVivo = PlayerControl.LocalPlayer != null &&
            PlayerControl.LocalPlayer.Data != null &&
            !PlayerControl.LocalPlayer.Data.IsDead;

        HudManager.Instance.ShadowQuad.gameObject.SetActive(tamanhoNormal && jogadorVivo);
    }
}

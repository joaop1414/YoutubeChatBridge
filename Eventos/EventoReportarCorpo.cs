using System;
using System.Collections.Generic;
using UnityEngine;

namespace YouTubeVoteBridge;

internal sealed class EventoReportarCorpo : EventoVotacao
{
    public override string Nome => "Reportar um corpo (desativado por segurança)";

    // O host nunca deve reportar corpos nem abrir reuniões por este evento.
    // A chamada é intencionalmente um no-op para evitar o bug que travava o host.
    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost())
            return;

        YouTubeVoteBridgePlugin.Instance?.Log.LogDebug(
            "Evento de reporte ignorado: reportes desativados no host por segurança.");
    }
}

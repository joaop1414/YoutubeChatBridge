namespace YouTubeVoteBridge;

internal sealed class EventoPersonalizado : EventoVotacao
{
    public override string Nome => "Evento personalizado: teleporte + controles invertidos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost()) return;
        new EventoTeleporteAleatorio().Executar();
        new EventoInverterControles().Executar();
    }
}
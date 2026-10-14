using YouTubeVoteBridge;

namespace YouTubeVoteBridge;

internal sealed class EventoInverterControles : EventoVotacao
{
    private const float DuracaoSegundos = 15f;
    public override string Nome => "Inverter os controles por 15 segundos";

    public void Executar()
    {
        if (!YouTubeVoteBridgePlugin.EhHost() || PlayerControl.LocalPlayer == null)
            return;

        // Reutiliza o sistema temporário de velocidade: multiplicador -1
        // faz Speed virar Speed * (-1) e o helper restaura o valor original.
        EventoVelocidadeJogadores.AplicarMultiplicador(
            PlayerControl.LocalPlayer.PlayerId, -1f, DuracaoSegundos);
    }
}
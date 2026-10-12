namespace YouTubeVoteBridge;

internal sealed class EventoSupervelocidade : EventoVotacao
{
    public override string Nome => "Supervelocidade temporária";
    public void Executar() => EventoVelocidadeTemporaria.Supervelocidade();
}

namespace YouTubeVoteBridge;

internal sealed class EventoVelocidadeReduzida : EventoVotacao
{
    public override string Nome => "Velocidade reduzida temporária";
    public void Executar() => EventoVelocidadeTemporaria.VelocidadeReduzida();
}

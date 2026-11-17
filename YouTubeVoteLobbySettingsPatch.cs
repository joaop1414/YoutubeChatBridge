// As configurações do YouTubeVoteBridge agora são exibidas em um painel Unity
// próprio, criado por YouTubeVoteConfiguracoesLobbyButtonPatch.
// Este arquivo mantém apenas o ponto de compatibilidade para chamadas antigas.
namespace YouTubeVoteBridge
{
    internal static class YouTubeVoteLobbySettingsPatch
    {
        internal static bool EhControle(StringOption option) => false;
        internal static bool EhCabecalho(StringOption option) => false;
        internal static void Alterar(StringOption option, int delta) { }
        internal static void ReaplicarControles(GameOptionsMenu menu) { }
    }
}

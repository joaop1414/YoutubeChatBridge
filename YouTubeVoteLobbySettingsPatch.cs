using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

// Primeira tentativa de controles visuais para o menu de configurações do lobby.
// Os controles são criados a partir do prefab StringOption do próprio jogo.
namespace YouTubeVoteBridge
{
    [HarmonyPatch(typeof(GameOptionsMenu), nameof(GameOptionsMenu.Initialize))]
    public static class YouTubeVoteLobbySettingsPatch
    {
        private const string PREFIX = "YTVB_LOBBY_";
        private static GameOptionsMenu menuAtual;

        public static void Postfix(GameOptionsMenu __instance)
        {
            try
            {
                if (__instance == null || __instance.Children == null)
                    return;

                menuAtual = __instance;

                // Recupera os controles existentes se o jogo recriou a lista Children.
                var existentes = __instance.GetComponentsInChildren<StringOption>(true)
                    .Where(x => x != null && x.name != null && x.name.StartsWith(PREFIX))
                    .ToArray();

                if (existentes.Length > 0)
                {
                    foreach (var existente in existentes)
                    {
                        bool jaNaLista = false;
                        foreach (var filho in __instance.Children)
                        {
                            if (filho == existente)
                            {
                                jaNaLista = true;
                                break;
                            }
                        }
                        if (!jaNaLista)
                            __instance.Children.Add(existente);
                    }
                    AtualizarControles(__instance);
                    return;
                }

                if (!YouTubeVoteBridgePlugin.EhHost())
                    return;

                StringOption modelo = null;
                foreach (OptionBehaviour item in __instance.Children)
                {
                    StringOption candidato = item as StringOption;
                    if (candidato != null && candidato.gameObject != null)
                    {
                        modelo = candidato;
                        break;
                    }
                }

                if (modelo == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "Não foi possível criar as configurações visuais: StringOption de referência não encontrado.");
                    return;
                }

                float y = -1.5f;
                foreach (var child in __instance.Children)
                {
                    if (child != null && child.gameObject != null && child.gameObject.activeSelf)
                        y = Math.Min(y, child.transform.localPosition.y - 0.48f);
                }

                CriarControle(__instance, modelo, "Tempo da votação (segundos)", PREFIX + "TEMPO", y);
                y -= 0.48f;
                CriarControle(__instance, modelo, "Intervalo entre rodadas (segundos)", PREFIX + "INTERVALO", y);
                y -= 0.48f;
                CriarControle(__instance, modelo, "Preparação do chat (segundos)", PREFIX + "PREPARACAO", y);

                AtualizarControles(__instance);
                __instance.scrollBar.SetYBoundsMax(Math.Max(6f, -y + 2f));
                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Controles visuais do lobby adicionados ao menu de configurações.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao criar configurações visuais do lobby: " + ex);
            }
        }

        private static void CriarControle(
            GameOptionsMenu menu, StringOption modelo, string titulo, string nome, float y)
        {
            var controle = UnityEngine.Object.Instantiate(
                modelo, modelo.transform.parent);

            controle.name = nome;
            controle.transform.localPosition = new Vector3(
                modelo.transform.localPosition.x, y, modelo.transform.localPosition.z);
            controle.SetClickMask(menu.ButtonClickMask);
            controle.SetUpFromData(modelo.data, GameOptionsMenu.MASK_LAYER);
            controle.TitleText.text = titulo;
            controle.OnValueChanged = new Action<OptionBehaviour>(_ => { });
            controle.Value = 0;
            controle.oldValue = 0;
            controle.ValueText.text = "0 s";
            menu.Children.Add(controle);
        }

        internal static bool EhControle(StringOption option)
        {
            return option != null && option.name != null && option.name.StartsWith(PREFIX);
        }

        internal static void Alterar(StringOption option, int delta)
        {
            if (!YouTubeVoteBridgePlugin.EhHost())
                return;

            if (option.name == PREFIX + "TEMPO")
                YouTubeVoteBridgePlugin.TempoVotacao.Value =
                    Mathf.Clamp(YouTubeVoteBridgePlugin.TempoVotacao.Value + delta * 5, 5, 300);
            else if (option.name == PREFIX + "INTERVALO")
                YouTubeVoteBridgePlugin.IntervaloRodadas.Value =
                    Mathf.Clamp(YouTubeVoteBridgePlugin.IntervaloRodadas.Value + delta * 5, 0, 300);
            else if (option.name == PREFIX + "PREPARACAO")
                YouTubeVoteBridgePlugin.PreparacaoChat.Value =
                    Mathf.Clamp(YouTubeVoteBridgePlugin.PreparacaoChat.Value + delta * 5, 0, 120);

            AtualizarUmControle(option);
        }

        private static void AtualizarControles(GameOptionsMenu menu)
        {
            if (menu == null || menu.Children == null)
                return;

            bool host = YouTubeVoteBridgePlugin.EhHost();
            foreach (OptionBehaviour item in menu.Children)
            {
                StringOption option = item as StringOption;
                if (!EhControle(option))
                    continue;

                option.gameObject.SetActive(host);
                AtualizarUmControle(option);
            }
        }

        private static void AtualizarUmControle(StringOption option)
        {
            if (option == null || option.gameObject == null)
                return;

            int valor;
            string unidade = " s";
            if (option.name == PREFIX + "TEMPO")
                valor = YouTubeVoteBridgePlugin.TempoVotacao.Value;
            else if (option.name == PREFIX + "INTERVALO")
                valor = YouTubeVoteBridgePlugin.IntervaloRodadas.Value;
            else
                valor = YouTubeVoteBridgePlugin.PreparacaoChat.Value;

            option.Value = valor;
            option.oldValue = valor;
            option.ValueText.text = valor + unidade;
        }
    }

    [HarmonyPatch(typeof(StringOption), nameof(StringOption.Increase))]
    public static class YouTubeVoteLobbySettingsIncreasePatch
    {
        public static bool Prefix(StringOption __instance)
        {
            if (!YouTubeVoteLobbySettingsPatch.EhControle(__instance))
                return true;

            YouTubeVoteLobbySettingsPatch.Alterar(__instance, 1);
            return false;
        }
    }

    [HarmonyPatch(typeof(StringOption), nameof(StringOption.Decrease))]
    public static class YouTubeVoteLobbySettingsDecreasePatch
    {
        public static bool Prefix(StringOption __instance)
        {
            if (!YouTubeVoteLobbySettingsPatch.EhControle(__instance))
                return true;

            YouTubeVoteLobbySettingsPatch.Alterar(__instance, -1);
            return false;
        }
    }
}

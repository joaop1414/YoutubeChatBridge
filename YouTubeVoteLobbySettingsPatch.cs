using System;
using System.Linq;
using HarmonyLib;
using UnityEngine;

// Adiciona os controles do YouTubeVoteBridge diretamente ao painel nativo "Jogo".
// Não cria uma aba separada nem altera a navegação do menu.
namespace YouTubeVoteBridge
{
    [HarmonyPatch(typeof(GameOptionsMenu), nameof(GameOptionsMenu.Initialize))]
    public static class YouTubeVoteLobbySettingsPatch
    {
        private const string PREFIX = "YTVB_LOBBY_";
        private const string CABECALHO = PREFIX + "CABECALHO";
        private static GameOptionsMenu menuAtual;

        public static void Postfix(GameOptionsMenu __instance)
        {
            try
            {
                GameSettingMenu settingMenu = GameSettingMenu.Instance;
                if (__instance == null || __instance.Children == null ||
                    settingMenu == null || __instance != settingMenu.GameSettingsTab)
                    return;

                menuAtual = __instance;

                var existentes = __instance.GetComponentsInChildren<StringOption>(true)
                    .Where(x => x != null && x.name != null && x.name.StartsWith(PREFIX))
                    .ToArray();

                if (existentes.Length > 0)
                {
                    foreach (var existente in existentes)
                    {
                        bool encontrado = __instance.Children.Any(filho => filho == existente);
                        if (!encontrado)
                            __instance.Children.Add(existente);
                    }
                    AtualizarControles(__instance);
                    return;
                }

                // A configuração só deve ser apresentada no lobby ao host.
                if (!YouTubeVoteBridgePlugin.EhHost())
                    return;

                StringOption modelo = __instance.GetComponentsInChildren<StringOption>(true)
                    .FirstOrDefault(x => x != null && x.gameObject != null &&
                        x.name != null && !x.name.StartsWith(PREFIX));

                if (modelo == null)
                {
                    YouTubeVoteBridgePlugin.Instance?.Log.LogWarning(
                        "YouTubeVoteBridge: não foi encontrado um StringOption para clonar.");
                    return;
                }

                float y = -1.5f;
                foreach (var child in __instance.Children)
                {
                    if (child != null && child.gameObject != null && child.gameObject.activeSelf)
                        y = Math.Min(y, child.transform.localPosition.y - 0.52f);
                }

                // Linha de título da categoria dentro do painel Jogo.
                CriarControle(__instance, modelo, "YouTubeVoteBridge", CABECALHO, y, true);
                y -= 0.52f;

                CriarControle(__instance, modelo, "Tempo da votação", PREFIX + "TEMPO", y);
                y -= 0.52f;
                CriarControle(__instance, modelo, "Intervalo entre rodadas", PREFIX + "INTERVALO", y);
                y -= 0.52f;
                CriarControle(__instance, modelo, "Preparação do chat", PREFIX + "PREPARACAO", y);

                AtualizarControles(__instance);
                if (__instance.scrollBar != null)
                    __instance.scrollBar.SetYBoundsMax(Math.Max(6f, -y + 2f));

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Configurações do YouTubeVoteBridge adicionadas ao painel Jogo.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao adicionar configurações ao painel Jogo: " + ex);
            }
        }

        private static void CriarControle(
            GameOptionsMenu menu, StringOption modelo, string titulo, string nome, float y,
            bool cabecalho = false)
        {
            StringOption controle = UnityEngine.Object.Instantiate(modelo, menu.settingsContainer);
            controle.name = nome;
            controle.transform.localPosition = new Vector3(
                modelo.transform.localPosition.x, y, modelo.transform.localPosition.z);
            controle.SetClickMask(menu.ButtonClickMask);
            controle.SetUpFromData(modelo.data, GameOptionsMenu.MASK_LAYER);
            controle.TitleText.text = titulo;
            controle.OnValueChanged = new Action<OptionBehaviour>(_ => { });
            controle.Value = 0;
            controle.oldValue = 0;
            controle.ValueText.text = cabecalho ? "" : "0 s";

            // O cabeçalho é apenas visual; desativa o texto de valor e não permite ajuste.
            if (cabecalho)
                controle.ValueText.gameObject.SetActive(false);

            menu.Children.Add(controle);
        }

        internal static bool EhControle(StringOption option)
        {
            return option != null && option.name != null &&
                option.name.StartsWith(PREFIX) && option.name != CABECALHO;
        }

        internal static bool EhCabecalho(StringOption option)
        {
            return option != null && option.name == CABECALHO;
        }

        internal static void Alterar(StringOption option, int delta)
        {
            if (option == null || !EhControle(option) || delta == 0 ||
                !YouTubeVoteBridgePlugin.EhHost() ||
                YouTubeVoteBridgePlugin.TempoVotacao == null ||
                YouTubeVoteBridgePlugin.IntervaloRodadas == null ||
                YouTubeVoteBridgePlugin.PreparacaoChat == null)
                return;

            if (option.name == PREFIX + "TEMPO")
                YouTubeVoteBridgePlugin.TempoVotacao.Value = Mathf.Clamp(
                    YouTubeVoteBridgePlugin.TempoVotacao.Value + delta * 5, 5, 600);
            else if (option.name == PREFIX + "INTERVALO")
                YouTubeVoteBridgePlugin.IntervaloRodadas.Value = Mathf.Clamp(
                    YouTubeVoteBridgePlugin.IntervaloRodadas.Value + delta * 5, 0, 600);
            else if (option.name == PREFIX + "PREPARACAO")
                YouTubeVoteBridgePlugin.PreparacaoChat.Value = Mathf.Clamp(
                    YouTubeVoteBridgePlugin.PreparacaoChat.Value + delta * 5, 0, 120);
            else
                return;

            YouTubeVoteBridgePlugin.Instance?.Config.Save();
            AtualizarUmControle(option);
        }

        internal static void ReaplicarControles(GameOptionsMenu menu)
        {
            if (menu == null || menu != GameSettingMenu.Instance?.GameSettingsTab)
                return;
            AtualizarControles(menu);
        }

        private static void AtualizarControles(GameOptionsMenu menu)
        {
            if (menu == null || menu.Children == null)
                return;

            bool host = YouTubeVoteBridgePlugin.EhHost();
            foreach (OptionBehaviour item in menu.Children)
            {
                StringOption option = item as StringOption;
                if (EhCabecalho(option))
                {
                    option.gameObject.SetActive(host);
                    option.TitleText.text = "YouTubeVoteBridge";
                    if (option.ValueText != null)
                        option.ValueText.gameObject.SetActive(false);
                    continue;
                }

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
            if (option.name == PREFIX + "TEMPO")
            {
                valor = YouTubeVoteBridgePlugin.TempoVotacao.Value;
                option.TitleText.text = "Tempo da votação";
            }
            else if (option.name == PREFIX + "INTERVALO")
            {
                valor = YouTubeVoteBridgePlugin.IntervaloRodadas.Value;
                option.TitleText.text = "Intervalo entre rodadas";
            }
            else
            {
                valor = YouTubeVoteBridgePlugin.PreparacaoChat.Value;
                option.TitleText.text = "Preparação do chat";
            }

            option.Value = valor;
            option.oldValue = valor;
            option.ValueText.text = valor + " s";
        }
    }

    [HarmonyPatch(typeof(StringOption), nameof(StringOption.Increase))]
    public static class YouTubeVoteLobbySettingsIncreasePatch
    {
        public static bool Prefix(StringOption __instance)
        {
            if (YouTubeVoteLobbySettingsPatch.EhCabecalho(__instance))
                return false;
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
            if (YouTubeVoteLobbySettingsPatch.EhCabecalho(__instance))
                return false;
            if (!YouTubeVoteLobbySettingsPatch.EhControle(__instance))
                return true;

            YouTubeVoteLobbySettingsPatch.Alterar(__instance, -1);
            return false;
        }
    }

    [HarmonyPatch(typeof(GameOptionsMenu), nameof(GameOptionsMenu.Update))]
    public static class YouTubeVoteLobbySettingsUpdatePatch
    {
        public static void Postfix(GameOptionsMenu __instance)
        {
            YouTubeVoteLobbySettingsPatch.ReaplicarControles(__instance);
        }
    }
}

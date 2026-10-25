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
        private static CategoryHeaderMasked cabecalhoAtual;

        public static void Postfix(GameOptionsMenu __instance)
        {
            try
            {
                GameSettingMenu settingMenu = GameSettingMenu.Instance;
                if (__instance == null || __instance.Children == null ||
                    settingMenu == null || __instance != settingMenu.GameSettingsTab)
                    return;

                var headerExistente = __instance.GetComponentsInChildren<CategoryHeaderMasked>(true)
                    .FirstOrDefault(x => x != null && x.name == CABECALHO);
                if (headerExistente != null)
                    cabecalhoAtual = headerExistente;

                var existentes = __instance.GetComponentsInChildren<StringOption>(true)
                    .Where(x => x != null && x.name != null && x.name.StartsWith(PREFIX))
                    .ToArray();

                if (existentes.Length > 0)
                {
                    foreach (var existente in existentes)
                    {
                        bool encontrado = false;
                        foreach (OptionBehaviour filho in __instance.Children)
                        {
                            if (filho == existente)
                            {
                                encontrado = true;
                                break;
                            }
                        }
                        if (!encontrado)
                            __instance.Children.Add(existente);
                    }

                    // Atualiza menus já criados em versões anteriores do mod,
                    // adicionando o novo controle sem exigir reiniciar o jogo.
                    if (YouTubeVoteBridgePlugin.EhHost())
                    {
                        StringOption modeloExistente = __instance.GetComponentsInChildren<StringOption>(true)
                            .FirstOrDefault(x => x != null && x.gameObject != null &&
                                x.name != null && !x.name.StartsWith(PREFIX));
                        if (modeloExistente != null)
                        {
                            float yExistente = existentes
                                .Where(x => x != null && x.gameObject != null)
                                .Select(x => x.transform.localPosition.y)
                                .DefaultIfEmpty(-2f).Min() - 0.52f;

                            if (!existentes.Any(x => x.name == PREFIX + "SEMFIM"))
                                CriarControle(__instance, modeloExistente, "Sem fim de jogo", PREFIX + "SEMFIM", yExistente);

                            if (!existentes.Any(x => x.name == PREFIX + "SEMFIM"))
                                CriarControle(__instance, modeloExistente, "Sem fim de jogo", PREFIX + "SEMFIM", yExistente);
                            if (!existentes.Any(x => x.name == PREFIX + "JUIZ"))
                                CriarControle(__instance, modeloExistente,
                                    "Pular fase de votos quando o juiz usa sua habilidade",
                                    PREFIX + "JUIZ", yExistente - 0.52f);

                            if (__instance.scrollBar != null)
                                __instance.scrollBar.SetYBoundsMax(Math.Max(6f, -yExistente + 2.6f));
                        }
                    }

                    GarantirCabecalho(__instance, cabecalhoAtual);
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

                // Usa o componente nativo de cabeçalho de categoria do Among Us.
                GarantirCabecalho(__instance, null);
                if (cabecalhoAtual != null)
                    cabecalhoAtual.transform.localPosition = new Vector3(-0.903f, y, -2f);
                y -= 0.72f;

                CriarControle(__instance, modelo, "Tempo da votação", PREFIX + "TEMPO", y);
                y -= 0.52f;
                CriarControle(__instance, modelo, "Intervalo entre rodadas", PREFIX + "INTERVALO", y);
                y -= 0.52f;
                CriarControle(__instance, modelo, "Preparação do chat", PREFIX + "PREPARACAO", y);
                y -= 0.52f;
                CriarControle(__instance, modelo, "Sem fim de jogo", PREFIX + "SEMFIM", y);
                y -= 0.52f;
                CriarControle(__instance, modelo,
                    "Pular fase de votos quando o juiz usa sua habilidade",
                    PREFIX + "JUIZ", y);
                y -= 0.52f;

                AtualizarControles(__instance);
                if (__instance.scrollBar != null)
                    __instance.scrollBar.SetYBoundsMax(Math.Max(6f, -y + 2f));

                YouTubeVoteBridgePlugin.Instance?.Log.LogInfo(
                    "Categoria e configurações do YouTubeVoteBridge adicionadas ao painel Jogo.");
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao adicionar configurações ao painel Jogo: " + ex);
            }
        }

        private static void CriarControle(
            GameOptionsMenu menu, StringOption modelo, string titulo, string nome, float y)
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
            controle.ValueText.text = "0 s";

            menu.Children.Add(controle);
        }

        private static void GarantirCabecalho(GameOptionsMenu menu, CategoryHeaderMasked existente)
        {
            if (menu == null)
                return;

            if (existente != null)
                cabecalhoAtual = existente;

            if (cabecalhoAtual == null)
            {
                cabecalhoAtual = UnityEngine.Object.Instantiate(
                    menu.categoryHeaderOrigin, menu.settingsContainer);
                cabecalhoAtual.name = CABECALHO;
                cabecalhoAtual.SetHeader(StringNames.Name, GameOptionsMenu.MASK_LAYER);
                cabecalhoAtual.Title.text = "YouTubeVoteBridge";
                cabecalhoAtual.transform.localScale = Vector3.one * 0.63f;
            }

            cabecalhoAtual.Title.text = "YouTubeVoteBridge";
            cabecalhoAtual.gameObject.SetActive(YouTubeVoteBridgePlugin.EhHost());
        }

        internal static bool EhControle(StringOption option)
        {
            return option != null && option.name != null && option.name.StartsWith(PREFIX);
        }

        internal static bool EhCabecalho(StringOption option) => false;

        internal static void Alterar(StringOption option, int delta)
        {
            if (option == null || !EhControle(option) || delta == 0 ||
                !YouTubeVoteBridgePlugin.EhHost() ||
                YouTubeVoteBridgePlugin.TempoVotacao == null ||
                YouTubeVoteBridgePlugin.IntervaloRodadas == null ||
                YouTubeVoteBridgePlugin.PreparacaoChat == null ||
                YouTubeVoteBridgePlugin.PularFaseVotosJuiz == null ||
                YouTubeVoteBridgePlugin.SemFimDeJogo == null)
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
            else if (option.name == PREFIX + "JUIZ")
                YouTubeVoteBridgePlugin.PularFaseVotosJuiz.Value = delta > 0;
            else if (option.name == PREFIX + "SEMFIM")
                YouTubeVoteBridgePlugin.SemFimDeJogo.Value = delta > 0;
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
            if (cabecalhoAtual != null)
                cabecalhoAtual.gameObject.SetActive(host);
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
            else if (option.name == PREFIX + "PREPARACAO")
            {
                valor = YouTubeVoteBridgePlugin.PreparacaoChat.Value;
                option.TitleText.text = "Preparação do chat";
                option.Value = valor;
                option.oldValue = valor;
                option.ValueText.text = valor + " s";
                return;
            }
            else if (option.name == PREFIX + "JUIZ")
            {
                bool ativado = YouTubeVoteBridgePlugin.PularFaseVotosJuiz != null &&
                    YouTubeVoteBridgePlugin.PularFaseVotosJuiz.Value;
                option.TitleText.text = "Pular fase de votos quando o juiz usa sua habilidade";
                option.Value = ativado ? 1 : 0;
                option.oldValue = ativado ? 1 : 0;
                option.ValueText.text = ativado ? "Ativado" : "Desativado";
                return;
            }
            else if (option.name == PREFIX + "SEMFIM")
            {
                bool ativado = YouTubeVoteBridgePlugin.SemFimDeJogo != null &&
                    YouTubeVoteBridgePlugin.SemFimDeJogo.Value;
                option.TitleText.text = "Sem fim de jogo";
                option.Value = ativado ? 1 : 0;
                option.oldValue = ativado ? 1 : 0;
                option.ValueText.text = ativado ? "Ativado" : "Desativado";
                return;
            }
            else
            {
                option.gameObject.SetActive(false);
                return;
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

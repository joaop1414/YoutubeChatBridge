using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using UnityEngine.Events;
using TMPro;
using UnityEngine;

namespace YouTubeVoteBridge
{
    // Cria uma aba própria usando somente os prefabs e APIs nativos do Among Us.
    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.OnEnable))]
    internal static class YouTubeVoteSettingsTabCreatePatch
    {
        internal static GameOptionsMenu MenuMod;
        internal static PassiveButton BotaoMod;

        private static GameSettingMenu menuDono;

        [HarmonyPostfix]
        private static void Postfix(GameSettingMenu __instance)
        {
            try
            {
                if (__instance == null || __instance.GameSettingsButton == null ||
                    __instance.GameSettingsTab == null)
                    return;

                if (menuDono != __instance || MenuMod == null || BotaoMod == null)
                {
                    menuDono = __instance;

                    MenuMod = UnityEngine.Object.Instantiate(
                        __instance.GameSettingsTab,
                        __instance.GameSettingsTab.transform.parent);
                    MenuMod.name = "tab_YouTubeVoteBridge";
                    MenuMod.gameObject.SetActive(false);

                    BotaoMod = UnityEngine.Object.Instantiate(
                        __instance.GameSettingsButton,
                        __instance.GameSettingsButton.transform.parent);
                    BotaoMod.name = "Button_YouTubeVoteBridge";

                    TextMeshPro label = BotaoMod.GetComponentInChildren<TextMeshPro>(true);
                    if (label != null)
                        label.text = "YouTubeVoteBridge";

                    BotaoMod.transform.localPosition = new Vector3(-2.4f, -0.95f, 0f);
                    BotaoMod.transform.localScale = new Vector3(0.45f, 0.35f, 1f);

                    // Usa o evento OnClick que já vem no prefab original do jogo.
                    // Não substitua o evento: no IL2CPP ele tem um tipo próprio.
                    Action aoClicar = () =>
                    {
                        if (GameSettingMenu.Instance != null)
                            GameSettingMenu.Instance.ChangeTab(3, false);
                    };
                    BotaoMod.OnClick.AddListener(
                        DelegateSupport.ConvertDelegate<UnityAction>(aoClicar));

                    __instance.ControllerSelectable.Add(BotaoMod);
                }

                BotaoMod.gameObject.SetActive(YouTubeVoteBridgePlugin.EhHost());
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao criar a aba de configurações do mod: " + ex);
            }
        }
    }

    [HarmonyPatch(typeof(GameOptionsMenu), nameof(GameOptionsMenu.Initialize))]
    internal static class YouTubeVoteSettingsTabInitializePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameOptionsMenu __instance)
        {
            if (__instance == null ||
                __instance != YouTubeVoteSettingsTabCreatePatch.MenuMod)
                return true;

            try
            {
                if (__instance.Children != null)
                {
                    foreach (OptionBehaviour child in __instance.Children)
                    {
                        if (child != null && child.gameObject != null)
                            child.gameObject.SetActive(false);
                    }
                    __instance.Children.Clear();
                }

                __instance.InitializeControllerNavigation();

                if (__instance.MapPicker != null)
                    __instance.MapPicker.gameObject.SetActive(false);

                return false;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao preparar a aba de configurações do mod: " + ex);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.ChangeTab))]
    internal static class YouTubeVoteSettingsTabChangePatch
    {
        [HarmonyPrefix]
        private static bool Prefix(GameSettingMenu __instance, int tabNum, bool previewOnly)
        {
            if (tabNum != 3)
                return true;

            try
            {
                var tab = YouTubeVoteSettingsTabCreatePatch.MenuMod;
                var button = YouTubeVoteSettingsTabCreatePatch.BotaoMod;
                if (tab == null || button == null)
                    return true;

                if (previewOnly)
                    return false;

                __instance.PresetsTab.gameObject.SetActive(false);
                __instance.GameSettingsTab.gameObject.SetActive(false);
                __instance.RoleSettingsTab.gameObject.SetActive(false);

                __instance.GamePresetsButton.SelectButton(false);
                __instance.GameSettingsButton.SelectButton(false);
                __instance.RoleSettingsButton.SelectButton(false);

                tab.gameObject.SetActive(true);
                __instance.MenuDescriptionText.text = "Configurações do YouTubeVoteBridge";

                button.SelectButton(true);
                __instance.ToggleLeftSideDarkener(true);
                __instance.ToggleRightSideDarkener(false);

                tab.Initialize();
                return false;
            }
            catch (Exception ex)
            {
                YouTubeVoteBridgePlugin.Instance?.Log.LogError(
                    "Falha ao abrir a aba de configurações do mod: " + ex);
                return true;
            }
        }
    }
}
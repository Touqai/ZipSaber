using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Util;
using HMUI;
using UnityEngine;

namespace ZipSaber
{
    internal class ModManagerFlowCoordinator : FlowCoordinator
    {
        // Do NOT cache these as static — they get destroyed on scene reload.
        // Always create fresh via BeatSaberUI when needed.
        private ModManagerViewController      _modManagerVC;
        private BeatModsBrowserViewController _browserVC;

        // The flow coordinator itself can be static since FlowCoordinators
        // survive scene transitions (they live on DontDestroyOnLoad objects).
        private static ModManagerFlowCoordinator _instance;

        internal static void Present()
        {
            var mainFlow = GetMainFlow();
            if (mainFlow == null) return;

            // If the cached instance was destroyed (scene reload), recreate it
            if (_instance == null)
                _instance = BeatSaberUI.CreateFlowCoordinator<ModManagerFlowCoordinator>();

            mainFlow.PresentFlowCoordinator(_instance,
                animationDirection: ViewController.AnimationDirection.Horizontal);
        }

        internal static void PresentBrowser()
        {
            if (_instance == null) return;
            _instance.ShowBrowser();
        }

        private void ShowBrowser()
        {
            // Always create fresh — previous instance may be destroyed
            if (_browserVC == null)
                _browserVC = BeatSaberUI.CreateViewController<BeatModsBrowserViewController>();
            SetTitle("Browse BeatMods");
            ReplaceTopViewController(_browserVC,
                animationType: ViewController.AnimationType.In,
                animationDirection: ViewController.AnimationDirection.Horizontal);
        }

        private void ShowModManager()
        {
            if (_modManagerVC == null)
                _modManagerVC = BeatSaberUI.CreateViewController<ModManagerViewController>();
            SetTitle("ZipSaber - Mod Manager");
            ReplaceTopViewController(_modManagerVC,
                animationType: ViewController.AnimationType.In,
                animationDirection: ViewController.AnimationDirection.Horizontal);
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            try
            {
                if (firstActivation)
                {
                    SetTitle("ZipSaber - Mod Manager");
                    showBackButton = true;
                    if (_modManagerVC == null)
                        _modManagerVC = BeatSaberUI.CreateViewController<ModManagerViewController>();
                    ProvideInitialViewControllers(_modManagerVC);
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.Error($"[ModManagerFlow] DidActivate error: {ex.Message}\n{ex}");
            }
        }

        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            if (topViewController == _browserVC)
                ShowModManager();
            else
            {
                var mainFlow = GetMainFlow();
                mainFlow?.DismissFlowCoordinator(this);
            }
        }

        private static FlowCoordinator GetMainFlow()
        {
            foreach (var fc in Resources.FindObjectsOfTypeAll<FlowCoordinator>())
                if (fc.GetType().Name == "MainFlowCoordinator") return fc;
            Plugin.Log?.Error("[ModManagerFlow] MainFlowCoordinator not found.");
            return null;
        }
    }
}

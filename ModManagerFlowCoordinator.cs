using BeatSaberMarkupLanguage;
using HMUI;
using UnityEngine;

namespace ZipSaber
{
    internal class ModManagerFlowCoordinator : FlowCoordinator
    {
        // Do NOT cache these as static — they get destroyed on scene reload.
        private ModManagerViewController      _modManagerVC;
        private BeatModsBrowserViewController _browserVC;

        private static ModManagerFlowCoordinator _instance;

        internal static void Present()
        {
            // On BSML 1.6.x, BeatSaberUI.MainFlowCoordinator is a static property
            var mainFlow = BeatSaberUI.MainFlowCoordinator;
            if (mainFlow == null)
            {
                Plugin.Log?.Error("[ModManagerFlow] MainFlowCoordinator not found.");
                return;
            }

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
                BeatSaberUI.MainFlowCoordinator?.DismissFlowCoordinator(this);
        }
    }
}

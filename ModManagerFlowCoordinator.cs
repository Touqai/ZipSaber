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
        private ManagerSettingsViewController _settingsVC;
        private GambleViewController          _gambleVC;

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

        internal static void PresentSettings()
        {
            if (_instance == null) return;
            if (_instance._settingsVC == null)
                _instance._settingsVC = BeatSaberUI.CreateViewController<ManagerSettingsViewController>();
            _instance.SetTitle(null);
            _instance.ReplaceTopViewController(_instance._settingsVC,
                animationType: ViewController.AnimationType.In,
                animationDirection: ViewController.AnimationDirection.Vertical);
        }

        internal static void PresentGamble()
        {
            if (_instance == null) return;
            if (_instance._gambleVC == null)
                _instance._gambleVC = BeatSaberUI.CreateViewController<GambleViewController>();
            _instance.SetTitle(null);
            _instance.ReplaceTopViewController(_instance._gambleVC,
                animationType: ViewController.AnimationType.In,
                animationDirection: ViewController.AnimationDirection.Vertical);
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
            SetTitle(null); // ZipSaber draws its own flat header + back button
            ReplaceTopViewController(_browserVC,
                animationType: ViewController.AnimationType.In,
                animationDirection: ViewController.AnimationDirection.Horizontal);
        }

        private void ShowModManager()
        {
            if (_modManagerVC == null)
                _modManagerVC = BeatSaberUI.CreateViewController<ModManagerViewController>();
            SetTitle(null);
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
                    // No game title bar: the views have their own flat header with a back button
                    SetTitle(null);
                    showBackButton = false;
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

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }

        /// <summary>Called by the in-view back buttons.</summary>
        internal static void GoBack()
        {
            if (_instance == null) return;
            _instance.BackButtonWasPressed(_instance.topViewController);
        }

        protected override void BackButtonWasPressed(ViewController topViewController)
        {
            if (topViewController == _browserVC || topViewController == _settingsVC || topViewController == _gambleVC)
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

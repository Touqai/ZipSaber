using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;

namespace ZipSaber
{
    [ViewDefinition("ZipSaber.settings.bsml")]
    [HotReload(RelativePathToLayout = @"settings.bsml")]
    internal class SettingsViewController : BSMLAutomaticViewController
    {
        // Singleton — kept alive across scene reloads so BSML doesn't re-parse
        // needlessly. Plugin.RegisterBSMLUI() always passes this same instance.
        private static SettingsViewController _instance;
        public static SettingsViewController instance
        {
            get
            {
                if (_instance == null)
                    _instance = BeatSaberUI.CreateViewController<SettingsViewController>();
                return _instance;
            }
        }

        [UIValue("delete-on-close")]
        public bool DeleteOnClose_UI
        {
            get => Plugin.Config?.DeleteOnClose ?? false;
            set
            {
                if (Plugin.Config == null) return;
                Plugin.Config.DeleteOnClose = value;
                Plugin.Log?.Info($"[Settings] DeleteOnClose → {value}");
                NotifyPropertyChanged(nameof(DeleteOnClose_UI));
            }
        }

        [UIValue("show-destination-prompt")]
        public bool ShowDestinationPrompt_UI
        {
            get => Plugin.Config?.ShowDestinationPrompt ?? true;
            set
            {
                if (Plugin.Config == null) return;
                Plugin.Config.ShowDestinationPrompt = value;
                Plugin.Log?.Info($"[Settings] ShowDestinationPrompt → {value}");
                NotifyPropertyChanged(nameof(ShowDestinationPrompt_UI));
            }
        }

        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            Plugin.Log?.Info($"[Settings] DidActivate: DeleteOnClose={DeleteOnClose_UI}, ShowPrompt={ShowDestinationPrompt_UI}");
            NotifyPropertyChanged(nameof(DeleteOnClose_UI));
            NotifyPropertyChanged(nameof(ShowDestinationPrompt_UI));
        }
    }
}

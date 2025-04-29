using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using System;

namespace ZipSaber
{
    [ViewDefinition("ZipSaber.settings.bsml")]
    [HotReload(RelativePathToLayout = @"settings.bsml")]
    internal class SettingsViewController : BSMLAutomaticViewController
    {
        // --- Instance ---
        private static SettingsViewController _instance;
        public static SettingsViewController instance
        {
            get { if (_instance == null) _instance = new SettingsViewController(); return _instance; }
            private set { _instance = value; }
        }
        // --- End Instance ---

        // --- Backing field for the UI toggle ---
        private bool _uiDeleteOnCloseValue;

        // --- UI Property ---
        [UIValue("delete-on-close")]
        public bool DeleteOnClose_UI // Renamed property slightly to avoid confusion
        {
            get => _uiDeleteOnCloseValue; // Get value from backing field
            set
            {
                if (_uiDeleteOnCloseValue == value) return; // No change
                _uiDeleteOnCloseValue = value;
                // Update the actual config only when UI changes
                if (Plugin.Config != null)
                {
                     Plugin.Config.DeleteOnClose = value;
                     Console.WriteLine($"[ZipSaber][Settings] DeleteOnClose set to: {value}"); // Console Log
                }
                else
                {
                     Console.WriteLine("[ZipSaber][Settings] ERROR: Config was null on attempting to set DeleteOnClose.");
                }
                NotifyPropertyChanged(); // Notify BSML the UI property changed
            }
        }
        // --- End UI Property ---

        // Public parameterless constructor
        public SettingsViewController() { }

        // Load initial value when the view becomes active
        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (Plugin.Config != null)
            {
                 // Load current config value into the UI backing field
                 _uiDeleteOnCloseValue = Plugin.Config.DeleteOnClose;
                 Console.WriteLine($"[ZipSaber][Settings] SettingsViewController Activated. Loaded DeleteOnClose = {_uiDeleteOnCloseValue}");
            }
            else
            {
                 // Config wasn't ready, use default
                 _uiDeleteOnCloseValue = false; // Default value
                 Console.WriteLine("[ZipSaber][Settings] ERROR: Config was null during DidActivate! Using default UI value.");
            }
            // Ensure UI reflects the loaded value
            NotifyPropertyChanged(nameof(DeleteOnClose_UI));
        }
    }
}
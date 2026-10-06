using System.Collections.Generic;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.ViewControllers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>ZipSaber settings, opened from the cog in the Mod Manager header.</summary>
    [ViewDefinition("ZipSaber.zipsaber-settings.bsml")]
    internal class ManagerSettingsViewController : BSMLAutomaticViewController
    {
        private static readonly (string Name, string Hex)[] Presets =
        {
            ("Sky", "#5FA8E8"), ("Teal", "#3FD0B6"), ("Lime", "#7ED957"), ("Gold", "#F2C94C"),
            ("Orange", "#FF8A3D"), ("Red", "#FF4F5E"), ("Pink", "#FF5FC8"), ("Violet", "#A07CFF"), ("Snow", "#E6E6EA"),
        };

        [UIComponent("zs-tag")]   private TextMeshProUGUI _tag = null;
        [UIComponent("sec-maps")] private TextMeshProUGUI _secMaps = null;
        [UIComponent("sec-look")] private TextMeshProUGUI _secLook = null;
        [UIObject("swatch-row")]  private GameObject _swatchRow = null;

        private Image _preview;
        private string _wipStatus = "";

        [UIAction("go-back")]
        private void OnGoBack() => ModManagerFlowCoordinator.GoBack();

        // ── Maps ──────────────────────────────────────────────────────────────────
        [UIValue("show-destination-prompt")]
        public bool ShowDestinationPrompt
        {
            get => Plugin.Config?.ShowDestinationPrompt ?? true;
            set { if (Plugin.Config != null) Plugin.Config.ShowDestinationPrompt = value; }
        }

        [UIValue("delete-on-close")]
        public bool DeleteOnClose
        {
            get => Plugin.Config?.DeleteOnClose ?? false;
            set { if (Plugin.Config != null) Plugin.Config.DeleteOnClose = value; }
        }

        [UIValue("wip-path-input")]
        public string WipPathInput
        {
            get => Plugin.Config?.CustomWipPath ?? "";
            set { }
        }

        [UIValue("wip-status")]
        public string WipStatus => _wipStatus;

        [UIAction("wip-path-changed")]
        private void OnWipPathChanged(string value)
        {
            string err = WipFolder.Set(value);
            UpdateWipStatus(err);
            NotifyPropertyChanged(nameof(WipPathInput));
        }

        [UIAction("wip-paste")]
        private void OnWipPaste()
        {
            string text = Clipboard.GetText();
            if (string.IsNullOrWhiteSpace(text))
            {
                UpdateWipStatus("Clipboard is empty - copy a folder path first.");
                return;
            }
            // Take the first line and strip quotes from "Copy as path"
            text = text.Split(new[] { '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries)[0].Trim().Trim('"');
            string err = WipFolder.Set(text);
            UpdateWipStatus(err);
            NotifyPropertyChanged(nameof(WipPathInput));
        }

        [UIAction("wip-reset")]
        private void OnWipReset()
        {
            WipFolder.Set("");
            UpdateWipStatus(null);
            NotifyPropertyChanged(nameof(WipPathInput));
        }

        private void UpdateWipStatus(string error)
        {
            string current = WipFolder.Current ?? "(unknown)";
            _wipStatus = error != null
                ? $"<color=#FF6B6B>Couldn't use that folder: {Esc(error)}</color>\nStill using: {Esc(current)}"
                : WipFolder.UsingCustom
                    ? $"<color=#7ED957>Custom folder</color> — dropped WIP maps go to {Esc(current)} and show up in the WIP pack."
                    : $"Default — dropped WIP maps go to {Esc(current)}";
            NotifyPropertyChanged(nameof(WipStatus));
        }

        private static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";

        // ── Accent ────────────────────────────────────────────────────────────────
        [UIValue("accent-hue")]
        public int AccentHue   // int: BSML integer-only sliders unbox the value as int
        {
            get => Mathf.RoundToInt(Theme.Hue) % 360;
            set { }
        }

        [UIAction("hue-changed")]
        private void OnHueChanged(int value) => Theme.SetHue(value);

        [UIAction("hue-format")]
        private string HueFormat(int value)
        {
            return $"Hue {value}";   // slider value text doesn't render rich text
        }

        // ── Lifecycle ─────────────────────────────────────────────────────────────
        protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            if (firstActivation) FlatView.Prepare(this);
            base.DidActivate(firstActivation, addedToHierarchy, screenSystemEnabling);
            if (firstActivation)
            {
                FlatView.Finish(this);
                BuildSwatches();
            }
            Theme.Changed -= ApplyTheme;
            Theme.Changed += ApplyTheme;
            UpdateWipStatus(null);
            ApplyTheme();
        }

        protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            Theme.Changed -= ApplyTheme;
            base.DidDeactivate(removedFromHierarchy, screenSystemDisabling);
        }

        private void BuildSwatches()
        {
            if (_swatchRow == null) return;
            var row = _swatchRow.transform;

            var lbl = UiKit.Text(row, "PresetsLabel", "Presets", 2.8f, new Color(0.75f, 0.75f, 0.8f), TextAlignmentOptions.Left);
            UiKit.Size(lbl.gameObject, 14, 6);

            foreach (var (name, hex) in Presets)
            {
                ColorUtility.TryParseHtmlString(hex, out var col);
                var sw = UiKit.Button(row, "Swatch_" + name, col, 7, 6, () => Theme.SetAccent(col));
                sw.sprite = UiKit.RoundSprite;
            }

            var spacer = UiKit.Rect("Spacer", row);
            UiKit.Size(spacer.gameObject, 3, 6);

            var plbl = UiKit.Text(row, "PreviewLabel", "Now", 2.8f, new Color(0.75f, 0.75f, 0.8f), TextAlignmentOptions.Right);
            UiKit.Size(plbl.gameObject, 8, 6);
            _preview = UiKit.Img(row, "Preview", UiKit.RoundSprite, Theme.Accent, sliced: true);
            UiKit.Size(_preview.gameObject, 12, 6);
        }

        private void ApplyTheme()
        {
            var a = Theme.Accent;
            if (_tag != null)     _tag.color = a;
            if (_secMaps != null) _secMaps.color = a;
            if (_secLook != null) _secLook.color = a;
            if (_preview != null) _preview.color = a;
            NotifyPropertyChanged(nameof(AccentHue));
        }
    }
}

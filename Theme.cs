using System;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>User-chosen accent colour for ZipSaber's UI.</summary>
    internal static class Theme
    {
        internal const string DefaultHex = "#5FA8E8";

        internal static event Action Changed;

        internal static Color Accent
        {
            get
            {
                string hex = Plugin.Config?.AccentColor;
                return ColorUtility.TryParseHtmlString(string.IsNullOrEmpty(hex) ? DefaultHex : hex, out var c)
                    ? c : new Color(0.37f, 0.66f, 0.91f);
            }
        }

        internal static string AccentHex => "#" + ColorUtility.ToHtmlStringRGB(Accent);

        /// <summary>Accent at a given brightness/alpha, for backgrounds and hovers.</summary>
        internal static Color AccentShade(float valueMul, float alpha)
        {
            Color.RGBToHSV(Accent, out float h, out float s, out float v);
            var c = Color.HSVToRGB(h, s, Mathf.Clamp01(v * valueMul));
            c.a = alpha;
            return c;
        }

        internal static float Hue
        {
            get { Color.RGBToHSV(Accent, out float h, out _, out _); return h * 360f; }
        }

        internal static void SetAccent(Color c)
        {
            if (Plugin.Config == null) return;
            Plugin.Config.AccentColor = "#" + ColorUtility.ToHtmlStringRGB(c);
            Changed?.Invoke();
        }

        internal static void SetHue(float degrees)
        {
            Color.RGBToHSV(Accent, out _, out float s, out float v);
            if (s < 0.25f) s = 0.6f;          // coming from white/grey: give the hue something to show
            if (v < 0.5f)  v = 0.92f;
            SetAccent(Color.HSVToRGB(Mathf.Repeat(degrees, 360f) / 360f, s, v));
        }
    }
}

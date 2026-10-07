using System;
using BeatSaberMarkupLanguage.FloatingScreen;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// Shared look for ZipSaber's floating prompts (flat dark panel, accent bar,
    /// ZIPSABER tag, X button, two action buttons, countdown bar).
    /// </summary>
    internal class PromptUi
    {
        internal FloatingScreen Screen;
        internal Transform Panel;
        internal TextMeshProUGUI Tag, Title, Body, Note, Countdown;
        internal Image AccentBar, Border, TimerFill, PrimaryBtn, SecondaryBtn;
        internal TextMeshProUGUI PrimaryLabel, SecondaryLabel;
        internal float W, H;

        /// <param name="onPrimary">left (main) button</param>
        /// <param name="onSecondary">right button</param>
        /// <param name="onClose">X button</param>
        internal static PromptUi Build(string name, float w, float h, Action onPrimary, Action onSecondary, Action onClose)
        {
            var ui = new PromptUi { W = w, H = h };
            ui.Screen = FloatingScreen.CreateFloatingScreen(new Vector2(w, h), false,
                new Vector3(0f, 1.5f, 2.4f), Quaternion.identity, 0f, false);   // flat
            ui.Screen.gameObject.name = name;
            UnityEngine.Object.DontDestroyOnLoad(ui.Screen.gameObject);

            foreach (var canvas in ui.Screen.GetComponentsInChildren<Canvas>(true))
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder = 32767;
            }
            foreach (var canvas in ui.Screen.GetComponentsInChildren<Canvas>(true))
                if (canvas.GetComponent<UnityEngine.EventSystems.BaseRaycaster>() == null)
                    canvas.gameObject.AddComponent<GraphicRaycaster>();

            var root = ui.Screen.transform;

            // Swallow clicks around the prompt so you don't hit the menu behind it
            var blocker = UiKit.Img(root, "ClickBlocker", UiKit.RoundSprite, Color.clear);
            blocker.raycastTarget = true;
            blocker.rectTransform.anchorMin = Vector2.zero; blocker.rectTransform.anchorMax = Vector2.one;
            blocker.rectTransform.offsetMin = new Vector2(-500, -500); blocker.rectTransform.offsetMax = new Vector2(500, 500);

            ui.Border = UiKit.Img(root, "Border", UiKit.RoundSprite, Color.white, sliced: true);
            UiKit.Stretch(ui.Border.rectTransform, -0.4f);
            var panel = UiKit.Img(root, "Panel", UiKit.RoundSprite, new Color(0.06f, 0.06f, 0.09f, 0.98f), sliced: true);
            UiKit.Stretch(panel.rectTransform);
            panel.raycastTarget = true;
            var p = ui.Panel = panel.transform;

            ui.AccentBar = UiKit.Img(p, "AccentBar", UiKit.RoundSprite, Theme.Accent, sliced: true);
            Place(ui.AccentBar.rectTransform, 3f, h - 4f, 0.9f, 9f);

            ui.Tag = UiKit.Text(p, "Tag", "ZIPSABER", 2.4f, Theme.Accent, TextAlignmentOptions.BottomLeft);
            ui.Tag.fontStyle = FontStyles.Bold;
            Place(ui.Tag.rectTransform, 6f, h - 4f, 60f, 3.5f);

            ui.Title = UiKit.Text(p, "Title", "", 4.6f, Color.white, TextAlignmentOptions.TopLeft);
            ui.Title.fontStyle = FontStyles.Bold;
            Place(ui.Title.rectTransform, 6f, h - 7.5f, w - 24f, 6f);

            var close = UiKit.Button(p, "Close", new Color(0.16f, 0.16f, 0.21f, 0.95f), 8, 8, onClose, "X", 3.6f);
            Place(close.rectTransform, w - 12f, h - 4f, 8f, 8f);

            ui.Body = UiKit.Text(p, "Body", "", 3f, new Color(0.88f, 0.88f, 0.92f), TextAlignmentOptions.TopLeft);
            ui.Body.enableWordWrapping = true;
            ui.Body.overflowMode = TextOverflowModes.Ellipsis;
            Place(ui.Body.rectTransform, 6f, h - 16f, w - 12f, h - 16f - 29f);

            ui.Note = UiKit.Text(p, "Note", "", 2.5f, new Color(0.62f, 0.62f, 0.7f), TextAlignmentOptions.TopLeft);
            ui.Note.enableWordWrapping = true;
            Place(ui.Note.rectTransform, 6f, 28.5f, w - 12f, 6f);

            const float btnW = 42f, btnH = 9f, btnTop = 21f;
            ui.PrimaryBtn = UiKit.Button(p, "Primary", Theme.Accent, btnW, btnH, onPrimary, " ", 3.2f);
            Place(ui.PrimaryBtn.rectTransform, 6f, btnTop, btnW, btnH);
            ui.PrimaryLabel = ui.PrimaryBtn.GetComponentInChildren<TextMeshProUGUI>();

            ui.SecondaryBtn = UiKit.Button(p, "Secondary", new Color(0.16f, 0.16f, 0.21f, 1f), btnW, btnH, onSecondary, " ", 3.2f);
            Place(ui.SecondaryBtn.rectTransform, w - 6f - btnW, btnTop, btnW, btnH);
            ui.SecondaryLabel = ui.SecondaryBtn.GetComponentInChildren<TextMeshProUGUI>();

            ui.Countdown = UiKit.Text(p, "Countdown", "", 2.5f, new Color(0.55f, 0.55f, 0.62f), TextAlignmentOptions.BottomLeft);
            Place(ui.Countdown.rectTransform, 6f, 9f, w - 12f, 4f);

            var track = UiKit.Img(p, "TimerTrack", UiKit.RoundSprite, new Color(1f, 1f, 1f, 0.08f), sliced: true);
            Place(track.rectTransform, 6f, 4.5f, w - 12f, 1.2f);
            ui.TimerFill = UiKit.Img(track.transform, "Fill", UiKit.RoundSprite, Theme.Accent, sliced: true);
            var fr = ui.TimerFill.rectTransform;
            fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = fr.offsetMax = Vector2.zero;

            ui.ApplyTheme();
            return ui;
        }

        internal void ApplyTheme()
        {
            var a = Theme.Accent;
            if (AccentBar != null) AccentBar.color = a;
            if (Tag != null) Tag.color = a;
            if (Border != null) Border.color = new Color(a.r, a.g, a.b, 0.35f);
            if (TimerFill != null) TimerFill.color = a;
            if (PrimaryBtn != null) UiKit.Recolor(PrimaryBtn, a);
        }

        internal void SetTimer(float fraction)
        {
            if (TimerFill != null) TimerFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
        }

        internal void Show(bool on) { if (Screen != null) Screen.gameObject.SetActive(on); }

        /// <summary>Position by top-left corner in panel units (origin bottom-left).</summary>
        internal static void Place(RectTransform rt, float x, float top, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, top);
        }

        internal static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";
    }
}

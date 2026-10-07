using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// In-view confirm card (delete mod, disable with dependents, install from BeatMods),
    /// styled like ZipSaber's floating prompts. Fills a BSML host element.
    /// </summary>
    internal class InlinePrompt
    {
        internal struct Btn
        {
            internal string Label; internal Color Color; internal Action OnClick; internal float Weight;
            internal static Btn Primary(string l, Action a, float w = 1f) => new Btn { Label = l, Color = Theme.Accent, OnClick = a, Weight = w };
            internal static Btn Danger(string l, Action a, float w = 1f)  => new Btn { Label = l, Color = new Color(0.55f, 0.12f, 0.14f, 1f), OnClick = a, Weight = w };
            internal static Btn Neutral(string l, Action a, float w = 1f) => new Btn { Label = l, Color = new Color(0.17f, 0.17f, 0.22f, 1f), OnClick = a, Weight = w };
        }

        private RectTransform _root, _buttonRow;
        private Image _border, _accentBar, _timerFill;
        private TextMeshProUGUI _kind, _title, _body, _note, _countdown;

        internal static InlinePrompt Build(GameObject host)
        {
            var p = new InlinePrompt();
            var root = UiKit.Rect("ZipSaberConfirm", host.transform);
            UiKit.Stretch(root);
            root.gameObject.AddComponent<LayoutElement>().ignoreLayout = true;
            p._root = root;

            p._border = UiKit.Img(root, "Border", UiKit.RoundSprite, Color.white, sliced: true);
            UiKit.Stretch(p._border.rectTransform, -0.4f);
            var panel = UiKit.Img(root, "Panel", UiKit.RoundSprite, new Color(0.06f, 0.06f, 0.09f, 0.97f), sliced: true);
            UiKit.Stretch(panel.rectTransform);
            panel.raycastTarget = true;
            var t = panel.transform;

            p._accentBar = UiKit.Img(t, "AccentBar", UiKit.RoundSprite, Theme.Accent, sliced: true);
            Top(p._accentBar.rectTransform, 3f, 3.5f, 0.9f, 10f);

            p._kind = UiKit.Text(t, "Kind", "", 2.4f, Theme.Accent, TextAlignmentOptions.TopLeft);
            p._kind.fontStyle = FontStyles.Bold; p._kind.characterSpacing = 3;
            Top(p._kind.rectTransform, 6f, 3.5f, 90f, 3.5f);

            p._title = UiKit.Text(t, "Title", "", 4.6f, Color.white, TextAlignmentOptions.TopLeft);
            p._title.fontStyle = FontStyles.Bold;
            // no wrapping + room for the full line height, otherwise TMP hides the whole line
            p._title.enableWordWrapping = false;
            p._title.overflowMode = TextOverflowModes.Ellipsis;
            p._title.richText = true;
            var tr = p._title.rectTransform;
            tr.anchorMin = new Vector2(0, 1); tr.anchorMax = new Vector2(1, 1); tr.pivot = new Vector2(0.5f, 1f);
            tr.offsetMin = new Vector2(6f, -16f); tr.offsetMax = new Vector2(-6f, -7f);

            p._body = UiKit.Text(t, "Body", "", 3f, new Color(0.85f, 0.85f, 0.9f), TextAlignmentOptions.TopLeft);
            p._body.enableWordWrapping = true;
            p._body.overflowMode = TextOverflowModes.Ellipsis;
            var br = p._body.rectTransform;
            br.anchorMin = new Vector2(0, 0); br.anchorMax = new Vector2(1, 1);
            br.offsetMin = new Vector2(6f, 25f); br.offsetMax = new Vector2(-6f, -18f);

            p._note = UiKit.Text(t, "Note", "", 2.6f, new Color(1f, 0.42f, 0.42f), TextAlignmentOptions.BottomLeft);
            p._note.enableWordWrapping = true;
            Bottom(p._note.rectTransform, 18f, 5.5f);

            p._buttonRow = UiKit.Rect("Buttons", t);
            Bottom(p._buttonRow, 8f, 9f);
            var h = p._buttonRow.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 2f; h.childAlignment = TextAnchor.MiddleLeft;
            h.childControlWidth = true; h.childControlHeight = true;
            h.childForceExpandWidth = true; h.childForceExpandHeight = true;

            p._countdown = UiKit.Text(t, "Countdown", "", 2.4f, new Color(0.55f, 0.55f, 0.62f), TextAlignmentOptions.BottomLeft);
            Bottom(p._countdown.rectTransform, 3.6f, 3.6f);

            var track = UiKit.Img(t, "TimerTrack", UiKit.RoundSprite, new Color(1f, 1f, 1f, 0.08f), sliced: true);
            Bottom(track.rectTransform, 2f, 1.1f);
            p._timerFill = UiKit.Img(track.transform, "Fill", UiKit.RoundSprite, Theme.Accent, sliced: true);
            var fr = p._timerFill.rectTransform;
            fr.anchorMin = Vector2.zero; fr.anchorMax = Vector2.one; fr.offsetMin = fr.offsetMax = Vector2.zero;
            return p;
        }

        /// <summary>Fill the card. kindColor null = accent (use red for destructive actions).</summary>
        internal void Set(string kind, string title, string body, string note, Color? kindColor, params Btn[] buttons)
        {
            var a = Theme.Accent;
            var k = kindColor ?? a;
            _kind.text = kind; _kind.color = k;
            _accentBar.color = k;
            _border.color = new Color(k.r, k.g, k.b, 0.35f);
            _timerFill.color = k;
            _title.text = title ?? "";
            _body.text = body ?? "";
            _note.text = note ?? "";

            UiKit.ClearChildren(_buttonRow);
            foreach (var b in buttons)
            {
                var col = b.Color == Theme.Accent ? a : b.Color;
                var img = UiKit.Button(_buttonRow, "Btn_" + b.Label, col, -1, 9f, b.OnClick, b.Label, 3f);
                var le = img.GetComponent<LayoutElement>();
                le.flexibleWidth = b.Weight; le.minWidth = 10f; le.preferredWidth = -1;
            }
            SetTimer(1f, "");
        }

        internal void SetTimer(float fraction, string text)
        {
            if (_timerFill != null) _timerFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(fraction), 1f);
            if (_countdown != null) _countdown.text = text ?? "";
        }

        // anchored to the top-left of the card
        private static void Top(RectTransform rt, float x, float yFromTop, float w, float h)
        {
            rt.anchorMin = rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.sizeDelta = new Vector2(w, h);
            rt.anchoredPosition = new Vector2(x, -yFromTop);
        }

        // full width minus 6 on each side, placed by distance from the bottom edge
        private static void Bottom(RectTransform rt, float bottom, float height)
        {
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 0);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(6f, bottom);
            rt.offsetMax = new Vector2(-6f, bottom + height);
        }
    }
}

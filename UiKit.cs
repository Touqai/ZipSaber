using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>Small shared helpers for ZipSaber's code-built UI (flat, glow-free).</summary>
    internal static class UiKit
    {
        private static Sprite   _roundSprite;
        private static Material _noGlowMat;

        internal static Sprite RoundSprite
        {
            get
            {
                if (_roundSprite != null) return _roundSprite;
                foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (s.name == "RoundRect10" || s.name == "RoundRectSmall") { _roundSprite = s; break; }
                return _roundSprite;
            }
        }

        internal static Material NoGlow
        {
            get
            {
                if (_noGlowMat != null) return _noGlowMat;
                foreach (var m in Resources.FindObjectsOfTypeAll<Material>())
                    if (m.name == "UINoGlow") { _noGlowMat = m; break; }
                return _noGlowMat;
            }
        }

        internal static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        internal static LayoutElement Size(GameObject go, float w, float h)
        {
            var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            if (w >= 0) { le.preferredWidth = w; le.minWidth = w; }
            if (h >= 0) { le.preferredHeight = h; le.minHeight = h; }
            return le;
        }

        internal static Image Img(Transform parent, string name, Sprite sprite, Color color, bool sliced = false)
        {
            var rt = Rect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = sprite ?? RoundSprite;
            img.type = sliced ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            img.preserveAspect = !sliced;
            img.material = NoGlow;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        internal static TextMeshProUGUI Text(Transform parent, string name, string text, float size, Color color,
                                             TextAlignmentOptions align = TextAlignmentOptions.Center)
        {
            var rt = Rect(name, parent);
            var t = rt.gameObject.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.color = color; t.alignment = align;
            t.richText = true; t.enableWordWrapping = false; t.raycastTarget = false;
            NoGlowText(t);
            return t;
        }

        internal static void NoGlowText(TMP_Text t)
        {
            if (t.font == null || t.fontSharedMaterial == null) return;
            var mat = new Material(t.fontSharedMaterial);
            if (mat.HasProperty("_GlowColor"))  mat.SetColor("_GlowColor", Color.clear);
            if (mat.HasProperty("_GlowPower"))  mat.SetFloat("_GlowPower", 0f);
            if (mat.HasProperty("_GlowOffset")) mat.SetFloat("_GlowOffset", 0f);
            if (mat.HasProperty("_GlowOuter"))  mat.SetFloat("_GlowOuter", 0f);
            t.fontSharedMaterial = mat;
        }

        internal static void Stretch(RectTransform rt, float inset = 0f)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
        }

        /// <summary>A clickable, hover-highlighting rounded button. Returns the background image.</summary>
        internal static Image Button(Transform parent, string name, Color color, float w, float h, Action onClick,
                                     string label = null, float fontSize = 2.8f, Sprite icon = null, float iconInset = 1.2f)
        {
            var bg = Img(parent, name, RoundSprite, color, sliced: true);
            Size(bg.gameObject, w, h);
            bg.raycastTarget = true;

            if (icon != null)
            {
                var ic = Img(bg.transform, "Icon", icon, Color.white);
                Stretch(ic.rectTransform, iconInset);
            }
            if (!string.IsNullOrEmpty(label))
            {
                var t = Text(bg.transform, "Label", label, fontSize, Color.white);
                t.fontStyle = FontStyles.Bold; t.characterSpacing = 2;
                Stretch(t.rectTransform);
            }

            Color baseCol = color;
            Color hover = new Color(Mathf.Min(color.r * 1.3f + 0.05f, 1f), Mathf.Min(color.g * 1.3f + 0.05f, 1f), Mathf.Min(color.b * 1.3f + 0.05f, 1f), Mathf.Max(color.a, 0.9f));
            var et = bg.gameObject.AddComponent<EventTrigger>();
            AddTrigger(et, EventTriggerType.PointerEnter, () => bg.color = hover);
            AddTrigger(et, EventTriggerType.PointerExit,  () => bg.color = baseCol);

            var btn = bg.gameObject.AddComponent<UnityEngine.UI.Button>();
            btn.transition = Selectable.Transition.None;
            btn.targetGraphic = bg;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            return bg;
        }

        /// <summary>Change a UiKit button's resting colour (keeps hover working).</summary>
        internal static void Recolor(Image buttonBg, Color color)
        {
            if (buttonBg == null) return;
            buttonBg.color = color;
            var et = buttonBg.GetComponent<EventTrigger>();
            if (et == null) return;
            et.triggers.Clear();
            Color hover = new Color(Mathf.Min(color.r * 1.3f + 0.05f, 1f), Mathf.Min(color.g * 1.3f + 0.05f, 1f), Mathf.Min(color.b * 1.3f + 0.05f, 1f), Mathf.Max(color.a, 0.9f));
            AddTrigger(et, EventTriggerType.PointerEnter, () => buttonBg.color = hover);
            AddTrigger(et, EventTriggerType.PointerExit,  () => buttonBg.color = color);
        }

        internal static void AddTrigger(EventTrigger et, EventTriggerType type, Action a)
        {
            var e = new EventTrigger.Entry { eventID = type };
            e.callback.AddListener(_ => a());
            et.triggers.Add(e);
        }

        internal static void ClearChildren(Transform t)
        {
            if (t == null) return;
            for (int i = t.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(t.GetChild(i).gameObject);
        }
    }
}

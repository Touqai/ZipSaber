using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Draws ZipSaber's icons at runtime (no bundled assets): settings cog, slot-machine icon,
    /// and Beat Saber note parts for the gamble reels. All shapes are white with anti-aliased
    /// edges so they can be tinted with Image.color.
    /// </summary>
    internal static class IconFactory
    {
        private static readonly Dictionary<string, Sprite> _cache = new Dictionary<string, Sprite>();

        internal static Sprite Cog         => Get("cog",    128, CogShape);
        internal static Sprite NoteBody    => Get("body",   128, NoteBodyShape);
        internal static Sprite NoteArrow   => Get("arrow",  128, ArrowShape);
        internal static Sprite NoteDot     => Get("dot",    128, DotShape);
        internal static Sprite Bomb        => Get("bomb",   128, BombShape);
        internal static Sprite BombGlint   => Get("glint",  128, GlintShape);
        internal static Sprite Slot        => Get("slot",   128, SlotShape);
        internal static Sprite Circle      => Get("circle",  64, (x, y) => Disc(x, y, 0.5f, 0.5f, 0.46f));
        internal static Sprite RoundRect   => Get("rrect",  128, (x, y) => RoundedRect(x, y, 0.5f, 0.5f, 0.46f, 0.46f, 0.14f));

        // ── Shapes: return coverage 0..1 for a point in unit space (0..1, y up) ─────
        private static float NoteBodyShape(float x, float y) => RoundedRect(x, y, 0.5f, 0.5f, 0.45f, 0.45f, 0.11f);

        // Beat Saber style flat chevron near the top of the note, pointing down
        private static float ArrowShape(float x, float y)
            => Triangle(x, y, new Vector2(0.20f, 0.74f), new Vector2(0.80f, 0.74f), new Vector2(0.50f, 0.56f));

        private static float DotShape(float x, float y) => Disc(x, y, 0.5f, 0.5f, 0.13f);

        private static float BombShape(float x, float y)
        {
            float body = Disc(x, y, 0.5f, 0.5f, 0.30f);
            // spikes
            float a = Mathf.Atan2(y - 0.5f, x - 0.5f);
            float r = Vector2.Distance(new Vector2(x, y), new Vector2(0.5f, 0.5f));
            float spike = Mathf.Pow(Mathf.Abs(Mathf.Cos(a * 4f)), 18f);
            float spikeR = 0.30f + 0.13f * spike;
            float spikes = Mathf.Clamp01((spikeR - r) * 128f * 0.5f + 0.5f);
            return Mathf.Max(body, spikes);
        }

        private static float GlintShape(float x, float y) => Disc(x, y, 0.40f, 0.60f, 0.07f);

        private static float CogShape(float x, float y)
        {
            float dx = x - 0.5f, dy = y - 0.5f;
            float r = Mathf.Sqrt(dx * dx + dy * dy);
            float a = Mathf.Atan2(dy, dx);
            const int teeth = 8;
            // square-ish teeth
            float t = Mathf.Cos(a * teeth);
            float tooth = Mathf.Clamp01((t - 0.2f) * 6f);
            float outer = Mathf.Lerp(0.34f, 0.46f, tooth);
            float aa = 1f / 128f * 1.5f;
            float fillOuter = Mathf.Clamp01((outer - r) / aa + 0.5f);
            float hole = Mathf.Clamp01((r - 0.15f) / aa + 0.5f);
            return fillOuter * hole;
        }

        // Simple slot-machine glyph: body with three windows
        private static float SlotShape(float x, float y)
        {
            float body = RoundedRect(x, y, 0.47f, 0.5f, 0.34f, 0.38f, 0.08f);
            float windows = 0f;
            for (int i = 0; i < 3; i++)
                windows = Mathf.Max(windows, RoundedRect(x, y, 0.27f + i * 0.2f, 0.54f, 0.075f, 0.14f, 0.03f));
            float lever = Mathf.Max(RoundedRect(x, y, 0.88f, 0.55f, 0.025f, 0.2f, 0.02f), Disc(x, y, 0.88f, 0.78f, 0.06f));
            float baseBar = RoundedRect(x, y, 0.47f, 0.2f, 0.26f, 0.04f, 0.02f);
            return Mathf.Max(Mathf.Max(body * (1f - windows), lever), baseBar * 0f);
        }

        // ── Primitives ────────────────────────────────────────────────────────────
        private const float AA = 1.5f / 128f;

        private static float Disc(float x, float y, float cx, float cy, float r)
        {
            float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
            return Mathf.Clamp01((r - d) / AA + 0.5f);
        }

        private static float RoundedRect(float x, float y, float cx, float cy, float hx, float hy, float rad)
        {
            float qx = Mathf.Abs(x - cx) - (hx - rad);
            float qy = Mathf.Abs(y - cy) - (hy - rad);
            float outside = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude;
            float inside = Mathf.Min(Mathf.Max(qx, qy), 0f);
            float d = outside + inside - rad;
            return Mathf.Clamp01(-d / AA + 0.5f);
        }

        private static float Triangle(float x, float y, Vector2 a, Vector2 b, Vector2 c)
        {
            var p = new Vector2(x, y);
            float d = Mathf.Max(Mathf.Max(EdgeDist(p, a, b), EdgeDist(p, b, c)), EdgeDist(p, c, a));
            return Mathf.Clamp01(-d / AA + 0.5f);
        }

        // signed distance to the line through a→b (positive = outside for clockwise winding)
        private static float EdgeDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 e = b - a;
            Vector2 n = new Vector2(-e.y, e.x).normalized;
            return Vector2.Dot(p - a, n);
        }

        // ── Texture → Sprite ──────────────────────────────────────────────────────
        private static Sprite Get(string key, int size, Func<float, float, float> shape)
        {
            if (_cache.TryGetValue(key, out var s) && s != null) return s;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "ZipSaber_" + key
            };
            var px = new Color32[size * size];
            for (int yy = 0; yy < size; yy++)
                for (int xx = 0; xx < size; xx++)
                {
                    float a = shape((xx + 0.5f) / size, (yy + 0.5f) / size);
                    px[yy * size + xx] = new Color32(255, 255, 255, (byte)Mathf.RoundToInt(Mathf.Clamp01(a) * 255f));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            s.name = "ZipSaber_" + key;
            _cache[key] = s;
            return s;
        }
    }
}

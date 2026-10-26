using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// Triple-bomb result: the whole menu "glitches" for a while.
    ///
    /// Photosensitivity-safe by design: nothing flashes, strobes or changes brightness.
    ///   • the menu screens drift and tilt slightly, driven by smooth Perlin noise (well under 1 Hz)
    ///   • a few text labels at a time get scrambled characters, at most ~2 swaps per second
    ///   • everything eases in at the start and back out at the end, then is restored exactly
    /// Runs on its own object, so leaving ZipSaber doesn't end it early.
    /// </summary>
    internal class GlitchEffect : MonoBehaviour
    {
        private static GlitchEffect _active;

        private float _duration, _t;
        private readonly List<(RectTransform rt, Vector3 pos, Quaternion rot, float seed)> _screens = new List<(RectTransform, Vector3, Quaternion, float)>();
        private readonly List<TMP_Text> _texts = new List<TMP_Text>();
        private readonly Dictionary<TMP_Text, (string original, string scrambled, float until)> _scrambled = new Dictionary<TMP_Text, (string, string, float)>();
        private Transform _root;
        private float _nextScramble, _nextRescan;

        private const float MaxShift = 1.6f;   // UI units
        private const float MaxTilt  = 1.2f;   // degrees
        private const float Speed    = 0.45f;  // noise speed — slow and smooth
        private const string Glyphs  = "#%&@$*+=?/\\<>[]{}01";

        internal static void Run(Component anyMenuView, float seconds)
        {
            if (_active != null) { _active._t = 0f; _active._duration = seconds; return; } // re-trigger extends it
            var canvas = anyMenuView != null ? anyMenuView.GetComponentInParent<Canvas>() : null;
            var root = canvas != null ? canvas.rootCanvas.transform.parent ?? canvas.rootCanvas.transform : null;
            if (root == null) return;

            var go = new GameObject("ZipSaber_Glitch");
            DontDestroyOnLoad(go);
            _active = go.AddComponent<GlitchEffect>();
            _active.Begin(root, seconds);
        }

        private void Begin(Transform root, float seconds)
        {
            _root = root; _duration = seconds; _t = 0f;
            // The menu's screens are the canvases directly under the screen container
            foreach (var c in root.GetComponentsInChildren<Canvas>(false))
            {
                if (!c.isRootCanvas) continue;
                var rt = c.transform as RectTransform;
                if (rt == null) continue;
                _screens.Add((rt, rt.localPosition, rt.localRotation, Random.value * 100f));
                foreach (var cc in rt.GetComponentsInChildren<Canvas>(false))
                {
                    if (cc == c || !(cc.transform is RectTransform crt)) continue;
                    // nested screens (main/top/left/right) also drift, each on its own noise track
                    if (cc.transform.parent == c.transform)
                        _screens.Add((crt, crt.localPosition, crt.localRotation, Random.value * 100f));
                }
            }
            Rescan();
            Plugin.Log?.Info($"[Slots] Glitch for {seconds:0}s on {_screens.Count} screen(s).");
        }

        private void Rescan()
        {
            _texts.Clear();
            if (_root == null) return;
            foreach (var t in _root.GetComponentsInChildren<TMP_Text>(false))
                if (t != null && !string.IsNullOrEmpty(t.text) && t.text.Length >= 3) _texts.Add(t);
        }

        private void Update()
        {
            _t += Time.unscaledDeltaTime;
            float remain = _duration - _t;

            // Ease in over 1.5s, ease out over the last 2s
            float strength = Mathf.Clamp01(_t / 1.5f) * Mathf.Clamp01(remain / 2f);

            for (int i = 0; i < _screens.Count; i++)
            {
                var (rt, pos, rot, seed) = _screens[i];
                if (rt == null) continue;
                float time = Time.unscaledTime * Speed;
                float nx = Mathf.PerlinNoise(seed, time) * 2f - 1f;
                float ny = Mathf.PerlinNoise(seed + 13.7f, time) * 2f - 1f;
                float nr = Mathf.PerlinNoise(seed + 27.1f, time * 0.8f) * 2f - 1f;
                rt.localPosition = pos + new Vector3(nx, ny, 0f) * MaxShift * strength;
                rt.localRotation = rot * Quaternion.Euler(0f, 0f, nr * MaxTilt * strength);
            }

            // Text scramble: a couple of labels at a time, slow swaps
            if (Time.unscaledTime >= _nextRescan) { _nextRescan = Time.unscaledTime + 4f; Rescan(); }
            if (strength > 0.2f && Time.unscaledTime >= _nextScramble && _texts.Count > 0)
            {
                _nextScramble = Time.unscaledTime + 0.5f;
                for (int k = 0; k < 2; k++)
                {
                    var t = _texts[Random.Range(0, _texts.Count)];
                    if (t == null || _scrambled.ContainsKey(t) || t.text.Contains("<")) continue;
                    string original = t.text;
                    string s = Scramble(original);
                    t.text = s;
                    _scrambled[t] = (original, s, Time.unscaledTime + Random.Range(0.6f, 1.4f));
                }
            }
            RestoreDueTexts(force: false);

            if (remain <= 0f) Finish();
        }

        private static string Scramble(string s)
        {
            var chars = s.ToCharArray();
            int swaps = Mathf.Clamp(chars.Length / 5, 1, 3);
            for (int i = 0; i < swaps; i++)
            {
                int idx = Random.Range(0, chars.Length);
                if (char.IsWhiteSpace(chars[idx])) continue;
                chars[idx] = Glyphs[Random.Range(0, Glyphs.Length)];
            }
            return new string(chars);
        }

        private void RestoreDueTexts(bool force)
        {
            if (_scrambled.Count == 0) return;
            var done = new List<TMP_Text>();
            foreach (var kv in _scrambled)
            {
                if (kv.Key == null) { done.Add(kv.Key); continue; }
                if (!force && Time.unscaledTime < kv.Value.until) continue;
                // only put it back if nobody else changed the text meanwhile
                if (kv.Key.text == kv.Value.scrambled) kv.Key.text = kv.Value.original;
                done.Add(kv.Key);
            }
            foreach (var k in done) _scrambled.Remove(k);
        }

        private void Finish()
        {
            foreach (var (rt, pos, rot, _) in _screens)
                if (rt != null) { rt.localPosition = pos; rt.localRotation = rot; }
            RestoreDueTexts(force: true);
            Plugin.Log?.Info("[Slots] Glitch over.");
            _active = null;
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_active == this) _active = null;
        }
    }

    /// <summary>
    /// Fireworks over a view: UI particle bursts with gravity and fade. Soft pops, no screen flashes.
    /// </summary>
    internal class Fireworks : MonoBehaviour
    {
        private class Spark
        {
            internal RectTransform Rt; internal Image Img;
            internal Vector2 Vel; internal float Life, Age; internal Color Col;
        }

        private readonly List<Spark> _sparks = new List<Spark>();
        private RectTransform _layer;
        private SlotAudio _audio;
        private float _duration, _t, _nextBurst;

        private static readonly Color[] Palette =
        {
            new Color(0.95f, 0.30f, 0.33f), new Color(0.30f, 0.55f, 1f), new Color(0.98f, 0.82f, 0.32f),
            new Color(0.50f, 0.90f, 0.45f), new Color(0.80f, 0.55f, 1f),
        };

        internal static void Run(MonoBehaviour host, Transform view, float seconds, SlotAudio audio)
        {
            var layer = UiKit.Rect("ZipSaber_Fireworks", view);
            UiKit.Stretch(layer);
            layer.localPosition += new Vector3(0, 0, -2f);  // in front of the panels
            layer.SetAsLastSibling();
            var fw = layer.gameObject.AddComponent<Fireworks>();
            fw._layer = layer; fw._audio = audio; fw._duration = seconds;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _t += dt;

            if (_t < _duration && _t >= _nextBurst)
            {
                _nextBurst = _t + Random.Range(0.28f, 0.5f);
                Burst();
            }

            for (int i = _sparks.Count - 1; i >= 0; i--)
            {
                var s = _sparks[i];
                s.Age += dt;
                if (s.Rt == null || s.Age >= s.Life)
                {
                    if (s.Rt != null) Destroy(s.Rt.gameObject);
                    _sparks.RemoveAt(i);
                    continue;
                }
                s.Vel += new Vector2(0f, -38f) * dt;   // gravity
                s.Vel *= 1f - 0.9f * dt;                // drag
                s.Rt.anchoredPosition += s.Vel * dt;
                float k = 1f - s.Age / s.Life;
                var c = s.Col; c.a = k * k;
                s.Img.color = c;
                float size = Mathf.Lerp(0.6f, 1.6f, k);
                s.Rt.sizeDelta = new Vector2(size, size);
            }

            if (_t >= _duration && _sparks.Count == 0) Destroy(gameObject);
        }

        private void Burst()
        {
            Rect r = _layer.rect;
            var center = new Vector2(Random.Range(r.xMin * 0.7f, r.xMax * 0.7f), Random.Range(0f, r.yMax * 0.75f));
            var col = Palette[Random.Range(0, Palette.Length)];
            var col2 = Palette[Random.Range(0, Palette.Length)];
            int n = 28;
            float speed = Random.Range(26f, 38f);
            for (int i = 0; i < n; i++)
            {
                float a = (i / (float)n) * Mathf.PI * 2f + Random.Range(-0.08f, 0.08f);
                var img = UiKit.Img(_layer, "Spark", IconFactory.Circle, col);
                var rt = img.rectTransform;
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = center;
                rt.sizeDelta = new Vector2(1.6f, 1.6f);
                _sparks.Add(new Spark
                {
                    Rt = rt, Img = img, Col = (i % 3 == 0) ? col2 : col,
                    Vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * speed * Random.Range(0.75f, 1.05f),
                    Life = Random.Range(1.0f, 1.5f),
                });
            }
            _audio?.FireworkPop();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Slot-machine sound kit. Uses Beat Saber's own clips where they're loaded in the menu
    /// (UI clicks, note cuts, bomb explosions, level cleared) and fills any gaps with small
    /// synthesized sounds, so it always works.
    /// </summary>
    internal class SlotAudio : MonoBehaviour
    {
        // UnityEngine.AudioModule isn't referenced at compile time (not in Libs), so audio types
        // are used through reflection. AudioClip/AudioSource are handled as UnityEngine.Object.
        private static readonly Type TSource = Type.GetType("UnityEngine.AudioSource, UnityEngine.AudioModule");
        private static readonly Type TClip   = Type.GetType("UnityEngine.AudioClip, UnityEngine.AudioModule");
        private static MethodInfo _playOneShot, _clipCreate, _clipSetData;
        private static PropertyInfo _pPitch, _pVolume, _pSpatial, _pPlayOnAwake, _pIgnorePause;

        private Component _sfx;       // one-shots
        private Component _tick;      // reel ticks (pitch varies)
        private readonly Component[] _pool = new Component[4];   // one-shots each get their own source so pitch changes don't bend other sounds
        private int _poolNext;

        private UnityEngine.Object[] _clicks = new UnityEngine.Object[0], _cuts = new UnityEngine.Object[0], _booms = new UnityEngine.Object[0];
        private UnityEngine.Object _cleared;

        // synthesized fallbacks / extras
        private UnityEngine.Object _synthPop;
        private UnityEngine.Object _synthTick, _synthThunk, _synthWin, _synthJackpot, _synthLose, _synthCoin, _synthAlarm;

        private static bool _loggedSources;

        internal static SlotAudio Create(Transform parent)
        {
            var go = new GameObject("ZipSaber_SlotAudio");
            go.transform.SetParent(parent, false);
            var a = go.AddComponent<SlotAudio>();
            a.Init();
            return a;
        }

        private void Init()
        {
            if (TSource == null || TClip == null) { Plugin.Log?.Warn("[Slots] Unity audio module not found - slots will be silent."); return; }
            _playOneShot  = TSource.GetMethod("PlayOneShot", new[] { TClip, typeof(float) });
            _pPitch       = TSource.GetProperty("pitch");
            _pVolume      = TSource.GetProperty("volume");
            _pSpatial     = TSource.GetProperty("spatialBlend");
            _pPlayOnAwake = TSource.GetProperty("playOnAwake");
            _pIgnorePause = TSource.GetProperty("ignoreListenerPause");
            _clipCreate   = TClip.GetMethod("Create", new[] { typeof(string), typeof(int), typeof(int), typeof(int), typeof(bool) });
            _clipSetData  = TClip.GetMethod("SetData", new[] { typeof(float[]), typeof(int) });

            _sfx  = MakeSource(MasterVolume);
            for (int i = 0; i < _pool.Length; i++) _pool[i] = MakeSource(MasterVolume);
            _tick = MakeSource(MasterVolume * 0.5f);

            _clicks  = FindClips("BasicUIAudioManager", "_clickSounds");
            _cuts    = FindClips("NoteCutSoundEffectManager", "_shortCutEffectsAudioClips")
                       .Concat(FindClips("NoteCutSoundEffect", "_goodCutSoundEffectAudioClips")).ToArray();
            _booms   = FindClips("BombExplosionEffect", "_bombExplosionAudioClips");
            _cleared = FindClips("LevelCompletionResultsSoundsPlayer", "_levelClearedAudioClip").FirstOrDefault();

            // Only short game clips are allowed. (The old name-search fallback could grab whole
            // songs/stingers with "cut" or "success" in the name — that was the loud overlapping noise.)
            _clicks = Short(_clicks, 0.5f);
            _cuts   = Short(_cuts, 0.8f);
            _booms  = Short(_booms, 2.5f);
            if (_cleared != null && ClipLength(_cleared) > 4f) _cleared = null;

            _synthTick    = Synth("tick",    0.03f,  (t, i) => Env(t, 0.03f, 0.002f) * Sine(t, 2000f) * 0.3f);
            _synthThunk   = Synth("thunk",   0.16f,  (t, i) => Env(t, 0.16f, 0.003f) * (Sine(t, 140f - 260f * t) * 0.8f + Noise(i) * 0.25f * Mathf.Exp(-t * 40f)));
            _synthCoin    = Synth("coin",    0.22f,  (t, i) => Env(t, 0.22f, 0.002f) * Sine(t, t < 0.07f ? 1320f : 1760f) * 0.45f);
            _synthLose    = Synth("lose",    0.6f,   (t, i) => Env(t, 0.6f, 0.01f) * Tri(t, t < 0.28f ? 392f : 311f) * 0.4f);
            _synthWin     = Synth("win",     0.75f,  (t, i) => Arp(t, new[] { 523.25f, 659.25f, 783.99f, 1046.5f }, 0.12f, 0.75f) * 0.4f);
            _synthJackpot = Synth("jackpot", 1.6f,   (t, i) => Arp(t, new[] { 523.25f, 659.25f, 783.99f, 1046.5f, 1318.5f, 1568f, 2093f, 1568f, 2093f, 2637f }, 0.1f, 1.6f) * 0.42f);
            // soft firework: a low thump with a short sparkly crackle tail
            _synthPop     = Synth("pop",     0.7f,   (t, i) => (Env(t, 0.12f, 0.003f) * Sine(t, 90f - 120f * t) * 0.6f
                                                                 + Noise(i) * 0.18f * Mathf.Exp(-t * 6f) * (Mathf.Sin(t * 230f) > 0.6f ? 1f : 0.15f)) * 0.6f);
            _synthAlarm   = Synth("alarm",   0.3f,   (t, i) => Env(t, 0.3f, 0.005f) * Tri(t, 880f + 220f * Mathf.Sin(t * 60f)) * 0.25f);

            if (!_loggedSources)
            {
                _loggedSources = true;
                Plugin.Log?.Debug($"[Slots] Sounds — clicks:{_clicks.Length} cuts:{_cuts.Length} booms:{_booms.Length} cleared:{(_cleared != null)}");
            }
        }

        private Component MakeSource(float vol)
        {
            var s = gameObject.AddComponent(TSource);
            try
            {
                _pPlayOnAwake?.SetValue(s, false);
                _pSpatial?.SetValue(s, 0f);
                _pVolume?.SetValue(s, vol);
                _pIgnorePause?.SetValue(s, true);
            }
            catch { }
            return s;
        }

        // ── Public cues ───────────────────────────────────────────────────────────
        internal void Lever()
        {
            PlayPooled(_synthCoin, 1f, 0.9f);
            if (_clicks.Length > 0) PlayPooled(Pick(_clicks), 0.8f, 1f);
        }

        /// <summary>A symbol passing the window. speed 0..1 raises the pitch.</summary>
        private float _lastTick;

        internal void Tick(float speed)
        {
            // Three reels tick at ~14/s each; cap the combined rate so it's a rattle, not a roar
            if (Time.unscaledTime - _lastTick < 0.07f) return;
            _lastTick = Time.unscaledTime;
            float pitch = 0.9f + speed * 0.6f + UnityEngine.Random.Range(-0.03f, 0.03f);
            if (_clicks.Length > 0) Play(_tick, Pick(_clicks), pitch, 0.7f);
            else Play(_tick, _synthTick, pitch, 1f);
        }

        internal void ReelStop(int reelIndex)
        {
            float pitch = 1f + reelIndex * 0.08f;
            if (_cuts.Length > 0) PlayPooled(Pick(_cuts), pitch, 0.9f);
            PlayPooled(_synthThunk, 1f + reelIndex * 0.1f, 0.8f);
        }

        internal void FireworkPop() => PlayPooled(_synthPop, UnityEngine.Random.Range(0.85f, 1.2f), 0.55f);

        internal void SmallWin()  => PlayPooled(_synthWin, 1f, 1f);
        internal void Lose()      => PlayPooled(_synthLose, 1f, 0.8f);
        internal void NearMiss()  { PlayPooled(_synthLose, 1.25f, 0.8f); PlayPooled(_synthCoin, 0.7f, 0.6f); }

        internal void BombHit()
        {
            if (_booms.Length > 0) PlayPooled(Pick(_booms), 1f, 0.9f);
            else PlayPooled(_synthThunk, 0.6f, 1f);
        }

        internal void Jackpot()
        {
            PlayPooled(_synthJackpot, 1f, 1f);
            if (_cleared != null) PlayPooled(_cleared, 1f, 0.8f);
        }

        internal void Countdown(int n) => PlayPooled(_synthAlarm, 1f + (5 - n) * 0.08f, 1f);

        internal void FinalBoom()
        {
            if (_booms.Length > 0) PlayPooled(Pick(_booms), 0.8f, 1f);
            PlayPooled(_synthThunk, 0.5f, 1f);
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private void PlayPooled(UnityEngine.Object clip, float pitch, float vol)
        {
            var src = _pool[_poolNext];
            _poolNext = (_poolNext + 1) % _pool.Length;
            Play(src ?? _sfx, clip, pitch, vol);
        }

        private static void Play(Component src, UnityEngine.Object clip, float pitch, float vol)
        {
            if (src == null || clip == null || _playOneShot == null) return;
            try
            {
                _pPitch?.SetValue(src, pitch);
                _playOneShot.Invoke(src, new object[] { clip, vol });
            }
            catch { }
        }

        private static UnityEngine.Object Pick(UnityEngine.Object[] arr) => arr[UnityEngine.Random.Range(0, arr.Length)];

        private static UnityEngine.Object[] FindClips(string typeName, string fieldName)
        {
            var found = new List<UnityEngine.Object>();
            if (TClip == null) return found.ToArray();
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType(typeName); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (type == null) return found.ToArray();
                var f = type.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null) return found.ToArray();
                foreach (var obj in Resources.FindObjectsOfTypeAll(type))
                {
                    var v = f.GetValue(obj);
                    if (v is UnityEngine.Object c && c != null && TClip.IsInstanceOfType(c)) found.Add(c);
                    else if (v is Array arr)
                        foreach (var x in arr) if (x is UnityEngine.Object o && o != null) found.Add(o);
                    if (found.Count > 0) break;
                }
            }
            catch { }
            return found.Distinct().ToArray();
        }

        private static PropertyInfo _pLength;

        private static float ClipLength(UnityEngine.Object clip)
        {
            try
            {
                if (_pLength == null) _pLength = TClip?.GetProperty("length");
                return _pLength != null ? (float)_pLength.GetValue(clip) : 99f;
            }
            catch { return 99f; }
        }

        private static UnityEngine.Object[] Short(UnityEngine.Object[] clips, float maxSeconds)
            => clips.Where(c => c != null && ClipLength(c) <= maxSeconds).ToArray();

        /// <summary>Overall loudness of the slot machine (0..1).</summary>
        private const float MasterVolume = 0.35f;

        // ── Tiny synth ────────────────────────────────────────────────────────────
        private const int Rate = 44100;
        private static readonly System.Random _rng = new System.Random(1234);

        private static UnityEngine.Object Synth(string name, float seconds, Func<float, int, float> f)
        {
            if (_clipCreate == null || _clipSetData == null) return null;
            int n = Mathf.CeilToInt(seconds * Rate);
            var data = new float[n];
            for (int i = 0; i < n; i++) data[i] = Mathf.Clamp(f(i / (float)Rate, i), -1f, 1f);
            try
            {
                var clip = _clipCreate.Invoke(null, new object[] { "ZipSaber_" + name, n, 1, Rate, false }) as UnityEngine.Object;
                _clipSetData.Invoke(clip, new object[] { data, 0 });
                return clip;
            }
            catch { return null; }
        }

        private static float Sine(float t, float hz)   => Mathf.Sin(2f * Mathf.PI * hz * t);
        private static float Square(float t, float hz) => Mathf.Sign(Sine(t, hz)) * 0.6f;
        private static float Tri(float t, float hz)    { float p = Mathf.Repeat(t * hz, 1f); return 4f * Mathf.Abs(p - 0.5f) - 1f; }
        private static float Noise(int i)              { lock (_rng) return (float)(_rng.NextDouble() * 2.0 - 1.0); }

        // attack/decay envelope
        private static float Env(float t, float len, float attack)
            => Mathf.Clamp01(t / attack) * Mathf.Clamp01(1f - t / len) * Mathf.Exp(-t * 3f / len);

        // bell-ish arpeggio: each note rings with a few harmonics
        private static float Arp(float t, float[] notes, float step, float len)
        {
            float v = 0f;
            for (int k = 0; k < notes.Length; k++)
            {
                float start = k * step;
                if (t < start) break;
                float lt = t - start;
                float e = Mathf.Exp(-lt * 6f) * Mathf.Clamp01(lt / 0.004f) * Mathf.Clamp01((len - t) / 0.05f);
                v += e * (Sine(lt, notes[k]) * 0.6f + Sine(lt, notes[k] * 2f) * 0.25f + Sine(lt, notes[k] * 3f) * 0.1f);
            }
            return v * 0.6f;
        }
    }
}

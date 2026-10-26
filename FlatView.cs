using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HMUI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// Makes a ZipSaber view render flat without touching the game's shared, curved menu screens.
    ///
    /// HMUI curved graphics resolve curvature like this:
    ///   1. CurvedCanvasSettings on their nearest Canvas (via a static Canvas→settings cache), else
    ///   2. the root screen canvas (curved, r=140).
    ///
    /// The view controller's own Canvas gets a flat (r=0) CurvedCanvasSettings. Nested canvases —
    /// scroll views, and popups like the Sort-by dropdown that the game re-parents onto the screen
    /// when shown — don't get a component (adding one to an inactive popup fails); instead their
    /// entry in HMUI's static lookup cache is pointed at the view's flat settings. Same result,
    /// works whether the popup is open or not, and nothing is added to the game's objects.
    /// </summary>
    internal static class FlatView
    {
        private const BindingFlags AnyStatic   = BindingFlags.Static   | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static FieldInfo _cacheField, _radiusField;
        private static readonly HashSet<int> _warned = new HashSet<int>();

        /// <summary>Call before BSML parses the view (first DidActivate, before base).</summary>
        internal static void Prepare(Component view)
        {
            if (view == null) return;
            var canvas = view.GetComponent<Canvas>();
            if (canvas == null) return;
            try
            {
                var cs = canvas.GetComponent<CurvedCanvasSettings>() ?? canvas.gameObject.AddComponent<CurvedCanvasSettings>();
                if (cs == null) { WarnOnce(canvas, "could not add curve settings"); return; }
                SetRadiusField(cs, 0f);
                if (canvas.gameObject.activeInHierarchy) { try { cs.SetRadius(0f); } catch { } }
                Cache()?.Remove(canvas);
                Cache()?.Add(canvas, cs);
            }
            catch (Exception ex) { WarnOnce(canvas, ex.Message); }
        }

        /// <summary>Call after BSML has built the view: flattens nested canvases and watches popups.</summary>
        internal static void Finish(MonoBehaviour view)
        {
            if (view == null) return;
            var flat = FlatSettingsOf(view);
            if (flat == null) return;

            var watcher = view.GetComponent<Watcher>() ?? view.gameObject.AddComponent<Watcher>();
            watcher.Flat = flat;
            watcher.ScanNow();
        }

        // ── Watcher ───────────────────────────────────────────────────────────────
        private class Watcher : MonoBehaviour
        {
            internal CurvedCanvasSettings Flat;
            private readonly List<Component> _modals = new List<Component>();
            private readonly HashSet<int> _wasOpen = new HashSet<int>();
            private float _nextScan;

            private void LateUpdate()
            {
                if (Time.unscaledTime < _nextScan) return;
                _nextScan = Time.unscaledTime + 0.1f;
                ScanNow();
            }

            internal void ScanNow()
            {
                if (Flat == null) return;

                foreach (var c in GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "ModalView" && !_modals.Contains(c)) _modals.Add(c);

                Point(this, Flat);
                for (int i = _modals.Count - 1; i >= 0; i--)
                {
                    var m = _modals[i];
                    if (m == null) { _modals.RemoveAt(i); continue; }
                    Point(m, Flat); // modals may live outside our hierarchy once shown

                    // When a popup opens the game adds its pointer raycaster; make sure that and
                    // every graphic re-resolve their curve settings so clicks line up with the flat visuals.
                    bool open = m.gameObject.activeInHierarchy;
                    int id = m.GetInstanceID();
                    if (open && _wasOpen.Add(id)) ResetHelpers(m);
                    else if (!open) _wasOpen.Remove(id);
                }
            }
        }

        /// <summary>Point every canvas under root at the flat settings; re-mesh if anything changed.</summary>
        private static void Point(Component root, CurvedCanvasSettings flat)
        {
            var cache = Cache();
            if (cache == null) return;
            bool changed = false;

            foreach (var canvas in root.GetComponentsInChildren<Canvas>(true))
            {
                if (canvas == null) continue;
                try
                {
                    var own = canvas.GetComponent<CurvedCanvasSettings>();
                    if (own != null)
                    {
                        // Canvas already has its own settings (e.g. our view canvas) — just make sure it's flat
                        if (own.radius != 0f) { SetRadiusField(own, 0f); changed = true; }
                        continue;
                    }
                    if (cache.Contains(canvas) && ReferenceEquals(cache[canvas], flat)) continue;
                    cache[canvas] = flat;
                    changed = true;
                }
                catch (Exception ex) { WarnOnce(canvas, ex.Message); }
            }

            if (changed) { ResetHelpers(root); Remesh(root); }
        }

        // HMUI components (graphics AND the VR pointer raycaster) each hold a CurvedCanvasSettingsHelper
        // that caches the curve settings it found. Once it has cached the curved screen as a fallback it
        // never looks again, so clear them all after re-pointing a canvas.
        private static readonly Dictionary<Type, FieldInfo[]> _helperFields = new Dictionary<Type, FieldInfo[]>();

        private static void ResetHelpers(Component root)
        {
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                foreach (var f in HelperFields(c.GetType()))
                {
                    try { (f.GetValue(c) as CurvedCanvasSettingsHelper)?.Reset(); } catch { }
                }
            }
        }

        private static FieldInfo[] HelperFields(Type t)
        {
            if (_helperFields.TryGetValue(t, out var fields)) return fields;
            var list = new List<FieldInfo>();
            for (var tt = t; tt != null && tt != typeof(MonoBehaviour); tt = tt.BaseType)
                foreach (var f in tt.GetFields(AnyInstance | BindingFlags.DeclaredOnly))
                    if (f.FieldType == typeof(CurvedCanvasSettingsHelper)) list.Add(f);
            fields = list.ToArray();
            _helperFields[t] = fields;
            return fields;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private static CurvedCanvasSettings FlatSettingsOf(Component view)
        {
            var canvas = view.GetComponent<Canvas>();
            var cs = canvas != null ? canvas.GetComponent<CurvedCanvasSettings>() : null;
            if (cs == null) { Prepare(view); cs = canvas != null ? canvas.GetComponent<CurvedCanvasSettings>() : null; }
            return cs;
        }

        private static IDictionary Cache()
        {
            if (_cacheField == null)
                _cacheField = typeof(CurvedCanvasSettingsHelper).GetField("_curvedCanvasCache", AnyStatic);
            return _cacheField?.GetValue(null) as IDictionary;
        }

        private static void SetRadiusField(CurvedCanvasSettings cs, float r)
        {
            if (_radiusField == null)
                _radiusField = typeof(CurvedCanvasSettings).GetField("_radius", AnyInstance);
            _radiusField?.SetValue(cs, r);
        }

        private static void Remesh(Component root)
        {
            foreach (var g in root.GetComponentsInChildren<Graphic>(true))
            {
                if (g == null) continue;
                // Graphics cache their settings until their canvas transform reports a change
                g.transform.hasChanged = true;
                if (g.canvas != null) g.canvas.transform.hasChanged = true;
                if (g is TMP_Text t)
                {
                    t.havePropertiesChanged = true;
                    t.SetAllDirty();
                    if (t.isActiveAndEnabled) t.ForceMeshUpdate();
                }
                else g.SetAllDirty();
            }
        }

        private static void WarnOnce(UnityEngine.Object obj, string msg)
        {
            if (obj != null && _warned.Add(obj.GetInstanceID()))
                Plugin.Log?.Warn($"[FlatView] Could not flatten {obj.name}: {msg}");
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IPA.Loader;

namespace ZipSaber
{
    /// <summary>
    /// Forcibly detaches a mod from the running game, for mods whose own OnDisable/OnExit
    /// doesn't undo what they set up (e.g. BeatLeader applies Harmony patches in OnStart
    /// and installs its menus through those patches, and never unpatches on disable).
    ///
    /// On disable it removes:
    ///   • every Harmony patch (prefix/postfix/transpiler/finalizer) whose patch method lives in the mod's assembly
    ///   • every handler from the mod's assembly subscribed to a static event/delegate in any loaded plugin
    ///     assembly (BSML MainMenuAwaiter, SongCore Loader events, etc.)
    /// and remembers them, so on re-enable anything the mod's own OnEnable/OnStart didn't
    /// re-create is put back exactly as it was.
    ///
    /// Harmony is accessed by reflection so ZipSaber doesn't bind to a specific 0Harmony version.
    /// </summary>
    internal static class ModDetacher
    {
        private const BindingFlags S  = BindingFlags.Static   | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags PI = BindingFlags.Instance | BindingFlags.Public;

        private class SavedPatch
        {
            internal MethodBase Original;
            internal MethodInfo PatchMethod;
            internal string     Kind;     // prefix / postfix / transpiler / finalizer
            internal string     Owner;
            internal int        Priority;
            internal string[]   Before;
            internal string[]   After;
        }

        private class SavedHandler
        {
            internal FieldInfo Field;
            internal Delegate  Handler;
        }

        private class Detached
        {
            internal readonly List<SavedPatch>   Patches  = new List<SavedPatch>();
            internal readonly List<SavedHandler> Handlers = new List<SavedHandler>();
        }

        private static readonly Dictionary<string, Detached> _detached = new Dictionary<string, Detached>(StringComparer.OrdinalIgnoreCase);

        private static readonly (string Field, string Kind)[] PatchKinds =
        {
            ("Prefixes", "prefix"), ("Postfixes", "postfix"), ("Transpilers", "transpiler"), ("Finalizers", "finalizer")
        };

        // ── Harmony via reflection ────────────────────────────────────────────────
        private static bool _hInit;
        private static Type _harmonyT, _harmonyMethodT;
        private static MethodInfo _getAllPatched, _getPatchInfo, _unpatch, _patch;

        private static bool HarmonyReady()
        {
            if (_hInit) return _harmonyT != null;
            _hInit = true;
            try
            {
                var asm = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "0Harmony");
                if (asm == null) { Plugin.Log?.Warn("[ModDetach] 0Harmony not loaded."); return false; }
                _harmonyT       = asm.GetType("HarmonyLib.Harmony");
                _harmonyMethodT = asm.GetType("HarmonyLib.HarmonyMethod");
                _getAllPatched  = _harmonyT.GetMethod("GetAllPatchedMethods", S, null, Type.EmptyTypes, null);
                _getPatchInfo   = _harmonyT.GetMethod("GetPatchInfo", S, null, new[] { typeof(MethodBase) }, null);
                _unpatch        = _harmonyT.GetMethod("Unpatch", PI, null, new[] { typeof(MethodBase), typeof(MethodInfo) }, null);
                _patch          = _harmonyT.GetMethods(PI).FirstOrDefault(m =>
                                      m.Name == "Patch" && m.GetParameters().Length >= 2 &&
                                      m.GetParameters()[0].ParameterType == typeof(MethodBase));
                if (_getAllPatched == null || _getPatchInfo == null || _unpatch == null || _patch == null)
                { Plugin.Log?.Warn("[ModDetach] Harmony API shape not recognised."); _harmonyT = null; return false; }
                return true;
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[ModDetach] Harmony init failed: {ex.Message}"); _harmonyT = null; return false; }
        }

        private static object NewHarmony(string id) => Activator.CreateInstance(_harmonyT, id);

        private static IEnumerable<object> PatchesOf(object patchInfo, string fieldName)
        {
            if (patchInfo == null) return Enumerable.Empty<object>();
            var t = patchInfo.GetType();
            object coll = t.GetField(fieldName, PI)?.GetValue(patchInfo) ?? t.GetProperty(fieldName, PI)?.GetValue(patchInfo);
            return coll is IEnumerable e ? e.Cast<object>() : Enumerable.Empty<object>();
        }

        private static T Get<T>(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name, PI);
            if (f != null) return (T)f.GetValue(o);
            var p = t.GetProperty(name, PI);
            return p != null ? (T)p.GetValue(o) : default;
        }

        // ── Public API ────────────────────────────────────────────────────────────
        /// <summary>Strip the mod's patches and static event handlers. Returns a short summary.</summary>
        internal static string Detach(PluginMetadata meta)
        {
            Assembly asm = meta?.Assembly;
            if (asm == null) return "";
            string key = meta.Id ?? meta.Name;
            if (!_detached.TryGetValue(key, out var rec)) { rec = new Detached(); _detached[key] = rec; }

            int patches = 0, handlers = 0;

            // 1. Harmony patches
            if (HarmonyReady())
            {
                List<MethodBase> originals;
                try { originals = ((IEnumerable)_getAllPatched.Invoke(null, null)).Cast<MethodBase>().ToList(); }
                catch (Exception ex) { originals = new List<MethodBase>(); Plugin.Log?.Warn($"[ModDetach] GetAllPatchedMethods failed: {ex.Message}"); }

                foreach (var original in originals)
                {
                    object info;
                    try { info = _getPatchInfo.Invoke(null, new object[] { original }); } catch { continue; }

                    foreach (var (field, kind) in PatchKinds)
                    {
                        foreach (var p in PatchesOf(info, field).ToList())
                        {
                            MethodInfo pm = Get<MethodInfo>(p, "PatchMethod");
                            if (pm?.DeclaringType == null || pm.DeclaringType.Assembly != asm) continue;
                            string owner = Get<string>(p, "owner") ?? key;
                            try
                            {
                                _unpatch.Invoke(NewHarmony(owner), new object[] { original, pm });
                                rec.Patches.Add(new SavedPatch
                                {
                                    Original = original, PatchMethod = pm, Kind = kind, Owner = owner,
                                    Priority = Get<int>(p, "priority"), Before = Get<string[]>(p, "before"), After = Get<string[]>(p, "after")
                                });
                                patches++;
                            }
                            catch (Exception ex)
                            {
                                Plugin.Log?.Warn($"[ModDetach] Unpatch {original.DeclaringType?.Name}.{original.Name} ({kind}) failed: {Inner(ex).Message}");
                            }
                        }
                    }
                }
            }

            // 2. Static delegate fields in other plugin assemblies (+ BSIPA) holding the mod's handlers
            foreach (var field in StaticDelegateFields(asm))
            {
                Delegate current;
                try { current = field.GetValue(null) as Delegate; } catch { continue; }
                if (current == null) continue;

                var mine = current.GetInvocationList().Where(h => BelongsTo(h, asm)).ToList();
                if (mine.Count == 0) continue;

                Delegate remaining = current;
                foreach (var h in mine) remaining = Delegate.Remove(remaining, h);
                try
                {
                    field.SetValue(null, remaining);
                    foreach (var h in mine) rec.Handlers.Add(new SavedHandler { Field = field, Handler = h });
                    handlers += mine.Count;
                }
                catch (Exception ex) { Plugin.Log?.Warn($"[ModDetach] Could not detach {field.DeclaringType?.Name}.{field.Name}: {ex.Message}"); }
            }

            Plugin.Log?.Info($"[ModDetach] {meta.Name}: removed {patches} Harmony patch(es), {handlers} event handler(s).");
            if (patches == 0 && handlers == 0) return "";
            return $"{patches} patch{(patches == 1 ? "" : "es")}, {handlers} hook{(handlers == 1 ? "" : "s")} removed";
        }

        /// <summary>Re-apply whatever Detach removed that the mod's own enable didn't already restore.</summary>
        internal static void Reattach(PluginMetadata meta)
        {
            string key = meta?.Id ?? meta?.Name;
            if (key == null || !_detached.TryGetValue(key, out var rec)) return;
            _detached.Remove(key);

            int patches = 0, handlers = 0;

            if (rec.Patches.Count > 0 && HarmonyReady())
            {
                foreach (var sp in rec.Patches)
                {
                    try
                    {
                        object info = _getPatchInfo.Invoke(null, new object[] { sp.Original });
                        bool present = PatchKinds.Any(k => PatchesOf(info, k.Field)
                            .Any(p => Get<MethodInfo>(p, "PatchMethod") == sp.PatchMethod));
                        if (present) continue; // the mod's own OnEnable already re-patched it

                        object hm = Activator.CreateInstance(_harmonyMethodT, sp.PatchMethod);
                        _harmonyMethodT.GetField("priority")?.SetValue(hm, sp.Priority);
                        _harmonyMethodT.GetField("before")?.SetValue(hm, sp.Before);
                        _harmonyMethodT.GetField("after")?.SetValue(hm, sp.After);

                        var ps = _patch.GetParameters();
                        var args = new object[ps.Length];
                        args[0] = sp.Original;
                        for (int i = 1; i < ps.Length; i++)
                            args[i] = ps[i].Name == sp.Kind ? hm : (ps[i].HasDefaultValue ? ps[i].DefaultValue : null);
                        _patch.Invoke(NewHarmony(sp.Owner), args);
                        patches++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.Warn($"[ModDetach] Re-patch {sp.Original.DeclaringType?.Name}.{sp.Original.Name} failed: {Inner(ex).Message}");
                    }
                }
            }

            foreach (var sh in rec.Handlers)
            {
                try
                {
                    var current = sh.Field.GetValue(null) as Delegate;
                    bool present = current != null && current.GetInvocationList()
                        .Any(h => h.Method == sh.Handler.Method && ReferenceEquals(h.Target, sh.Handler.Target));
                    if (present) continue;
                    sh.Field.SetValue(null, Delegate.Combine(current, sh.Handler));
                    handlers++;
                }
                catch (Exception ex) { Plugin.Log?.Warn($"[ModDetach] Re-hook {sh.Field.Name} failed: {ex.Message}"); }
            }

            Plugin.Log?.Info($"[ModDetach] {meta.Name}: restored {patches} patch(es), {handlers} handler(s) not re-created by the mod.");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private static bool BelongsTo(Delegate h, Assembly asm)
            => h.Method?.DeclaringType?.Assembly == asm || (h.Target != null && h.Target.GetType().Assembly == asm);

        private static IEnumerable<FieldInfo> StaticDelegateFields(Assembly exclude)
        {
            var asms = new HashSet<Assembly> { typeof(PluginManager).Assembly };
            foreach (var m in PluginManager.EnabledPlugins.Concat(PluginManager.DisabledPlugins))
                if (m.Assembly != null && m.Assembly != exclude) asms.Add(m.Assembly);

            foreach (var a in asms)
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types.Where(t => t != null).ToArray(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.ContainsGenericParameters) continue;
                    FieldInfo[] fields;
                    try { fields = t.GetFields(S | BindingFlags.DeclaredOnly); } catch { continue; }
                    foreach (var f in fields)
                    {
                        if (f.IsLiteral || f.IsInitOnly && !typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        if (!typeof(Delegate).IsAssignableFrom(f.FieldType)) continue;
                        if (f.IsInitOnly) continue; // can't write readonly
                        yield return f;
                    }
                }
            }
        }

        private static Exception Inner(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}

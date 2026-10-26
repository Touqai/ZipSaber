using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using IPA.Loader;
using UnityEngine;

namespace ZipSaber
{
    internal enum ModRuntimeState
    {
        Enabled,    // loaded and running
        Disabled,   // disabled (at launch via Disabled Mods.json, or live by ZipSaber)
        Protected,  // BSIPA, ZipSaber, or something ZipSaber itself needs — never toggleable
        Library,    // bare library manifest, nothing to enable/disable
        Ignored,    // BSIPA refused to load it (wrong game version, missing deps, …)
        NotLoaded   // DLL exists but BSIPA has never seen it (installed this session)
    }

    internal class ToggleResult
    {
        internal bool Success;
        internal string Message = "";
        internal string Detail  = "";
        internal readonly List<string> Changed       = new List<string>();
        internal readonly List<string> NeedsRestart  = new List<string>();
    }

    /// <summary>
    /// Live enable/disable of BSIPA plugins without restarting the game.
    ///
    /// BSIPA's own StateTransitionTransaction only applies changes live for
    /// [Plugin(RuntimeOptions.DynamicInit)] mods; everything else is just written to
    /// "UserData/Disabled Mods.json" and flagged needs-restart. Almost every mod is
    /// SingleStartInit, so this service performs the exact same steps BSIPA performs
    /// for a DynamicInit plugin (runtimeDisabledPlugins / _bsPlugins / PluginLoader.DisabledPlugins
    /// bookkeeping, PluginEnabled/PluginDisabled events, executor Enable/Disable),
    /// but for any plugin. State is also persisted to Disabled Mods.json, so a mod you
    /// disable stays disabled on the next launch until you re-enable it.
    ///
    /// The PluginDisabled/PluginEnabled events are raised with needsRestart=false so
    /// listeners such as SiraUtil turn the mod's Zenject installers off/on; a menu
    /// reload (ReloadMenu) then rebuilds the menu without them.
    /// </summary>
    internal static class ModToggleService
    {
        private const BindingFlags S = BindingFlags.Static   | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static bool _initDone;
        private static bool _ready;
        internal static string InitError { get; private set; }

        private static FieldInfo    _bsPluginsF, _runtimeDisabledF, _loaderDisabledF, _cfgInstanceF, _lockF, _isSelfF;
        private static FieldInfo    _evtEnabledF, _evtDisabledF, _evtAnyF, _evtStateF;
        private static PropertyInfo _execMetaP, _depsP, _cfgIdsP;
        private static MethodInfo   _execEnableM, _execDisableM, _initPluginM, _cfgChangeTxM;

        /// <summary>Mods toggled since the last menu reload (drives the "Reload Menu" banner).</summary>
        internal static readonly HashSet<string> ChangedSinceReload = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        internal static bool Busy { get; private set; }

        // ── Reflection setup ──────────────────────────────────────────────────────
        internal static bool Available => EnsureInit();

        private static bool EnsureInit()
        {
            if (_initDone) return _ready;
            _initDone = true;
            try
            {
                Assembly ipa     = typeof(PluginManager).Assembly;
                Type pm          = typeof(PluginManager);
                Type execType    = ipa.GetType("IPA.Loader.PluginExecutor", true);
                Type loaderType  = ipa.GetType("IPA.Loader.PluginLoader", true);
                Type cfgType     = ipa.GetType("IPA.Loader.DisabledConfig", true);

                _bsPluginsF       = Req(pm.GetField("_bsPlugins", S), "PluginManager._bsPlugins");
                _runtimeDisabledF = Req(pm.GetField("runtimeDisabledPlugins", S), "PluginManager.runtimeDisabledPlugins");
                _lockF            = pm.GetField("commitTransactionLockObject", S);
                _evtEnabledF      = pm.GetField("PluginEnabled", S);
                _evtDisabledF     = pm.GetField("PluginDisabled", S);
                _evtAnyF          = pm.GetField("OnAnyPluginsStateChanged", S);
                _evtStateF        = pm.GetField("OnPluginsStateChanged", S);

                _loaderDisabledF  = Req(loaderType.GetField("DisabledPlugins", S), "PluginLoader.DisabledPlugins");
                _initPluginM      = Req(loaderType.GetMethod("InitPlugin", S, null,
                                        new[] { typeof(PluginMetadata), typeof(IEnumerable<PluginMetadata>) }, null),
                                        "PluginLoader.InitPlugin");

                _execMetaP        = Req(execType.GetProperty("Metadata", I), "PluginExecutor.Metadata");
                _execEnableM      = Req(execType.GetMethod("Enable", I, null, Type.EmptyTypes, null), "PluginExecutor.Enable");
                _execDisableM     = Req(execType.GetMethod("Disable", I, null, Type.EmptyTypes, null), "PluginExecutor.Disable");

                _cfgInstanceF     = Req(cfgType.GetField("Instance", S), "DisabledConfig.Instance");
                _cfgIdsP          = Req(cfgType.GetProperty("DisabledModIds", I), "DisabledConfig.DisabledModIds");
                _cfgChangeTxM     = cfgType.GetMethod("ChangeTransaction", I);

                _depsP            = typeof(PluginMetadata).GetProperty("Dependencies", I);
                _isSelfF          = typeof(PluginMetadata).GetField("IsSelf", I);

                _ready = true;
                Plugin.Log?.Info("[ModToggle] BSIPA runtime toggle hooks ready.");
            }
            catch (Exception ex)
            {
                InitError = ex.Message;
                Plugin.Log?.Error($"[ModToggle] Init failed — live toggling unavailable: {ex.Message}");
                _ready = false;
            }
            return _ready;
        }

        private static T Req<T>(T member, string name) where T : class
            => member ?? throw new MissingMemberException($"BSIPA member not found: {name}");

        // ── Lookups ───────────────────────────────────────────────────────────────
        private static IList  BsPlugins       => (IList)_bsPluginsF.GetValue(null);
        private static object RuntimeDisabled => _runtimeDisabledF.GetValue(null);           // HashSet<PluginExecutor>
        private static List<PluginMetadata> LoaderDisabled => (List<PluginMetadata>)_loaderDisabledF.GetValue(null);
        private static object LockObj => _lockF?.GetValue(null) ?? typeof(ModToggleService);

        private static PluginMetadata ExecMeta(object exec) => (PluginMetadata)_execMetaP.GetValue(exec);

        private static IEnumerable<PluginMetadata> AllKnown()
            => PluginManager.EnabledPlugins
                .Concat(PluginManager.DisabledPlugins)
                .Concat(PluginManager.IgnoredPlugins.Keys)
                .Distinct();

        private static string Key(PluginMetadata m) => m.Id ?? m.Name;

        internal static PluginMetadata FindMeta(ModInfo mod)
        {
            if (mod == null || !Available) return null;
            foreach (var m in AllKnown())
            {
                if (!string.IsNullOrEmpty(m.Id) && m.Id.Equals(mod.Id, StringComparison.OrdinalIgnoreCase)) return m;
                try
                {
                    if (m.File != null && !string.IsNullOrEmpty(mod.DllPath) &&
                        string.Equals(m.File.FullName, System.IO.Path.GetFullPath(mod.DllPath), StringComparison.OrdinalIgnoreCase))
                        return m;
                }
                catch { }
            }
            return null;
        }

        private static PluginMetadata FindById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return AllKnown().FirstOrDefault(m =>
                (m.Id ?? "").Equals(id, StringComparison.OrdinalIgnoreCase) ||
                (m.Name ?? "").Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Direct dependencies: BSIPA's resolved set, merged with the manifest dependsOn list.</summary>
        private static HashSet<PluginMetadata> DirectDeps(PluginMetadata m)
        {
            var result = new HashSet<PluginMetadata>();
            try
            {
                if (_depsP?.GetValue(m) is IEnumerable<PluginMetadata> deps)
                    foreach (var d in deps) if (d != null) result.Add(d);
            }
            catch { }

            var info = ModRegistry.GetAllMods(Plugin.GetPluginsPath())
                .FirstOrDefault(x => x.Id.Equals(Key(m), StringComparison.OrdinalIgnoreCase));
            if (info != null)
                foreach (string id in info.DependsOn)
                {
                    var d = FindById(id);
                    if (d != null && d != m) result.Add(d);
                }
            return result;
        }

        private static HashSet<PluginMetadata> TransitiveDeps(PluginMetadata m)
        {
            var seen = new HashSet<PluginMetadata>();
            var stack = new Stack<PluginMetadata>(DirectDeps(m));
            while (stack.Count > 0)
            {
                var d = stack.Pop();
                if (!seen.Add(d)) continue;
                foreach (var dd in DirectDeps(d)) if (!seen.Contains(dd)) stack.Push(dd);
            }
            seen.Remove(m);
            return seen;
        }

        private static HashSet<PluginMetadata> _protected;
        private static bool IsProtected(PluginMetadata m)
        {
            if (_protected == null)
            {
                _protected = new HashSet<PluginMetadata>();
                var self = PluginManager.GetPluginFromId("ZipSaber");
                if (self != null) { _protected.Add(self); _protected.UnionWith(TransitiveDeps(self)); }
            }
            if (_protected.Contains(m)) return true;
            if ((m.Id ?? "").Equals("BSIPA", StringComparison.OrdinalIgnoreCase)) return true;
            try { if (_isSelfF != null && (bool)_isSelfF.GetValue(m)) return true; } catch { }
            return false;
        }

        internal static ModRuntimeState GetState(ModInfo mod)
        {
            if (!Available) return ModRuntimeState.NotLoaded;
            var m = FindMeta(mod);
            if (m == null) return ModRuntimeState.NotLoaded;
            if (PluginManager.IgnoredPlugins.ContainsKey(m)) return ModRuntimeState.Ignored;
            if (IsProtected(m)) return ModRuntimeState.Protected;
            if (m.IsBare) return ModRuntimeState.Library;
            return PluginManager.IsEnabled(m) ? ModRuntimeState.Enabled : ModRuntimeState.Disabled;
        }

        /// <summary>Enabled mods that would also have to be disabled along with this one.</summary>
        internal static List<string> GetEnabledDependents(ModInfo mod)
        {
            var m = FindMeta(mod);
            if (m == null) return new List<string>();
            return EnabledDependents(m).Select(d => d.Name ?? d.Id).ToList();
        }

        private static List<PluginMetadata> EnabledDependents(PluginMetadata target)
            => PluginManager.EnabledPlugins.Where(p => p != target && TransitiveDeps(p).Contains(target)).ToList();

        // ── Disable ───────────────────────────────────────────────────────────────
        internal static async Task<ToggleResult> DisableAsync(ModInfo mod)
        {
            var res = new ToggleResult();
            if (!Available) { res.Message = "Live toggling unavailable: " + InitError; return res; }
            if (Busy) { res.Message = "Another toggle is still running."; return res; }

            var target = FindMeta(mod);
            if (target == null) { res.Message = $"{mod.DisplayLabel} isn't loaded by BSIPA yet — restart once first."; return res; }
            if (IsProtected(target)) { res.Message = $"{mod.DisplayLabel} is required by ZipSaber/BSIPA."; return res; }
            if (!PluginManager.IsEnabled(target)) { res.Success = true; res.Message = $"{mod.DisplayLabel} is already disabled."; return res; }

            // Dependents first, deepest first (a dependent always has a strictly larger dep closure)
            var order = EnabledDependents(target)
                .OrderByDescending(p => TransitiveDeps(p).Count)
                .ToList();
            order.Add(target);

            var prot = order.FirstOrDefault(IsProtected);
            if (prot != null) { res.Message = $"Can't disable: {prot.Name} depends on it and is required by ZipSaber."; return res; }

            Busy = true;
            try
            {
                foreach (var m in order)
                {
                    object exec = null;
                    lock (LockObj)
                    {
                        foreach (var e in BsPlugins) if (ExecMeta(e) == m) { exec = e; break; }
                        if (exec != null)
                        {
                            BsPlugins.Remove(exec);
                            RuntimeDisabled.GetType().GetMethod("Add").Invoke(RuntimeDisabled, new[] { exec });
                        }
                        if (!LoaderDisabled.Contains(m)) LoaderDisabled.Add(m);
                    }
                    PersistDisabled(m, true);
                    RaiseEvent(_evtDisabledF, m, false);

                    if (exec != null)
                    {
                        try
                        {
                            var t = _execDisableM.Invoke(exec, null) as Task;
                            if (t != null) await t;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.Warn($"[ModToggle] {m.Name} threw in its OnDisable: {Unwrap(ex).Message}");
                        }
                    }

                    // Strip anything the mod's own OnDisable/OnExit left behind (Harmony patches, static event hooks)
                    string detached = "";
                    try { detached = ModDetacher.Detach(m); }
                    catch (Exception ex) { Plugin.Log?.Warn($"[ModToggle] Detach of {m.Name} failed: {ex.Message}"); }
                    if (m == target && detached.Length > 0) res.Detail = detached;

                    res.Changed.Add(m.Name ?? m.Id);
                    ChangedSinceReload.Add(Key(m));
                    Plugin.Log?.Info($"[ModToggle] Disabled {m.Name} (live).");
                }

                RaiseStateChanged(Enumerable.Empty<PluginMetadata>(), order);
                res.Success = true;
                res.Message = order.Count == 1
                    ? $"Disabled {target.Name}."
                    : $"Disabled {target.Name} + {order.Count - 1} dependent mod{(order.Count == 2 ? "" : "s")}.";
                if (!string.IsNullOrEmpty(res.Detail)) res.Message += $" ({res.Detail})";
            }
            catch (Exception ex)
            {
                res.Message = "Disable failed: " + Unwrap(ex).Message;
                Plugin.Log?.Error($"[ModToggle] {res.Message}\n{ex}");
            }
            finally { Busy = false; }
            return res;
        }

        // ── Enable ────────────────────────────────────────────────────────────────
        internal static async Task<ToggleResult> EnableAsync(ModInfo mod)
        {
            var res = new ToggleResult();
            if (!Available) { res.Message = "Live toggling unavailable: " + InitError; return res; }
            if (Busy) { res.Message = "Another toggle is still running."; return res; }

            var target = FindMeta(mod);
            if (target == null) { res.Message = $"{mod.DisplayLabel} isn't loaded by BSIPA yet — restart once first."; return res; }
            if (PluginManager.IgnoredPlugins.TryGetValue(target, out var why))
            { res.Message = $"BSIPA ignored {target.Name}: {why.ReasonText ?? why.Reason.ToString()}"; return res; }
            if (PluginManager.IsEnabled(target)) { res.Success = true; res.Message = $"{target.Name} is already enabled."; return res; }

            // Disabled dependencies first (post-order DFS)
            var order = new List<PluginMetadata>();
            var visiting = new HashSet<PluginMetadata>();
            string blocker = null;
            void Visit(PluginMetadata m)
            {
                if (order.Contains(m) || !visiting.Add(m)) return;
                foreach (var d in DirectDeps(m))
                {
                    if (PluginManager.IgnoredPlugins.ContainsKey(d)) { blocker ??= $"{d.Name} (ignored by BSIPA)"; continue; }
                    if (PluginManager.IsDisabled(d)) Visit(d); // bare libs are neither enabled nor disabled — treat as satisfied
                }
                order.Add(m);
            }
            Visit(target);
            if (blocker != null) { res.Message = $"Can't enable {target.Name}: needs {blocker}."; return res; }

            Busy = true;
            try
            {
                foreach (var m in order)
                {
                    object exec = null;
                    lock (LockObj)
                    {
                        var rd = RuntimeDisabled;
                        foreach (var e in (IEnumerable)rd) if (ExecMeta(e) == m) { exec = e; break; }
                        if (exec != null) rd.GetType().GetMethod("Remove").Invoke(rd, new[] { exec });
                    }

                    bool freshLoad = false;
                    if (exec == null)
                    {
                        // Disabled since launch → never constructed. Load it now, like BSIPA does for DynamicInit.
                        try
                        {
                            exec = _initPluginM.Invoke(null, new object[] { m, PluginManager.EnabledPlugins.ToList() });
                            freshLoad = exec != null;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log?.Warn($"[ModToggle] Live load of {m.Name} failed: {Unwrap(ex).Message}");
                        }
                    }

                    PersistDisabled(m, false);

                    if (exec == null)
                    {
                        // Couldn't load live — it's re-enabled for next launch at least.
                        res.NeedsRestart.Add(m.Name ?? m.Id);
                        continue;
                    }

                    lock (LockObj)
                    {
                        LoaderDisabled.Remove(m);
                        if (!BsPlugins.Contains(exec)) BsPlugins.Add(exec);
                    }
                    RaiseEvent(_evtEnabledF, m, false);

                    try
                    {
                        var t = _execEnableM.Invoke(exec, null) as Task;
                        if (t != null) await t;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.Warn($"[ModToggle] {m.Name} threw in its OnEnable: {Unwrap(ex).Message}");
                    }

                    // Put back patches/hooks ZipSaber stripped that the mod's OnEnable didn't recreate
                    if (!freshLoad)
                    {
                        try { ModDetacher.Reattach(m); }
                        catch (Exception ex) { Plugin.Log?.Warn($"[ModToggle] Reattach of {m.Name} failed: {ex.Message}"); }
                    }

                    res.Changed.Add(m.Name ?? m.Id);
                    ChangedSinceReload.Add(Key(m));
                    Plugin.Log?.Info($"[ModToggle] Enabled {m.Name} ({(freshLoad ? "loaded live" : "resumed")}).");
                }

                RaiseStateChanged(order, Enumerable.Empty<PluginMetadata>());
                res.Success = res.Changed.Count > 0;
                var parts = new List<string>();
                if (res.Changed.Count > 0)
                    parts.Add(res.Changed.Count == 1 ? $"Enabled {res.Changed[0]}." : $"Enabled {target.Name} + {res.Changed.Count - 1} dependenc{(res.Changed.Count == 2 ? "y" : "ies")}.");
                if (res.NeedsRestart.Count > 0)
                    parts.Add($"Will load next launch: {string.Join(", ", res.NeedsRestart)}.");
                res.Message = string.Join(" ", parts);
            }
            catch (Exception ex)
            {
                res.Message = "Enable failed: " + Unwrap(ex).Message;
                Plugin.Log?.Error($"[ModToggle] {res.Message}\n{ex}");
            }
            finally { Busy = false; }
            return res;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────
        private static void PersistDisabled(PluginMetadata m, bool disabled)
        {
            try
            {
                object cfg = _cfgInstanceF.GetValue(null);
                if (cfg == null) return;
                IDisposable tx = null;
                try { tx = _cfgChangeTxM?.Invoke(cfg, null) as IDisposable; } catch { }
                try
                {
                    var ids = (HashSet<string>)_cfgIdsP.GetValue(cfg);
                    if (ids == null) return;
                    if (disabled) ids.Add(Key(m)); else { ids.Remove(Key(m)); if (m.Id != null) ids.Remove(m.Name); }
                }
                finally { tx?.Dispose(); } // disposing the change transaction saves Disabled Mods.json
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[ModToggle] Could not persist state for {m.Name}: {ex.Message}"); }
        }

        private static void RaiseEvent(FieldInfo evtField, PluginMetadata m, bool needsRestart)
        {
            if (!(evtField?.GetValue(null) is Delegate d)) return;
            foreach (var h in d.GetInvocationList())
            {
                try { h.DynamicInvoke(m, needsRestart); }
                catch (Exception ex) { Plugin.Log?.Warn($"[ModToggle] Listener {h.Method.DeclaringType?.Name}.{h.Method.Name} threw: {Unwrap(ex).Message}"); }
            }
        }

        private static void RaiseStateChanged(IEnumerable<PluginMetadata> enabled, IEnumerable<PluginMetadata> disabled)
        {
            var done = Task.CompletedTask;
            try
            {
                if (_evtAnyF?.GetValue(null) is Delegate any)
                    foreach (var h in any.GetInvocationList())
                        try { h.DynamicInvoke(done, enabled, disabled); } catch (Exception ex) { Plugin.Log?.Warn($"[ModToggle] State listener threw: {Unwrap(ex).Message}"); }
                if (_evtStateF?.GetValue(null) is Delegate st)
                    foreach (var h in st.GetInvocationList())
                        Plugin.Log?.Debug($"[ModToggle] OnPluginsStateChanged listener: {h.Method.DeclaringType?.FullName}.{h.Method.Name}");
                if (_evtStateF?.GetValue(null) is Delegate st2)
                    foreach (var h in st2.GetInvocationList())
                        try { h.DynamicInvoke(done); } catch (Exception ex) { Plugin.Log?.Warn($"[ModToggle] State listener threw: {Unwrap(ex).Message}"); }
            }
            catch { }
        }

        private static Exception Unwrap(Exception ex)
        {
            while ((ex is TargetInvocationException || ex is AggregateException) && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        /// <summary>
        /// Soft-reloads the menu (same thing the game does when you hit OK in its settings),
        /// so Zenject/SiraUtil bindings and menu UI are rebuilt with the new mod set.
        /// No process restart.
        /// </summary>
        internal static bool ReloadMenu()
        {
            try
            {
                Type helperType = Type.GetType("MenuTransitionsHelper, Main");
                if (helperType == null) { Plugin.Log?.Error("[ModToggle] MenuTransitionsHelper type not found."); return false; }
                var helper = Resources.FindObjectsOfTypeAll(helperType).FirstOrDefault();
                if (helper == null) { Plugin.Log?.Error("[ModToggle] MenuTransitionsHelper instance not found."); return false; }
                var mi = helperType.GetMethod("RestartGame", I);
                if (mi == null) { Plugin.Log?.Error("[ModToggle] RestartGame not found."); return false; }
                ChangedSinceReload.Clear();
                mi.Invoke(helper, new object[mi.GetParameters().Length]);
                Plugin.Log?.Info("[ModToggle] Menu reload requested.");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error($"[ModToggle] Menu reload failed: {Unwrap(ex).Message}");
                return false;
            }
        }
    }
}

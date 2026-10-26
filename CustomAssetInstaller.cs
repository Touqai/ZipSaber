using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using BeatSaberMarkupLanguage.FloatingScreen;
using TMPro;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Drag-and-drop install for custom cosmetics: sabers, notes, platforms, avatars, walls.
    /// Accepts the files directly or inside a .zip (as long as the zip isn't a map).
    /// </summary>
    internal static class CustomAssetInstaller
    {
        private class Kind
        {
            internal string Folder, Singular, Plural, Hint;
        }

        private static readonly Kind Sabers    = new Kind { Folder = "CustomSabers",    Singular = "saber",    Plural = "sabers",    Hint = "Open Saber Factory and hit Reload to see it." };
        private static readonly Kind Notes     = new Kind { Folder = "CustomNotes",     Singular = "note",     Plural = "notes",     Hint = "CustomNotes may need a restart to list it." };
        private static readonly Kind Platforms = new Kind { Folder = "CustomPlatforms", Singular = "platform", Plural = "platforms", Hint = "CustomPlatforms may need a restart to list it." };
        private static readonly Kind Avatars   = new Kind { Folder = "CustomAvatars",   Singular = "avatar",   Plural = "avatars",   Hint = "Reload avatars in CustomAvatars' menu." };
        private static readonly Kind Walls     = new Kind { Folder = "CustomWalls",     Singular = "wall",     Plural = "walls",     Hint = "CustomWalls may need a restart to list it." };

        private static readonly Dictionary<string, Kind> ByExt = new Dictionary<string, Kind>(StringComparer.OrdinalIgnoreCase)
        {
            { ".saber", Sabers }, { ".whacker", Sabers },
            { ".bloq", Notes },   { ".note", Notes },
            { ".plat", Platforms },
            { ".avatar", Avatars },
            { ".pixie", Walls },  { ".box", Walls },
        };

        internal static bool IsAssetFile(string path) => ByExt.ContainsKey(Path.GetExtension(path) ?? "");

        /// <summary>True if the zip holds cosmetics and isn't a beatmap.</summary>
        internal static bool IsAssetZip(string zipPath)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    bool hasInfo = zip.Entries.Any(e => e.Name.Equals("info.dat", StringComparison.OrdinalIgnoreCase));
                    bool hasAsset = zip.Entries.Any(e => IsAssetFile(e.Name));
                    return hasAsset && !hasInfo;
                }
            }
            catch { return false; }
        }

        /// <summary>Install loose files and asset zips. Safe to call from a background thread.</summary>
        internal static void Install(List<string> files, List<string> zips)
        {
            string root = GameRoot();
            if (root == null) { Plugin.Log?.Error("[Assets] Game folder unknown."); return; }

            var installed = new List<(Kind kind, string name)>();
            var errors = new List<string>();

            foreach (var f in files)
            {
                try
                {
                    var kind = ByExt[Path.GetExtension(f)];
                    string dest = UniqueTarget(Path.Combine(root, kind.Folder), Path.GetFileName(f), new FileInfo(f).Length, out bool same);
                    if (!same) File.Copy(f, dest, overwrite: false);
                    installed.Add((kind, Path.GetFileNameWithoutExtension(f)));
                    Plugin.Log?.Info($"[Assets] {(same ? "Already had" : "Installed")} {Path.GetFileName(f)} -> {kind.Folder}");
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(f)); Plugin.Log?.Error($"[Assets] {Path.GetFileName(f)}: {ex.Message}"); }
            }

            foreach (var z in zips)
            {
                try
                {
                    using (var zip = ZipFile.OpenRead(z))
                    {
                        foreach (var e in zip.Entries.Where(e => IsAssetFile(e.Name)))
                        {
                            var kind = ByExt[Path.GetExtension(e.Name)];
                            string dest = UniqueTarget(Path.Combine(root, kind.Folder), e.Name, e.Length, out bool same);
                            if (!same) e.ExtractToFile(dest, overwrite: false);
                            installed.Add((kind, Path.GetFileNameWithoutExtension(e.Name)));
                            Plugin.Log?.Info($"[Assets] {(same ? "Already had" : "Installed")} {e.Name} (from {Path.GetFileName(z)}) -> {kind.Folder}");
                        }
                    }
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(z)); Plugin.Log?.Error($"[Assets] {Path.GetFileName(z)}: {ex.Message}"); }
            }

            if (installed.Count == 0 && errors.Count == 0) return;
            string message = Summary(installed, errors);
            MainThreadDispatcher.Enqueue(() => Toast.Show(message));
        }

        private static string Summary(List<(Kind kind, string name)> installed, List<string> errors)
        {
            var lines = new List<string>();
            foreach (var g in installed.GroupBy(i => i.kind))
            {
                var names = g.Select(x => x.name).ToList();
                string what = names.Count == 1 ? $"<b>{Esc(names[0])}</b>" : $"{names.Count} {g.Key.Plural}";
                lines.Add($"Installed {what} to {g.Key.Folder}. <color=#9A9AA6>{g.Key.Hint}</color>");
            }
            if (errors.Count > 0) lines.Add($"<color=#FF6B6B>Couldn't install: {Esc(string.Join(", ", errors))}</color>");
            return string.Join("\n", lines);
        }

        private static string Esc(string s) => "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";

        /// <summary>Destination path; if a same-named file exists and is the same size it's treated as already installed.</summary>
        private static string UniqueTarget(string folder, string fileName, long size, out bool alreadyThere)
        {
            Directory.CreateDirectory(folder);
            alreadyThere = false;
            string dest = Path.Combine(folder, fileName);
            if (!File.Exists(dest)) return dest;
            if (new FileInfo(dest).Length == size) { alreadyThere = true; return dest; }
            string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
            for (int n = 2; n < 100; n++)
            {
                string alt = Path.Combine(folder, $"{stem} ({n}){ext}");
                if (!File.Exists(alt)) return alt;
            }
            return Path.Combine(folder, $"{stem} ({Guid.NewGuid().ToString("N").Substring(0, 6)}){ext}");
        }

        private static string GameRoot()
        {
            string plugins = Plugin.GetPluginsPath();
            return string.IsNullOrEmpty(plugins) ? null : Path.GetDirectoryName(plugins.TrimEnd('\\', '/'));
        }
    }

    /// <summary>Small flat notification that floats in front of the player for a few seconds.</summary>
    internal static class Toast
    {
        private static FloatingScreen _screen;
        private static TextMeshProUGUI _text;
        private static ToastTimer _timer;

        internal static void Show(string message, float seconds = 7f)
        {
            try
            {
                if (_screen == null)
                {
                    _screen = FloatingScreen.CreateFloatingScreen(new Vector2(110f, 22f), false,
                        new Vector3(0f, 2.55f, 2.3f), Quaternion.Euler(-12f, 0f, 0f), 0f, false);
                    UnityEngine.Object.DontDestroyOnLoad(_screen.gameObject);

                    var bg = UiKit.Img(_screen.transform, "Bg", UiKit.RoundSprite, new Color(0.06f, 0.06f, 0.09f, 0.96f), sliced: true);
                    UiKit.Stretch(bg.rectTransform);
                    var bar = UiKit.Img(bg.transform, "Accent", UiKit.RoundSprite, Theme.Accent, sliced: true);
                    bar.rectTransform.anchorMin = new Vector2(0, 0); bar.rectTransform.anchorMax = new Vector2(0, 1);
                    bar.rectTransform.offsetMin = new Vector2(1.5f, 2f); bar.rectTransform.offsetMax = new Vector2(2.5f, -2f);

                    var tag = UiKit.Text(bg.transform, "Tag", "ZIPSABER", 2.4f, Theme.Accent, TextAlignmentOptions.TopLeft);
                    tag.fontStyle = FontStyles.Bold;
                    tag.rectTransform.anchorMin = new Vector2(0, 1); tag.rectTransform.anchorMax = new Vector2(1, 1);
                    tag.rectTransform.offsetMin = new Vector2(5f, -5f); tag.rectTransform.offsetMax = new Vector2(-3f, -1.5f);

                    _text = UiKit.Text(bg.transform, "Msg", "", 3f, new Color(0.9f, 0.9f, 0.94f), TextAlignmentOptions.TopLeft);
                    _text.enableWordWrapping = true;
                    _text.rectTransform.anchorMin = Vector2.zero; _text.rectTransform.anchorMax = Vector2.one;
                    _text.rectTransform.offsetMin = new Vector2(5f, 2f); _text.rectTransform.offsetMax = new Vector2(-3f, -5.5f);

                    _timer = _screen.gameObject.AddComponent<ToastTimer>();
                }
                _text.text = message;
                _screen.gameObject.SetActive(true);
                _timer.HideAt = Time.unscaledTime + seconds;
            }
            catch (Exception ex) { Plugin.Log?.Warn($"[Toast] {ex.Message}"); }
        }

        private class ToastTimer : MonoBehaviour
        {
            internal float HideAt;
            private void Update() { if (Time.unscaledTime >= HideAt) gameObject.SetActive(false); }
        }
    }
}

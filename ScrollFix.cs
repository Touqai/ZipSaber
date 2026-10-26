using System.Collections;
using System.Reflection;
using HMUI;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// BSML's scroll-view only re-measures its content when the scroll view itself is resized,
    /// not when the list inside grows or shrinks. After rebuilding a list (sorting, filtering,
    /// toggling) the scroll limits go stale: you can't scroll to the top/bottom and rows end up
    /// half-hidden. This re-measures after the layout settles and optionally jumps to the top.
    /// </summary>
    internal static class ScrollFix
    {
        internal static void Refresh(MonoBehaviour host, GameObject container, bool toTop)
        {
            if (host == null || container == null || !host.isActiveAndEnabled) return;
            host.StartCoroutine(Run(container, toTop));
        }

        private static IEnumerator Run(GameObject container, bool toTop)
        {
            yield return null;                       // let destroyed rows go and new ones lay out
            if (container == null) yield break;
            var rt = (RectTransform)container.transform;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);
            yield return null;
            if (container == null) yield break;
            LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

            var sv = container.GetComponentInParent<ScrollView>();
            if (sv == null) yield break;
            try
            {
                // These members are non-public in the game's HMUI, so go through reflection
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var t = typeof(ScrollView);
                t.GetMethod("SetContentSize", F, null, new[] { typeof(float) }, null)?.Invoke(sv, new object[] { rt.rect.height });
                if (toTop) sv.ScrollTo(0f, false);
                else
                {
                    float content = (t.GetProperty("contentSize", F)?.GetValue(sv) as float?) ?? rt.rect.height;
                    float page    = (t.GetProperty("scrollPageSize", F)?.GetValue(sv) as float?) ?? 0f;
                    float max = Mathf.Max(0f, content - page);
                    sv.ScrollTo(Mathf.Clamp(sv.position, 0f, max), false);
                }
                t.GetMethod("RefreshButtons", F, null, System.Type.EmptyTypes, null)?.Invoke(sv, null);
            }
            catch (System.Exception ex) { Plugin.Log?.Debug($"[ScrollFix] {ex.Message}"); }
        }
    }
}

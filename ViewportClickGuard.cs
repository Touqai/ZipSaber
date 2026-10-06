using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace ZipSaber
{
    /// <summary>
    /// Beat Saber's VR raycaster ignores scroll-view masks, so rows scrolled out of view
    /// (hidden behind the header/toolbar) can still be clicked. This sits on a list container
    /// and turns off raycasting/interactivity for any row not fully inside the visible viewport.
    /// </summary>
    internal class ViewportClickGuard : MonoBehaviour
    {
        private RectTransform _viewport;
        private readonly HashSet<Graphic>    _mutedGraphics  = new HashSet<Graphic>();
        private readonly HashSet<Selectable> _mutedSelectables = new HashSet<Selectable>();
        private readonly Vector3[] _corners = new Vector3[4];
        private readonly Dictionary<Transform, bool> _state = new Dictionary<Transform, bool>();

        internal static void Attach(GameObject container)
        {
            if (container == null || container.GetComponent<ViewportClickGuard>() != null) return;
            container.AddComponent<ViewportClickGuard>();
        }

        private void Start() => _viewport = FindViewport();

        private RectTransform FindViewport()
        {
            // Nearest ancestor that clips its children = the scroll viewport
            Transform t = transform.parent;
            while (t != null)
            {
                if (t.GetComponent<RectMask2D>() != null || t.GetComponent<Mask>() != null)
                    return t as RectTransform;
                t = t.parent;
            }
            return transform.parent as RectTransform;
        }

        private void LateUpdate()
        {
            if (_viewport == null) { _viewport = FindViewport(); if (_viewport == null) return; }
            Rect vp = _viewport.rect;

            for (int i = 0; i < transform.childCount; i++)
            {
                var row = transform.GetChild(i) as RectTransform;
                if (row == null || !row.gameObject.activeInHierarchy) continue;

                row.GetWorldCorners(_corners);
                float yMin = _viewport.InverseTransformPoint(_corners[0]).y;
                float yMax = _viewport.InverseTransformPoint(_corners[1]).y;
                const float tol = 0.5f;
                bool visible = yMin >= vp.yMin - tol && yMax <= vp.yMax + tol;
                if (_state.TryGetValue(row, out bool was) && was == visible) continue;
                _state[row] = visible;
                SetRowInteractive(row, visible);
            }
        }

        private void SetRowInteractive(Transform row, bool on)
        {
            foreach (var g in row.GetComponentsInChildren<Graphic>(true))
            {
                if (on)
                {
                    if (_mutedGraphics.Remove(g)) g.raycastTarget = true;
                }
                else if (g.raycastTarget)
                {
                    g.raycastTarget = false;
                    _mutedGraphics.Add(g);
                }
            }
            foreach (var s in row.GetComponentsInChildren<Selectable>(true))
            {
                if (on)
                {
                    if (_mutedSelectables.Remove(s)) s.interactable = true;
                }
                else if (s.interactable)
                {
                    s.interactable = false;
                    _mutedSelectables.Add(s);
                }
            }
        }

        private void OnTransformChildrenChanged()
        {
            // Rows were rebuilt — forget references to destroyed objects
            _mutedGraphics.RemoveWhere(g => g == null);
            _mutedSelectables.RemoveWhere(s => s == null);
            var dead = new List<Transform>();
            foreach (var k in _state.Keys) if (k == null) dead.Add(k);
            foreach (var k in dead) _state.Remove(k);
        }
    }
}

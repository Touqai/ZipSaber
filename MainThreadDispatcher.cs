using System;
using System.Collections.Generic;
using UnityEngine;

namespace ZipSaber
{
    /// <summary>
    /// Simple queue that lets background threads schedule work on the Unity main thread.
    /// Attach this to a persistent GameObject once; it drains the queue every frame.
    /// </summary>
    internal class MainThreadDispatcher : MonoBehaviour
    {
        private static MainThreadDispatcher _instance;
        private static readonly Queue<Action> _actions = new Queue<Action>();
        private static readonly object _lock = new object();

        internal static void Enqueue(Action action)
        {
            if (action == null) return;
            EnsureExists();
            lock (_lock) { _actions.Enqueue(action); }
        }

        private static void EnsureExists()
        {
            if (_instance != null) return;
            // May be called from a background thread; schedule creation on main thread
            // via a tiny Unity trick: if we are already on the main thread just create it.
            // If not, it will be created on the first Update after the first Enqueue anyway
            // because we can create the GO from any thread in modern Unity.
            var go = new GameObject("ZipSaber_MainThreadDispatcher");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MainThreadDispatcher>();
        }

        private void Update()
        {
            while (true)
            {
                Action action;
                lock (_lock)
                {
                    if (_actions.Count == 0) break;
                    action = _actions.Dequeue();
                }
                try { action(); }
                catch (Exception ex) { Plugin.Log?.Error($"[Dispatcher] Exception: {ex.Message}\n{ex}"); }
            }
        }
    }
}

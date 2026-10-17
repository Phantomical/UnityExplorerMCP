using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityExplorerMCP.Util
{
    /// <summary>
    /// Hides on-screen overlays (the development-build console and
    /// UnityExplorer's UI) so they don't obscure screenshots.
    /// </summary>
    public static class ScreenOverlays
    {
        const string UniverseLibCanvasName = "UniverseLibCanvas";

        static PropertyInfo _canvasRootProp;
        static bool _canvasRootPropResolved;

        /// <summary>
        /// Saved state of the overlays hidden by <see cref="Hide"/>.
        /// </summary>
        public class State
        {
            internal bool developerConsoleVisible;
            internal readonly List<Canvas> canvases = new();
        }

        /// <summary>
        /// Hide all overlays, returning their previous state for <see cref="Restore"/>.
        /// </summary>
        public static State Hide()
        {
            var state = new State { developerConsoleVisible = Debug.developerConsoleVisible };
            Debug.developerConsoleVisible = false;

            foreach (var canvas in FindUniverseLibCanvases())
            {
                if (!canvas.enabled)
                    continue;
                canvas.enabled = false;
                state.canvases.Add(canvas);
            }

            return state;
        }

        public static void Restore(State state)
        {
            if (state == null)
                return;

            foreach (var canvas in state.canvases)
            {
                if (canvas != null)
                    canvas.enabled = true;
            }
            state.canvases.Clear();

            if (state.developerConsoleVisible)
                Debug.developerConsoleVisible = true;
        }

        /// <summary>
        /// Find the canvases under UniverseLib's root canvas object (used by
        /// UnityExplorer and any other UniverseLib-based UI).
        /// </summary>
        static Canvas[] FindUniverseLibCanvases()
        {
            var root = GetUniverseLibCanvasRoot();
            if (root != null)
                return root.GetComponentsInChildren<Canvas>(true);

            // Fallback: the root has HideInHierarchy set, so GameObject.Find
            // won't see it. Scan all canvases by root name instead.
            var result = new List<Canvas>();
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas.transform.root.name == UniverseLibCanvasName)
                    result.Add(canvas);
            }
            return result.ToArray();
        }

        /// <summary>
        /// Resolve <c>UniverseLib.UI.UniversalUI.CanvasRoot</c> via reflection
        /// so there is no compile-time dependency on UniverseLib.
        /// </summary>
        static GameObject GetUniverseLibCanvasRoot()
        {
            if (!_canvasRootPropResolved)
            {
                _canvasRootPropResolved = true;
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = asm.GetType("UniverseLib.UI.UniversalUI", false);
                        if (type == null)
                            continue;
                        _canvasRootProp = type.GetProperty(
                            "CanvasRoot",
                            BindingFlags.Public | BindingFlags.Static
                        );
                        break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning(
                        $"[UnityExplorerMCP] Could not resolve UniverseLib canvas root: {ex.Message}"
                    );
                }
            }

            try
            {
                return _canvasRootProp?.GetValue(null) as GameObject;
            }
            catch
            {
                return null;
            }
        }
    }
}

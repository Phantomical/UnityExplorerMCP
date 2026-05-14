using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityExplorerMCP.ObjectRegistry
{
    /// <summary>
    /// Manages references to Unity and managed objects across MCP tool calls.
    /// Uses WeakReferences so we don't prevent garbage collection.
    /// </summary>
    public class ObjectRegistry
    {
        readonly Dictionary<int, WeakReference<UnityEngine.Object>> _unityObjects = new();
        readonly Dictionary<long, WeakReference> _managedObjects = new();
        long _nextManagedHandle = 1;
        int _cleanupCounter;
        const int CleanupInterval = 100;

        /// <summary>
        /// Register a Unity object and return its handle string.
        /// </summary>
        public string Register(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;

            int id = obj.GetInstanceID();
            _unityObjects[id] = new WeakReference<UnityEngine.Object>(obj);
            MaybeCleanup();
            return ObjectHandle.ForUnity(id);
        }

        /// <summary>
        /// Register a managed (non-Unity) C# object and return its handle string.
        /// If the object is a UnityEngine.Object, delegates to Register(UnityEngine.Object).
        /// </summary>
        public string RegisterManaged(object obj)
        {
            if (obj == null)
                return null;

            if (obj is UnityEngine.Object unityObj)
                return Register(unityObj);

            long id = _nextManagedHandle++;
            _managedObjects[id] = new WeakReference(obj);
            MaybeCleanup();
            return ObjectHandle.ForManaged(id);
        }

        /// <summary>
        /// Resolve a handle string back to a live object, or null if destroyed/collected.
        /// </summary>
        public object Resolve(string handle)
        {
            if (string.IsNullOrEmpty(handle))
                return null;

            if (ObjectHandle.TryParseUnityId(handle, out int instanceId))
            {
                if (
                    _unityObjects.TryGetValue(instanceId, out var weakRef)
                    && weakRef.TryGetTarget(out var obj)
                    && obj != null
                )
                {
                    return obj;
                }
                _unityObjects.Remove(instanceId);
                return null;
            }

            if (ObjectHandle.TryParseManagedId(handle, out long managedId))
            {
                if (
                    _managedObjects.TryGetValue(managedId, out var weakRef)
                    && weakRef.IsAlive
                    && weakRef.Target != null
                )
                {
                    return weakRef.Target;
                }
                _managedObjects.Remove(managedId);
                return null;
            }

            return null;
        }

        /// <summary>
        /// Resolve a handle and cast to the expected type.
        /// </summary>
        public T Resolve<T>(string handle)
            where T : class
        {
            return Resolve(handle) as T;
        }

        /// <summary>
        /// Try to get a handle for an already-registered Unity object without re-registering.
        /// </summary>
        public string GetHandle(UnityEngine.Object obj)
        {
            if (obj == null)
                return null;
            int id = obj.GetInstanceID();
            return _unityObjects.ContainsKey(id) ? ObjectHandle.ForUnity(id) : Register(obj);
        }

        void MaybeCleanup()
        {
            if (++_cleanupCounter < CleanupInterval)
                return;
            _cleanupCounter = 0;
            Cleanup();
        }

        readonly List<int> _deadUnityScratch = new();
        readonly List<long> _deadManagedScratch = new();

        public void Cleanup()
        {
            _deadUnityScratch.Clear();
            foreach (var kv in _unityObjects)
            {
                if (!kv.Value.TryGetTarget(out var obj) || obj == null)
                    _deadUnityScratch.Add(kv.Key);
            }
            foreach (var key in _deadUnityScratch)
                _unityObjects.Remove(key);
            _deadUnityScratch.Clear();

            _deadManagedScratch.Clear();
            foreach (var kv in _managedObjects)
            {
                if (!kv.Value.IsAlive || kv.Value.Target == null)
                    _deadManagedScratch.Add(kv.Key);
            }
            foreach (var key in _deadManagedScratch)
                _managedObjects.Remove(key);
            _deadManagedScratch.Clear();
        }
    }
}

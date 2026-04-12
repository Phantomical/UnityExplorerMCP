using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace UnityExplorerMCP.Serialization
{
    /// <summary>
    /// Resolves type names across all loaded assemblies.
    /// Supports short names, full names, and assembly-qualified names.
    /// </summary>
    public static class TypeResolver
    {
        static Dictionary<string, Type> _typeCache = new();

        /// <summary>
        /// Find a type by name. Tries exact match first, then searches all assemblies.
        /// </summary>
        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            // Check cache
            if (_typeCache.TryGetValue(name, out var cached))
                return cached;

            // Try direct resolution
            Type type = Type.GetType(name);
            if (type != null)
            {
                _typeCache[name] = type;
                return type;
            }

            // Search all assemblies
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // Try full name match
                    type = asm.GetType(name);
                    if (type != null)
                    {
                        _typeCache[name] = type;
                        return type;
                    }
                }
                catch { }
            }

            // Try short name match (slower)
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == name || t.FullName == name)
                        {
                            _typeCache[name] = t;
                            return t;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Search for types matching a name filter across all assemblies.
        /// </summary>
        public static List<Type> SearchTypes(string nameFilter, int limit = 25, int offset = 0)
        {
            var results = new List<Type>();
            string filter = nameFilter?.ToLowerInvariant() ?? "";

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (
                            string.IsNullOrEmpty(filter)
                            || type.FullName?.ToLowerInvariant().Contains(filter) == true
                        )
                            results.Add(type);
                    }
                }
                catch { }
            }

            return results.Skip(offset).Take(limit).ToList();
        }

        /// <summary>
        /// Get the count of types matching a filter.
        /// </summary>
        public static int CountTypes(string nameFilter)
        {
            string filter = nameFilter?.ToLowerInvariant() ?? "";
            int count = 0;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (
                            string.IsNullOrEmpty(filter)
                            || type.FullName?.ToLowerInvariant().Contains(filter) == true
                        )
                            count++;
                    }
                }
                catch { }
            }

            return count;
        }

        public static void ClearCache() => _typeCache.Clear();
    }
}

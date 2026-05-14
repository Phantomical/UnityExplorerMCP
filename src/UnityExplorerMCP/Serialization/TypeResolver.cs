using System;
using System.Collections.Generic;
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
        /// Search for types matching a name filter and return both the total match count
        /// and the requested page in a single pass over all assemblies.
        /// </summary>
        public static (int TotalCount, List<Type> Page) SearchTypesPaged(
            string nameFilter,
            int limit = 25,
            int offset = 0
        )
        {
            string filter = nameFilter ?? "";
            bool hasFilter = filter.Length > 0;
            int count = 0;
            var page = new List<Type>(Math.Min(limit, 64));

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (hasFilter)
                    {
                        var name = type.FullName;
                        if (
                            name == null
                            || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                        )
                            continue;
                    }

                    if (count >= offset && page.Count < limit)
                        page.Add(type);
                    count++;
                }
            }

            return (count, page);
        }

        public static void ClearCache() => _typeCache.Clear();
    }
}

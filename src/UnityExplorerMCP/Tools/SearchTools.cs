using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityExplorerMCP.Serialization;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class SearchTools : ToolGroupBase
    {
        public SearchTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        #region Parameter Types

        public struct SearchObjectsParams
        {
            [McpParam("Case-insensitive name substring filter")]
            public string NameFilter { get; set; }

            [McpParam("Filter by type (must be a UnityEngine.Object subclass). Default: all.")]
            public string TypeName { get; set; }

            [McpParam(
                "Scene filter. Default: any.",
                EnumValues = new[]
                {
                    "any",
                    "activelyLoaded",
                    "dontDestroyOnLoad",
                    "hideAndDontSave",
                }
            )]
            public string SceneFilter { get; set; }

            [McpParam(
                "Hierarchy filter. Default: any.",
                EnumValues = new[] { "any", "rootObject", "hasParent" }
            )]
            public string ChildFilter { get; set; }

            [McpParam("Max results (default 25)")]
            public int? Limit { get; set; }

            [McpParam("Pagination offset (default 0)")]
            public int? Offset { get; set; }
        }

        public struct SearchTypesParams
        {
            [McpParam("Case-insensitive substring filter on the full type name", Required = true)]
            public string NameFilter { get; set; }

            [McpParam("Max results (default 25)")]
            public int? Limit { get; set; }

            [McpParam("Pagination offset (default 0)")]
            public int? Offset { get; set; }
        }

        public struct SearchSingletonsParams
        {
            [McpParam("Case-insensitive filter on the type name")]
            public string NameFilter { get; set; }

            [McpParam("Max results (default 25)")]
            public int? Limit { get; set; }

            [McpParam("Pagination offset (default 0)")]
            public int? Offset { get; set; }
        }

        #endregion

        public override void Register()
        {
            Tools.Register<SearchObjectsParams>(
                "search_objects",
                "Search for Unity objects by name and/or type, with optional scene and hierarchy filters.",
                SearchObjects
            );

            Tools.Register<SearchTypesParams>(
                "search_types",
                "Search for C# types across all loaded assemblies by name.",
                SearchTypes
            );

            Tools.Register<SearchSingletonsParams>(
                "search_singletons",
                "Search for singleton instances by scanning assemblies for common instance field patterns (Instance, m_instance, s_Instance, etc.).",
                SearchSingletons
            );
        }

        McpProtocol.ToolCallResult SearchObjects(SearchObjectsParams args)
        {
            string sceneFilter = args.SceneFilter ?? "any";
            string childFilter = args.ChildFilter ?? "any";
            int limit = args.Limit ?? 25;
            int offset = args.Offset ?? 0;

            Type searchType = typeof(UnityEngine.Object);
            if (!string.IsNullOrEmpty(args.TypeName))
            {
                var resolved = TypeResolver.FindType(args.TypeName);
                if (resolved == null)
                    return McpProtocol.ToolError($"Type not found: {args.TypeName}");
                if (!typeof(UnityEngine.Object).IsAssignableFrom(resolved))
                    return McpProtocol.ToolError(
                        $"Type '{resolved.FullName}' is not a UnityEngine.Object subclass."
                    );
                searchType = resolved;
            }

            var allObjects = Resources.FindObjectsOfTypeAll(searchType);
            bool shouldFilterGOs =
                searchType == typeof(GameObject) || typeof(Component).IsAssignableFrom(searchType);

            // First pass: collect references for everything that passes the filters.
            // The expensive per-match work (Register / GetFullPath / JsonObject) is deferred
            // to the page slice below so that large match sets don't blow up the call.
            var matches = new List<UnityEngine.Object>();
            foreach (var obj in allObjects)
            {
                if (obj == null)
                    continue;

                if (
                    !string.IsNullOrEmpty(args.NameFilter)
                    && obj.name.IndexOf(args.NameFilter, StringComparison.OrdinalIgnoreCase) < 0
                )
                    continue;

                if (shouldFilterGOs)
                {
                    GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
                    if (go != null)
                    {
                        // Skip UnityExplorer/UniverseLib UI objects
                        if (go.transform.root.name == "UniverseLibCanvas")
                            continue;

                        if (sceneFilter != "any" && !MatchesSceneFilter(go.scene, sceneFilter))
                            continue;

                        if (childFilter == "rootObject" && go.transform.parent != null)
                            continue;
                        if (childFilter == "hasParent" && go.transform.parent == null)
                            continue;
                    }
                }

                matches.Add(obj);
            }

            int totalCount = matches.Count;
            int start = Math.Min(offset, totalCount);
            int end = Math.Min(offset + limit, totalCount);

            var results = new JsonArray();
            for (int i = start; i < end; i++)
            {
                var obj = matches[i];
                GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;

                string path = null;
                string sceneName = null;
                bool? activeSelf = null;
                if (go != null)
                {
                    path = GetFullPath(go.transform);
                    sceneName = go.scene.IsValid() ? go.scene.name : "DontDestroyOnLoad";
                    activeSelf = go.activeSelf;
                }

                results.Add(
                    new JsonObject
                    {
                        ["objectHandle"] = Registry.Register(obj),
                        ["instanceId"] = obj.GetInstanceID(),
                        ["name"] = obj.name,
                        ["typeName"] = obj.GetType().Name,
                        ["sceneName"] = sceneName,
                        ["path"] = path,
                        ["activeSelf"] = activeSelf,
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JsonObject { ["totalCount"] = totalCount, ["results"] = results }
            );
        }

        McpProtocol.ToolCallResult SearchTypes(SearchTypesParams args)
        {
            int limit = args.Limit ?? 25;
            int offset = args.Offset ?? 0;

            var (totalCount, types) = TypeResolver.SearchTypesPaged(
                args.NameFilter,
                limit,
                offset
            );

            var results = new JsonArray();
            foreach (var type in types)
            {
                results.Add(
                    new JsonObject
                    {
                        ["typeName"] = type.Name,
                        ["typeFullName"] = type.FullName,
                        ["assemblyName"] = type.Assembly.GetName().Name,
                        ["namespace"] = type.Namespace,
                        ["isClass"] = type.IsClass,
                        ["isValueType"] = type.IsValueType,
                        ["isEnum"] = type.IsEnum,
                        ["isInterface"] = type.IsInterface,
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JsonObject { ["totalCount"] = totalCount, ["types"] = results }
            );
        }

        McpProtocol.ToolCallResult SearchSingletons(SearchSingletonsParams args)
        {
            int limit = args.Limit ?? 25;
            int offset = args.Offset ?? 0;

            var singletonFieldNames = new[]
            {
                "m_instance",
                "m_Instance",
                "s_instance",
                "s_Instance",
                "_instance",
                "_Instance",
                "instance",
                "Instance",
                "<Instance>k__BackingField",
                "<instance>k__BackingField",
            };

            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            // First pass: collect (type, fieldName, value) for every singleton match.
            // Defer RegisterManaged / TrySafeToString / JsonObject to the page slice below.
            var matches = new List<(Type Type, string FieldName, object Value)>();

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
                    // Skip static classes and enums
                    if ((type.IsSealed && type.IsAbstract) || type.IsEnum)
                        continue;

                    if (
                        !string.IsNullOrEmpty(args.NameFilter)
                        && (
                            type.FullName == null
                            || type.FullName.IndexOf(
                                args.NameFilter,
                                StringComparison.OrdinalIgnoreCase
                            ) < 0
                        )
                    )
                        continue;

                    foreach (var fieldName in singletonFieldNames)
                    {
                        try
                        {
                            var field = type.GetField(fieldName, flags);
                            if (field == null || !field.IsStatic)
                                continue;

                            object value = field.GetValue(null);
                            if (value == null)
                                continue;

                            matches.Add((type, fieldName, value));
                            break; // Found singleton for this type, move on
                        }
                        catch { }
                    }
                }
            }

            int totalCount = matches.Count;
            int start = Math.Min(offset, totalCount);
            int end = Math.Min(offset + limit, totalCount);

            var results = new JsonArray();
            for (int i = start; i < end; i++)
            {
                var match = matches[i];
                results.Add(
                    new JsonObject
                    {
                        ["objectHandle"] = Registry.RegisterManaged(match.Value),
                        ["typeName"] = match.Type.Name,
                        ["typeFullName"] = match.Type.FullName,
                        ["fieldName"] = match.FieldName,
                        ["toString"] = TrySafeToString(match.Value),
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JsonObject { ["totalCount"] = totalCount, ["singletons"] = results }
            );
        }

        #region Helpers

        static bool MatchesSceneFilter(Scene scene, string filter)
        {
            return filter switch
            {
                "activelyLoaded" => scene.buildIndex != -1,
                "dontDestroyOnLoad" => scene.handle == -12,
                "hideAndDontSave" => !scene.IsValid(),
                _ => true,
            };
        }

        static string GetFullPath(Transform t)
        {
            string path = t.name;
            while (t.parent != null)
            {
                t = t.parent;
                path = t.name + "/" + path;
            }
            return "/" + path;
        }

        static string TrySafeToString(object obj)
        {
            try
            {
                return obj.ToString();
            }
            catch
            {
                return $"<{obj.GetType().Name}>";
            }
        }

        #endregion
    }
}

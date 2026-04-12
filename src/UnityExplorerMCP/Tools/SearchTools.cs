using System;
using System.Collections.Generic;
using System.Linq;
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

        public override void Register()
        {
            Tools.Register(
                "search_objects",
                "Search for Unity objects by name and/or type, with optional scene and hierarchy filters.",
                @"{
                    ""nameFilter"":  { ""type"": ""string"", ""description"": ""Case-insensitive name substring filter"" },
                    ""typeName"":    { ""type"": ""string"", ""description"": ""Filter by type (must be a UnityEngine.Object subclass). Default: all."" },
                    ""sceneFilter"": { ""type"": ""string"", ""enum"": [""any"",""activelyLoaded"",""dontDestroyOnLoad"",""hideAndDontSave""], ""description"": ""Scene filter. Default: any."" },
                    ""childFilter"": { ""type"": ""string"", ""enum"": [""any"",""rootObject"",""hasParent""], ""description"": ""Hierarchy filter. Default: any."" },
                    ""limit"":       { ""type"": ""integer"", ""description"": ""Max results (default 25)"" },
                    ""offset"":      { ""type"": ""integer"", ""description"": ""Pagination offset (default 0)"" }
                }",
                null,
                SearchObjects
            );

            Tools.Register(
                "search_types",
                "Search for C# types across all loaded assemblies by name.",
                @"{
                    ""nameFilter"": { ""type"": ""string"", ""description"": ""Case-insensitive substring filter on the full type name"" },
                    ""limit"":      { ""type"": ""integer"", ""description"": ""Max results (default 25)"" },
                    ""offset"":     { ""type"": ""integer"", ""description"": ""Pagination offset (default 0)"" }
                }",
                new[] { "nameFilter" },
                SearchTypes
            );

            Tools.Register(
                "search_singletons",
                "Search for singleton instances by scanning assemblies for common instance field patterns (Instance, m_instance, s_Instance, etc.).",
                @"{
                    ""nameFilter"": { ""type"": ""string"", ""description"": ""Case-insensitive filter on the type name"" },
                    ""limit"":      { ""type"": ""integer"", ""description"": ""Max results (default 25)"" },
                    ""offset"":     { ""type"": ""integer"", ""description"": ""Pagination offset (default 0)"" }
                }",
                null,
                SearchSingletons
            );
        }

        McpProtocol.ToolCallResult SearchObjects(JsonObject args)
        {
            string nameFilter = GetString(args, "nameFilter");
            string typeName = GetString(args, "typeName");
            string sceneFilter = GetString(args, "sceneFilter", "any");
            string childFilter = GetString(args, "childFilter", "any");
            int limit = GetInt(args, "limit", 25);
            int offset = GetInt(args, "offset", 0);

            Type searchType = typeof(UnityEngine.Object);
            if (!string.IsNullOrEmpty(typeName))
            {
                var resolved = TypeResolver.FindType(typeName);
                if (resolved == null)
                    return McpProtocol.ToolError($"Type not found: {typeName}");
                if (!typeof(UnityEngine.Object).IsAssignableFrom(resolved))
                    return McpProtocol.ToolError(
                        $"Type '{resolved.FullName}' is not a UnityEngine.Object subclass."
                    );
                searchType = resolved;
            }

            var allObjects = Resources.FindObjectsOfTypeAll(searchType);
            var results = new List<JsonObject>();
            bool shouldFilterGOs =
                searchType == typeof(GameObject) || typeof(Component).IsAssignableFrom(searchType);

            foreach (var obj in allObjects)
            {
                if (obj == null)
                    continue;

                // Name filter
                if (
                    !string.IsNullOrEmpty(nameFilter)
                    && obj.name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0
                )
                    continue;

                GameObject go = null;
                if (obj is GameObject goObj)
                    go = goObj;
                else if (obj is Component comp)
                    go = comp.gameObject;

                if (go != null && shouldFilterGOs)
                {
                    // Skip UnityExplorer/UniverseLib UI objects
                    if (go.transform.root.name == "UniverseLibCanvas")
                        continue;

                    // Scene filter
                    if (sceneFilter != "any" && !MatchesSceneFilter(go.scene, sceneFilter))
                        continue;

                    // Child filter
                    if (childFilter == "rootObject" && go.transform.parent != null)
                        continue;
                    if (childFilter == "hasParent" && go.transform.parent == null)
                        continue;
                }

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

            int totalCount = results.Count;
            var paged = results.Skip(offset).Take(limit).ToList();

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["totalCount"] = totalCount,
                    ["results"] = new JsonArray(paged.ToArray()),
                }
            );
        }

        McpProtocol.ToolCallResult SearchTypes(JsonObject args)
        {
            string nameFilter = GetString(args, "nameFilter");
            int limit = GetInt(args, "limit", 25);
            int offset = GetInt(args, "offset", 0);

            int totalCount = TypeResolver.CountTypes(nameFilter);
            var types = TypeResolver.SearchTypes(nameFilter, limit, offset);

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

        McpProtocol.ToolCallResult SearchSingletons(JsonObject args)
        {
            string nameFilter = GetString(args, "nameFilter");
            int limit = GetInt(args, "limit", 25);
            int offset = GetInt(args, "offset", 0);

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

            var results = new List<JsonObject>();
            const BindingFlags flags =
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

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
                        !string.IsNullOrEmpty(nameFilter)
                        && (
                            type.FullName == null
                            || type.FullName.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase)
                                < 0
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

                            results.Add(
                                new JsonObject
                                {
                                    ["objectHandle"] = Registry.RegisterManaged(value),
                                    ["typeName"] = type.Name,
                                    ["typeFullName"] = type.FullName,
                                    ["fieldName"] = fieldName,
                                    ["toString"] = TrySafeToString(value),
                                }
                            );
                            break; // Found singleton for this type, move on
                        }
                        catch { }
                    }
                }
            }

            int totalCount = results.Count;
            var paged = results.Skip(offset).Take(limit).ToList();

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["totalCount"] = totalCount,
                    ["singletons"] = new JsonArray(paged.ToArray()),
                }
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

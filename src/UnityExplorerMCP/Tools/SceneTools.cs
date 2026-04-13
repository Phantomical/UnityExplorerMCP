using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class SceneTools : ToolGroupBase
    {
        public SceneTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        #region Parameter Types

        public struct GetRootObjectsParams
        {
            [McpParam(
                "Scene handle from list_scenes. Use -12 for DontDestroyOnLoad, -1 for HideAndDontSave.",
                Required = true
            )]
            public int SceneHandle { get; set; }

            [McpParam("Max results (default 50)")]
            public int? Limit { get; set; }

            [McpParam("Skip first N results (default 0)")]
            public int? Offset { get; set; }
        }

        public struct GetChildrenParams
        {
            [McpParam("Handle of the parent GameObject", Required = true)]
            public string ObjectHandle { get; set; }

            [McpParam("Max results (default 50)")]
            public int? Limit { get; set; }

            [McpParam("Skip first N results (default 0)")]
            public int? Offset { get; set; }
        }

        public struct FindByPathParams
        {
            [McpParam("Hierarchy path, slash-separated. Leading / is optional.", Required = true)]
            public string Path { get; set; }

            [McpParam("Limit search to a specific scene (optional).")]
            public int? SceneHandle { get; set; }
        }

        public struct GetHierarchyPathParams
        {
            [McpParam("Handle of the GameObject", Required = true)]
            public string ObjectHandle { get; set; }
        }

        #endregion

        public override void Register()
        {
            Tools.Register(
                "list_scenes",
                "List all currently loaded Unity scenes, including DontDestroyOnLoad and HideAndDontSave pseudo-scenes.",
                ListScenes
            );

            Tools.Register<GetRootObjectsParams>(
                "get_root_objects",
                "Get the root GameObjects of a scene.",
                GetRootObjects
            );

            Tools.Register<GetChildrenParams>(
                "get_children",
                "Get the immediate children of a GameObject.",
                GetChildren
            );

            Tools.Register<FindByPathParams>(
                "find_by_path",
                "Find a GameObject by its hierarchy path (e.g. '/Sun/Sunlight'). Searches active and inactive objects.",
                FindByPath
            );

            Tools.Register<GetHierarchyPathParams>(
                "get_hierarchy_path",
                "Get the full hierarchy path of a GameObject from the scene root, including ancestor chain.",
                GetHierarchyPath
            );
        }

        McpProtocol.ToolCallResult ListScenes()
        {
            var scenes = new JsonArray();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.IsValid())
                    continue;

                scenes.Add(
                    new JsonObject
                    {
                        ["name"] = scene.name,
                        ["buildIndex"] = scene.buildIndex,
                        ["handle"] = scene.handle,
                        ["isLoaded"] = scene.isLoaded,
                        ["rootCount"] = scene.rootCount,
                        ["path"] = scene.path,
                        ["isSpecial"] = false,
                    }
                );
            }

            // DontDestroyOnLoad (handle -12)
            if (DontDestroyOnLoadExists())
            {
                scenes.Add(
                    new JsonObject
                    {
                        ["name"] = "DontDestroyOnLoad",
                        ["buildIndex"] = -1,
                        ["handle"] = -12,
                        ["isLoaded"] = true,
                        ["rootCount"] = GetDontDestroyRootObjects().Length,
                        ["path"] = "",
                        ["isSpecial"] = true,
                        ["specialType"] = "DontDestroyOnLoad",
                    }
                );
            }

            // HideAndDontSave (handle -1)
            scenes.Add(
                new JsonObject
                {
                    ["name"] = "HideAndDontSave",
                    ["buildIndex"] = -1,
                    ["handle"] = -1,
                    ["isLoaded"] = true,
                    ["rootCount"] = 0,
                    ["path"] = "",
                    ["isSpecial"] = true,
                    ["specialType"] = "HideAndDontSave",
                }
            );

            return McpProtocol.ToolSuccess(new JsonObject { ["scenes"] = scenes });
        }

        McpProtocol.ToolCallResult GetRootObjects(GetRootObjectsParams args)
        {
            int limit = args.Limit ?? 50;
            int offset = args.Offset ?? 0;

            GameObject[] roots = GetRootObjectsForScene(args.SceneHandle);
            string sceneName = GetSceneName(args.SceneHandle);

            var objects = new JsonArray();
            for (int i = offset; i < roots.Length && i < offset + limit; i++)
            {
                var go = roots[i];
                if (go == null)
                    continue;
                objects.Add(GameObjectSummary(go));
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["sceneHandle"] = args.SceneHandle,
                    ["sceneName"] = sceneName,
                    ["totalCount"] = roots.Length,
                    ["objects"] = objects,
                }
            );
        }

        McpProtocol.ToolCallResult GetChildren(GetChildrenParams args)
        {
            int limit = args.Limit ?? 50;
            int offset = args.Offset ?? 0;

            var go = Registry.Resolve<GameObject>(args.ObjectHandle);
            if (go == null)
            {
                var comp = Registry.Resolve<Component>(args.ObjectHandle);
                if (comp != null)
                    go = comp.gameObject;
            }
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var transform = go.transform;
            int totalCount = transform.childCount;
            var children = new JsonArray();

            for (int i = offset; i < totalCount && i < offset + limit; i++)
            {
                var child = transform.GetChild(i);
                if (child == null)
                    continue;
                var childGo = child.gameObject;
                children.Add(
                    new JsonObject
                    {
                        ["objectHandle"] = Registry.Register(childGo),
                        ["instanceId"] = childGo.GetInstanceID(),
                        ["name"] = childGo.name,
                        ["activeSelf"] = childGo.activeSelf,
                        ["activeInHierarchy"] = childGo.activeInHierarchy,
                        ["childCount"] = child.childCount,
                        ["siblingIndex"] = child.GetSiblingIndex(),
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["parentHandle"] = args.ObjectHandle,
                    ["parentName"] = go.name,
                    ["totalCount"] = totalCount,
                    ["children"] = children,
                }
            );
        }

        McpProtocol.ToolCallResult FindByPath(FindByPathParams args)
        {
            if (string.IsNullOrEmpty(args.Path))
                return McpProtocol.ToolError("Path is required");

            // Normalize path
            string path = args.Path.TrimStart('/');
            string[] parts = path.Split('/');

            List<GameObject[]> rootSets = new();
            if (args.SceneHandle.HasValue)
            {
                rootSets.Add(GetRootObjectsForScene(args.SceneHandle.Value));
            }
            else
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    Scene scene = SceneManager.GetSceneAt(i);
                    if (scene.IsValid() && scene.isLoaded)
                        rootSets.Add(scene.GetRootGameObjects());
                }
                if (DontDestroyOnLoadExists())
                    rootSets.Add(GetDontDestroyRootObjects());
            }

            foreach (var roots in rootSets)
            {
                foreach (var root in roots)
                {
                    if (root.name != parts[0])
                        continue;

                    GameObject current = root;
                    bool found = true;
                    for (int i = 1; i < parts.Length; i++)
                    {
                        Transform child = current.transform.Find(parts[i]);
                        if (child == null)
                        {
                            found = false;
                            break;
                        }
                        current = child.gameObject;
                    }

                    if (found)
                    {
                        return McpProtocol.ToolSuccess(
                            new JsonObject
                            {
                                ["found"] = true,
                                ["objectHandle"] = Registry.Register(current),
                                ["instanceId"] = current.GetInstanceID(),
                                ["name"] = current.name,
                                ["fullPath"] = GetFullPath(current.transform),
                                ["sceneName"] = current.scene.IsValid()
                                    ? current.scene.name
                                    : "DontDestroyOnLoad",
                            }
                        );
                    }
                }
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["found"] = false,
                    ["objectHandle"] = null,
                    ["instanceId"] = null,
                    ["name"] = null,
                    ["fullPath"] = null,
                    ["sceneName"] = null,
                }
            );
        }

        McpProtocol.ToolCallResult GetHierarchyPath(GetHierarchyPathParams args)
        {
            var go = Registry.Resolve<GameObject>(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var ancestors = new JsonArray();
            Transform t = go.transform.parent;
            while (t != null)
            {
                ancestors.Insert(
                    0,
                    new JsonObject
                    {
                        ["objectHandle"] = Registry.Register(t.gameObject),
                        ["name"] = t.name,
                    }
                );
                t = t.parent;
            }

            int depth = 0;
            t = go.transform;
            while (t.parent != null)
            {
                depth++;
                t = t.parent;
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["objectHandle"] = args.ObjectHandle,
                    ["path"] = GetFullPath(go.transform),
                    ["sceneName"] = go.scene.IsValid() ? go.scene.name : "DontDestroyOnLoad",
                    ["depth"] = depth,
                    ["ancestors"] = ancestors,
                }
            );
        }

        #region Helpers

        JsonObject GameObjectSummary(GameObject go)
        {
            return new JsonObject
            {
                ["objectHandle"] = Registry.Register(go),
                ["instanceId"] = go.GetInstanceID(),
                ["name"] = go.name,
                ["activeSelf"] = go.activeSelf,
                ["activeInHierarchy"] = go.activeInHierarchy,
                ["childCount"] = go.transform.childCount,
                ["componentCount"] = go.GetComponents<Component>().Length,
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

        static string GetSceneName(int handle)
        {
            if (handle == -12)
                return "DontDestroyOnLoad";
            if (handle == -1)
                return "HideAndDontSave";
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.handle == handle)
                    return scene.name;
            }
            return "Unknown";
        }

        static GameObject[] GetRootObjectsForScene(int handle)
        {
            if (handle == -12)
                return GetDontDestroyRootObjects();

            if (handle == -1)
                return GetHideAndDontSaveObjects();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.handle == handle && scene.IsValid() && scene.isLoaded)
                    return scene.GetRootGameObjects();
            }
            return new GameObject[0];
        }

        static GameObject[] GetDontDestroyRootObjects()
        {
            // Create a temporary GO in DontDestroyOnLoad to get its scene
            GameObject temp = new GameObject("__McpTemp__");
            Object.DontDestroyOnLoad(temp);
            var ddolScene = temp.scene;
            Object.DestroyImmediate(temp);

            if (ddolScene.IsValid())
                return ddolScene.GetRootGameObjects().Where(g => g.name != "__McpTemp__").ToArray();

            return new GameObject[0];
        }

        static bool DontDestroyOnLoadExists()
        {
            try
            {
                var temp = new GameObject("__McpDdolCheck__");
                Object.DontDestroyOnLoad(temp);
                bool exists = temp.scene.IsValid();
                Object.DestroyImmediate(temp);
                return exists;
            }
            catch
            {
                return false;
            }
        }

        static GameObject[] GetHideAndDontSaveObjects()
        {
            var allGOs = Resources.FindObjectsOfTypeAll<GameObject>();
            return allGOs
                .Where(g =>
                    g.transform.parent == null
                    && !g.scene.IsValid()
                    && g.hideFlags != HideFlags.None
                )
                .ToArray();
        }

        #endregion
    }
}

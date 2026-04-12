using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class GameObjectTools : ToolGroupBase
    {
        public GameObjectTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        public override void Register()
        {
            Tools.Register(
                "get_gameobject",
                "Get detailed information about a GameObject: name, active state, layer, tag, transform, children (first 20), and all components.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the GameObject"" }
                }",
                new[] { "objectHandle" },
                GetGameObject
            );

            Tools.Register(
                "set_gameobject",
                "Modify properties of a GameObject. Only specified fields are changed.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the GameObject"" },
                    ""name"":       { ""type"": ""string"",  ""description"": ""New name"" },
                    ""activeSelf"": { ""type"": ""boolean"", ""description"": ""Set active/inactive"" },
                    ""layer"":      { ""type"": ""integer"", ""description"": ""Set layer"" },
                    ""tag"":        { ""type"": ""string"",  ""description"": ""Set tag"" },
                    ""isStatic"":   { ""type"": ""boolean"", ""description"": ""Set static flag"" }
                }",
                new[] { "objectHandle" },
                SetGameObject
            );

            Tools.Register(
                "get_transform",
                "Get detailed transform information: world/local position, rotation, scale, forward/up/right vectors.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the GameObject"" }
                }",
                new[] { "objectHandle" },
                GetTransform
            );

            Tools.Register(
                "set_transform",
                "Set transform properties. Only specified fields are modified.",
                @"{
                    ""objectHandle"":  { ""type"": ""string"", ""description"": ""Handle of the GameObject"" },
                    ""position"":      { ""type"": ""object"", ""description"": ""World position {x,y,z}"", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}} },
                    ""localPosition"": { ""type"": ""object"", ""description"": ""Local position {x,y,z}"", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}} },
                    ""rotation"":      { ""type"": ""object"", ""description"": ""Local Euler angles {x,y,z}"", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}} },
                    ""localScale"":    { ""type"": ""object"", ""description"": ""Local scale {x,y,z}"", ""properties"": {""x"":{""type"":""number""},""y"":{""type"":""number""},""z"":{""type"":""number""}} }
                }",
                new[] { "objectHandle" },
                SetTransform
            );
        }

        McpProtocol.ToolCallResult GetGameObject(JObject args)
        {
            string handle = GetString(args, "objectHandle");
            var go = ResolveGameObject(handle);
            if (go == null)
                return HandleNotFound(handle);

            var transform = go.transform;

            // Children (first 20)
            var children = new JArray();
            int childCount = transform.childCount;
            for (int i = 0; i < childCount && i < 20; i++)
            {
                var child = transform.GetChild(i);
                children.Add(
                    new JObject
                    {
                        ["objectHandle"] = Registry.Register(child.gameObject),
                        ["name"] = child.name,
                        ["activeSelf"] = child.gameObject.activeSelf,
                        ["childCount"] = child.childCount,
                    }
                );
            }

            // Components
            var components = new JArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                    continue;
                var compType = comp.GetType();
                bool isBehaviour = comp is Behaviour;
                components.Add(
                    new JObject
                    {
                        ["objectHandle"] = Registry.Register(comp),
                        ["typeName"] = compType.Name,
                        ["typeFullName"] = compType.FullName,
                        ["isBehaviour"] = isBehaviour,
                        ["enabled"] = isBehaviour ? (bool?)(comp as Behaviour).enabled : null,
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JObject
                {
                    ["objectHandle"] = handle,
                    ["instanceId"] = go.GetInstanceID(),
                    ["name"] = go.name,
                    ["activeSelf"] = go.activeSelf,
                    ["activeInHierarchy"] = go.activeInHierarchy,
                    ["isStatic"] = go.isStatic,
                    ["layer"] = go.layer,
                    ["layerName"] = LayerMask.LayerToName(go.layer),
                    ["tag"] = go.tag,
                    ["sceneName"] = go.scene.IsValid() ? go.scene.name : "DontDestroyOnLoad",
                    ["hideFlags"] = go.hideFlags.ToString(),
                    ["path"] = GetFullPath(transform),
                    ["transform"] = new JObject
                    {
                        ["position"] = Vec3(transform.position),
                        ["localPosition"] = Vec3(transform.localPosition),
                        ["rotation"] = Vec3(transform.localEulerAngles),
                        ["localScale"] = Vec3(transform.localScale),
                    },
                    ["parentHandle"] =
                        transform.parent != null
                            ? Registry.Register(transform.parent.gameObject)
                            : null,
                    ["parentName"] = transform.parent != null ? transform.parent.name : null,
                    ["childCount"] = childCount,
                    ["children"] = children,
                    ["components"] = components,
                }
            );
        }

        McpProtocol.ToolCallResult SetGameObject(JObject args)
        {
            string handle = GetString(args, "objectHandle");
            var go = ResolveGameObject(handle);
            if (go == null)
                return HandleNotFound(handle);

            var updated = new JArray();

            if (HasKey(args, "name"))
            {
                go.name = GetString(args, "name");
                updated.Add("name");
            }
            if (HasKey(args, "activeSelf"))
            {
                go.SetActive(GetBool(args, "activeSelf"));
                updated.Add("activeSelf");
            }
            if (HasKey(args, "layer"))
            {
                go.layer = GetInt(args, "layer");
                updated.Add("layer");
            }
            if (HasKey(args, "tag"))
            {
                go.tag = GetString(args, "tag");
                updated.Add("tag");
            }
            if (HasKey(args, "isStatic"))
            {
                go.isStatic = GetBool(args, "isStatic");
                updated.Add("isStatic");
            }

            return McpProtocol.ToolSuccess(
                new JObject
                {
                    ["success"] = true,
                    ["objectHandle"] = handle,
                    ["updatedProperties"] = updated,
                }
            );
        }

        McpProtocol.ToolCallResult GetTransform(JObject args)
        {
            string handle = GetString(args, "objectHandle");
            var go = ResolveGameObject(handle);
            if (go == null)
                return HandleNotFound(handle);

            var t = go.transform;
            return McpProtocol.ToolSuccess(
                new JObject
                {
                    ["objectHandle"] = handle,
                    ["position"] = Vec3(t.position),
                    ["localPosition"] = Vec3(t.localPosition),
                    ["rotation"] = new JObject
                    {
                        ["x"] = t.rotation.x,
                        ["y"] = t.rotation.y,
                        ["z"] = t.rotation.z,
                        ["w"] = t.rotation.w,
                    },
                    ["localEulerAngles"] = Vec3(t.localEulerAngles),
                    ["localScale"] = Vec3(t.localScale),
                    ["lossyScale"] = Vec3(t.lossyScale),
                    ["forward"] = Vec3(t.forward),
                    ["up"] = Vec3(t.up),
                    ["right"] = Vec3(t.right),
                }
            );
        }

        McpProtocol.ToolCallResult SetTransform(JObject args)
        {
            string handle = GetString(args, "objectHandle");
            var go = ResolveGameObject(handle);
            if (go == null)
                return HandleNotFound(handle);

            var t = go.transform;
            var updated = new JArray();

            if (HasKey(args, "position"))
            {
                t.position = ParseVec3(args["position"] as JObject);
                updated.Add("position");
            }
            if (HasKey(args, "localPosition"))
            {
                t.localPosition = ParseVec3(args["localPosition"] as JObject);
                updated.Add("localPosition");
            }
            if (HasKey(args, "rotation"))
            {
                t.localEulerAngles = ParseVec3(args["rotation"] as JObject);
                updated.Add("rotation");
            }
            if (HasKey(args, "localScale"))
            {
                t.localScale = ParseVec3(args["localScale"] as JObject);
                updated.Add("localScale");
            }

            return McpProtocol.ToolSuccess(
                new JObject
                {
                    ["success"] = true,
                    ["objectHandle"] = handle,
                    ["updatedProperties"] = updated,
                }
            );
        }

        #region Helpers

        GameObject ResolveGameObject(string handle)
        {
            var obj = Registry.Resolve(handle);
            if (obj is GameObject go)
                return go;
            if (obj is Component comp && comp != null)
                return comp.gameObject;
            return null;
        }

        static JObject Vec3(Vector3 v) =>
            new()
            {
                ["x"] = v.x,
                ["y"] = v.y,
                ["z"] = v.z,
            };

        static Vector3 ParseVec3(JObject obj)
        {
            if (obj == null)
                return Vector3.zero;
            return new Vector3(
                obj["x"]?.Value<float>() ?? 0,
                obj["y"]?.Value<float>() ?? 0,
                obj["z"]?.Value<float>() ?? 0
            );
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

        #endregion
    }
}

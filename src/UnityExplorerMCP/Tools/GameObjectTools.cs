using System.Text.Json.Nodes;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class GameObjectTools : ToolGroupBase
    {
        public GameObjectTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        #region Parameter Types

        public struct GetGameObjectParams
        {
            [McpParam("Handle of the GameObject", Required = true)]
            public string ObjectHandle { get; set; }
        }

        public struct SetGameObjectParams
        {
            [McpParam("Handle of the GameObject", Required = true)]
            public string ObjectHandle { get; set; }

            [McpParam("New name")]
            public string Name { get; set; }

            [McpParam("Set active/inactive")]
            public bool? ActiveSelf { get; set; }

            [McpParam("Set layer")]
            public int? Layer { get; set; }

            [McpParam("Set tag")]
            public string Tag { get; set; }

            [McpParam("Set static flag")]
            public bool? IsStatic { get; set; }
        }

        public struct GetTransformParams
        {
            [McpParam("Handle of the GameObject", Required = true)]
            public string ObjectHandle { get; set; }
        }

        public struct SetTransformParams
        {
            [McpParam("Handle of the GameObject", Required = true)]
            public string ObjectHandle { get; set; }

            [McpParam("World position {x,y,z}")]
            public Vec3Param? Position { get; set; }

            [McpParam("Local position {x,y,z}")]
            public Vec3Param? LocalPosition { get; set; }

            [McpParam("Local Euler angles {x,y,z}")]
            public Vec3Param? Rotation { get; set; }

            [McpParam("Local scale {x,y,z}")]
            public Vec3Param? LocalScale { get; set; }
        }

        #endregion

        public override void Register()
        {
            Tools.Register<GetGameObjectParams>(
                "get_gameobject",
                "Get detailed information about a GameObject: name, active state, layer, tag, transform, children (first 20), and all components.",
                GetGameObject
            );

            Tools.Register<SetGameObjectParams>(
                "set_gameobject",
                "Modify properties of a GameObject. Only specified fields are changed.",
                SetGameObject
            );

            Tools.Register<GetTransformParams>(
                "get_transform",
                "Get detailed transform information: world/local position, rotation, scale, forward/up/right vectors.",
                GetTransform
            );

            Tools.Register<SetTransformParams>(
                "set_transform",
                "Set transform properties. Only specified fields are modified.",
                SetTransform
            );
        }

        McpProtocol.ToolCallResult GetGameObject(GetGameObjectParams args)
        {
            var go = ResolveGameObject(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var transform = go.transform;

            // Children (first 20)
            var children = new JsonArray();
            int childCount = transform.childCount;
            for (int i = 0; i < childCount && i < 20; i++)
            {
                var child = transform.GetChild(i);
                children.Add(
                    new JsonObject
                    {
                        ["objectHandle"] = Registry.Register(child.gameObject),
                        ["name"] = child.name,
                        ["activeSelf"] = child.gameObject.activeSelf,
                        ["childCount"] = child.childCount,
                    }
                );
            }

            // Components
            var components = new JsonArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                    continue;
                var compType = comp.GetType();
                bool isBehaviour = comp is Behaviour;
                components.Add(
                    new JsonObject
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
                new JsonObject
                {
                    ["objectHandle"] = args.ObjectHandle,
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
                    ["transform"] = new JsonObject
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

        McpProtocol.ToolCallResult SetGameObject(SetGameObjectParams args)
        {
            var go = ResolveGameObject(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var updated = new JsonArray();

            if (args.Name != null)
            {
                go.name = args.Name;
                updated.Add("name");
            }
            if (args.ActiveSelf.HasValue)
            {
                go.SetActive(args.ActiveSelf.Value);
                updated.Add("activeSelf");
            }
            if (args.Layer.HasValue)
            {
                go.layer = args.Layer.Value;
                updated.Add("layer");
            }
            if (args.Tag != null)
            {
                go.tag = args.Tag;
                updated.Add("tag");
            }
            if (args.IsStatic.HasValue)
            {
                go.isStatic = args.IsStatic.Value;
                updated.Add("isStatic");
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["success"] = true,
                    ["objectHandle"] = args.ObjectHandle,
                    ["updatedProperties"] = updated,
                }
            );
        }

        McpProtocol.ToolCallResult GetTransform(GetTransformParams args)
        {
            var go = ResolveGameObject(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var t = go.transform;
            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["objectHandle"] = args.ObjectHandle,
                    ["position"] = Vec3(t.position),
                    ["localPosition"] = Vec3(t.localPosition),
                    ["rotation"] = new JsonObject
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

        McpProtocol.ToolCallResult SetTransform(SetTransformParams args)
        {
            var go = ResolveGameObject(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var t = go.transform;
            var updated = new JsonArray();

            if (args.Position.HasValue)
            {
                t.position = args.Position.Value.ToVector3();
                updated.Add("position");
            }
            if (args.LocalPosition.HasValue)
            {
                t.localPosition = args.LocalPosition.Value.ToVector3();
                updated.Add("localPosition");
            }
            if (args.Rotation.HasValue)
            {
                t.localEulerAngles = args.Rotation.Value.ToVector3();
                updated.Add("rotation");
            }
            if (args.LocalScale.HasValue)
            {
                t.localScale = args.LocalScale.Value.ToVector3();
                updated.Add("localScale");
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["success"] = true,
                    ["objectHandle"] = args.ObjectHandle,
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

        static JsonObject Vec3(Vector3 v) =>
            new()
            {
                ["x"] = v.x,
                ["y"] = v.y,
                ["z"] = v.z,
            };

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

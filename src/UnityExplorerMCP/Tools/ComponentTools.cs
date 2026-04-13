using System;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityExplorerMCP.Serialization;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class ComponentTools : ToolGroupBase
    {
        public ComponentTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools) { }

        #region Parameter Types

        public struct ListComponentsParams
        {
            [McpParam("Handle of the GameObject", Required = true)]
            public string ObjectHandle { get; set; }
        }

        public struct ToggleComponentParams
        {
            [McpParam("Handle of the Component", Required = true)]
            public string ObjectHandle { get; set; }

            [McpParam("Desired enabled state", Required = true)]
            public bool Enabled { get; set; }
        }

        public struct AddComponentParams
        {
            [McpParam("Handle of the target GameObject", Required = true)]
            public string ObjectHandle { get; set; }

            [McpParam("Full or short name of the component type", Required = true)]
            public string TypeName { get; set; }
        }

        public struct RemoveComponentParams
        {
            [McpParam("Handle of the Component to destroy", Required = true)]
            public string ObjectHandle { get; set; }
        }

        #endregion

        public override void Register()
        {
            Tools.Register<ListComponentsParams>(
                "list_components",
                "List all components on a GameObject with type info, enabled state, and assembly info.",
                ListComponents
            );

            Tools.Register<ToggleComponentParams>(
                "toggle_component",
                "Enable or disable a Behaviour component.",
                ToggleComponent
            );

            Tools.Register<AddComponentParams>(
                "add_component",
                "Add a new component to a GameObject by type name.",
                AddComponent
            );

            Tools.Register<RemoveComponentParams>(
                "remove_component",
                "Destroy a component. Cannot destroy the Transform component.",
                RemoveComponent
            );
        }

        McpProtocol.ToolCallResult ListComponents(ListComponentsParams args)
        {
            var go = ResolveGameObject(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            var components = new JsonArray();
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null)
                    continue;
                var type = comp.GetType();
                bool isBehaviour = comp is Behaviour;

                components.Add(
                    new JsonObject
                    {
                        ["objectHandle"] = Registry.Register(comp),
                        ["instanceId"] = comp.GetInstanceID(),
                        ["typeName"] = type.Name,
                        ["typeFullName"] = type.FullName,
                        ["assemblyName"] = type.Assembly.GetName().Name,
                        ["isBehaviour"] = isBehaviour,
                        ["enabled"] = isBehaviour ? (bool?)(comp as Behaviour).enabled : null,
                        ["isTransform"] = comp is Transform,
                    }
                );
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["gameObjectHandle"] = args.ObjectHandle,
                    ["gameObjectName"] = go.name,
                    ["components"] = components,
                }
            );
        }

        McpProtocol.ToolCallResult ToggleComponent(ToggleComponentParams args)
        {
            var comp = Registry.Resolve<Component>(args.ObjectHandle);
            if (comp == null)
                return HandleNotFound(args.ObjectHandle);

            if (comp is Behaviour behaviour)
            {
                behaviour.enabled = args.Enabled;
                return McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["success"] = true,
                        ["objectHandle"] = args.ObjectHandle,
                        ["typeName"] = comp.GetType().Name,
                        ["enabled"] = behaviour.enabled,
                    }
                );
            }

            return McpProtocol.ToolError(
                $"Component '{comp.GetType().Name}' is not a Behaviour and cannot be toggled."
            );
        }

        McpProtocol.ToolCallResult AddComponent(AddComponentParams args)
        {
            var go = ResolveGameObject(args.ObjectHandle);
            if (go == null)
                return HandleNotFound(args.ObjectHandle);

            Type type = TypeResolver.FindType(args.TypeName);
            if (type == null)
                return McpProtocol.ToolError($"Could not find type: {args.TypeName}");

            if (!typeof(Component).IsAssignableFrom(type))
                return McpProtocol.ToolError(
                    $"Type '{type.FullName}' does not derive from Component."
                );

            var newComp = go.AddComponent(type);
            if (newComp == null)
                return McpProtocol.ToolError($"Failed to add component of type '{type.FullName}'.");

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["success"] = true,
                    ["objectHandle"] = args.ObjectHandle,
                    ["newComponentHandle"] = Registry.Register(newComp),
                    ["typeName"] = type.FullName,
                }
            );
        }

        McpProtocol.ToolCallResult RemoveComponent(RemoveComponentParams args)
        {
            var comp = Registry.Resolve<Component>(args.ObjectHandle);
            if (comp == null)
                return HandleNotFound(args.ObjectHandle);

            if (comp is Transform)
                return McpProtocol.ToolError("Cannot destroy the Transform component.");

            string typeName = comp.GetType().Name;
            UnityEngine.Object.Destroy(comp);

            return McpProtocol.ToolSuccess(
                new JsonObject { ["success"] = true, ["typeName"] = typeName }
            );
        }

        GameObject ResolveGameObject(string handle)
        {
            var obj = Registry.Resolve(handle);
            if (obj is GameObject go)
                return go;
            if (obj is Component comp && comp != null)
                return comp.gameObject;
            return null;
        }
    }
}

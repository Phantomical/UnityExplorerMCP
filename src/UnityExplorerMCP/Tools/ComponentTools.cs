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

        public override void Register()
        {
            Tools.Register(
                "list_components",
                "List all components on a GameObject with type info, enabled state, and assembly info.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the GameObject"" }
                }",
                new[] { "objectHandle" },
                ListComponents
            );

            Tools.Register(
                "toggle_component",
                "Enable or disable a Behaviour component.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the Component"" },
                    ""enabled"":      { ""type"": ""boolean"", ""description"": ""Desired enabled state"" }
                }",
                new[] { "objectHandle", "enabled" },
                ToggleComponent
            );

            Tools.Register(
                "add_component",
                "Add a new component to a GameObject by type name.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the target GameObject"" },
                    ""typeName"":     { ""type"": ""string"", ""description"": ""Full or short name of the component type"" }
                }",
                new[] { "objectHandle", "typeName" },
                AddComponent
            );

            Tools.Register(
                "remove_component",
                "Destroy a component. Cannot destroy the Transform component.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the Component to destroy"" }
                }",
                new[] { "objectHandle" },
                RemoveComponent
            );
        }

        McpProtocol.ToolCallResult ListComponents(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            var go = ResolveGameObject(handle);
            if (go == null)
                return HandleNotFound(handle);

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
                    ["gameObjectHandle"] = handle,
                    ["gameObjectName"] = go.name,
                    ["components"] = components,
                }
            );
        }

        McpProtocol.ToolCallResult ToggleComponent(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            bool enabled = GetBool(args, "enabled");

            var comp = Registry.Resolve<Component>(handle);
            if (comp == null)
                return HandleNotFound(handle);

            if (comp is Behaviour behaviour)
            {
                behaviour.enabled = enabled;
                return McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["success"] = true,
                        ["objectHandle"] = handle,
                        ["typeName"] = comp.GetType().Name,
                        ["enabled"] = behaviour.enabled,
                    }
                );
            }

            return McpProtocol.ToolError(
                $"Component '{comp.GetType().Name}' is not a Behaviour and cannot be toggled."
            );
        }

        McpProtocol.ToolCallResult AddComponent(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            string typeName = GetString(args, "typeName");

            var go = ResolveGameObject(handle);
            if (go == null)
                return HandleNotFound(handle);

            Type type = TypeResolver.FindType(typeName);
            if (type == null)
                return McpProtocol.ToolError($"Could not find type: {typeName}");

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
                    ["objectHandle"] = handle,
                    ["newComponentHandle"] = Registry.Register(newComp),
                    ["typeName"] = type.FullName,
                }
            );
        }

        McpProtocol.ToolCallResult RemoveComponent(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");

            var comp = Registry.Resolve<Component>(handle);
            if (comp == null)
                return HandleNotFound(handle);

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

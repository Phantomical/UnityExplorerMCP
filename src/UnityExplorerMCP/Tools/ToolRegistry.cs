using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Attribute to mark a class as containing MCP tool methods.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class McpToolGroupAttribute : Attribute { }

    /// <summary>
    /// Attribute to mark a method as an MCP tool.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class McpToolAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }

        public McpToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }
    }

    /// <summary>
    /// Defines the JSON Schema for a tool's input parameters.
    /// Applied to the same method as McpToolAttribute.
    /// The schema string should be valid JSON for the "properties" portion of the input schema.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class McpToolSchemaAttribute : Attribute
    {
        public string SchemaJson { get; }
        public string[] Required { get; }

        public McpToolSchemaAttribute(string schemaJson, params string[] required)
        {
            SchemaJson = schemaJson;
            Required = required;
        }
    }

    public delegate McpProtocol.ToolCallResult ToolHandler(JsonObject arguments);

    /// <summary>
    /// Registry that maps tool names to their handlers and schema definitions.
    /// </summary>
    public class ToolRegistry
    {
        readonly Dictionary<string, ToolHandler> _handlers = new();
        readonly Dictionary<string, McpProtocol.ToolDefinition> _definitions = new();

        public void Register(
            string name,
            string description,
            JsonObject inputSchema,
            ToolHandler handler
        )
        {
            _handlers[name] = handler;
            _definitions[name] = new McpProtocol.ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = inputSchema,
            };
        }

        public void Register(
            string name,
            string description,
            string propertiesJson,
            string[] required,
            ToolHandler handler
        )
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = JsonNode.Parse(propertiesJson).AsObject(),
            };
            if (required != null && required.Length > 0)
            {
                var reqArr = new JsonArray();
                foreach (var r in required)
                    reqArr.Add((JsonNode)r);
                schema["required"] = reqArr;
            }

            Register(name, description, schema, handler);
        }

        public McpProtocol.ToolCallResult Invoke(string name, JsonObject arguments)
        {
            if (!_handlers.TryGetValue(name, out var handler))
                return McpProtocol.ToolError($"Unknown tool: {name}");

            try
            {
                return handler(arguments);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[UnityExplorerMCP] Tool '{name}' failed: {ex}");
                return McpProtocol.ToolError($"Tool execution failed: {ex.Message}");
            }
        }

        public List<McpProtocol.ToolDefinition> ListTools()
        {
            return new List<McpProtocol.ToolDefinition>(_definitions.Values);
        }

        public bool HasTool(string name) => _handlers.ContainsKey(name);
    }
}

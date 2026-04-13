using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Registry that maps tool names to their handlers and schema definitions.
    /// Supports typed parameter structs with automatic deserialization and validation.
    /// </summary>
    public class ToolRegistry
    {
        readonly Dictionary<string, Func<JsonObject, McpProtocol.ToolCallResult>> _handlers = new();
        readonly Dictionary<string, McpProtocol.ToolDefinition> _definitions = new();

        static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        /// <summary>
        /// Register a tool with typed parameters. The JSON schema is auto-generated from
        /// the struct's properties and <see cref="McpParamAttribute"/> metadata. Required
        /// fields are validated before the handler is called.
        /// </summary>
        public void Register<T>(
            string name,
            string description,
            Func<T, McpProtocol.ToolCallResult> handler
        )
        {
            var schema = SchemaGenerator.Generate(typeof(T));
            var requiredFields = SchemaGenerator.GetRequiredFields(typeof(T));

            _handlers[name] = args =>
            {
                foreach (var field in requiredFields)
                {
                    if (args == null || !args.ContainsKey(field) || args[field] is null)
                        return McpProtocol.ToolError($"Required parameter '{field}' is missing.");
                }

                T typed;
                try
                {
                    typed = (args ?? new JsonObject()).Deserialize<T>(DeserializeOptions);
                }
                catch (Exception ex)
                {
                    return McpProtocol.ToolError($"Invalid parameters: {ex.Message}");
                }

                return handler(typed);
            };

            _definitions[name] = new McpProtocol.ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = schema,
            };
        }

        /// <summary>
        /// Register a tool with no parameters.
        /// </summary>
        public void Register(
            string name,
            string description,
            Func<McpProtocol.ToolCallResult> handler
        )
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };

            _handlers[name] = _ => handler();
            _definitions[name] = new McpProtocol.ToolDefinition
            {
                Name = name,
                Description = description,
                InputSchema = schema,
            };
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

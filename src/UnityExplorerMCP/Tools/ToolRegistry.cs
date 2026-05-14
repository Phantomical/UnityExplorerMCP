using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
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
        readonly Dictionary<
            string,
            Func<JsonObject, Action<McpProtocol.ToolCallResult>, IEnumerator>
        > _coroutineHandlers = new();
        readonly Dictionary<string, McpProtocol.ToolDefinition> _definitions = new();

        static readonly JsonSerializerOptions DeserializeOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
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
        /// Register a tool whose handler is a coroutine spanning multiple frames.
        /// The handler receives a callback to deliver the result when the coroutine completes.
        /// </summary>
        public void RegisterCoroutine<T>(
            string name,
            string description,
            Func<T, Action<McpProtocol.ToolCallResult>, IEnumerator> handler
        )
        {
            var schema = SchemaGenerator.Generate(typeof(T));
            var requiredFields = SchemaGenerator.GetRequiredFields(typeof(T));

            _coroutineHandlers[name] = (args, callback) =>
            {
                foreach (var field in requiredFields)
                {
                    if (args == null || !args.ContainsKey(field) || args[field] is null)
                    {
                        callback(
                            McpProtocol.ToolError($"Required parameter '{field}' is missing.")
                        );
                        return EmptyEnumerator();
                    }
                }

                T typed;
                try
                {
                    typed = (args ?? new JsonObject()).Deserialize<T>(DeserializeOptions);
                }
                catch (Exception ex)
                {
                    callback(McpProtocol.ToolError($"Invalid parameters: {ex.Message}"));
                    return EmptyEnumerator();
                }

                return handler(typed, callback);
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

        public bool HasTool(string name) =>
            _handlers.ContainsKey(name) || _coroutineHandlers.ContainsKey(name);

        public bool IsCoroutine(string name) => _coroutineHandlers.ContainsKey(name);

        /// <summary>
        /// Start a coroutine tool. Returns an IEnumerator to be run via StartCoroutine.
        /// The callback is invoked with the result when the coroutine completes.
        /// </summary>
        public IEnumerator InvokeCoroutine(
            string name,
            JsonObject arguments,
            Action<McpProtocol.ToolCallResult> callback
        )
        {
            if (!_coroutineHandlers.TryGetValue(name, out var handler))
            {
                callback(McpProtocol.ToolError($"Unknown tool: {name}"));
                yield break;
            }

            IEnumerator inner;
            try
            {
                inner = handler(arguments, callback);
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[UnityExplorerMCP] Tool '{name}' failed: {ex}");
                callback(McpProtocol.ToolError($"Tool execution failed: {ex.Message}"));
                yield break;
            }

            while (true)
            {
                object current;
                bool hasMore;
                try
                {
                    hasMore = inner.MoveNext();
                    if (!hasMore)
                        break;
                    current = inner.Current;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError($"[UnityExplorerMCP] Tool '{name}' failed: {ex}");
                    callback(McpProtocol.ToolError($"Tool execution failed: {ex.Message}"));
                    yield break;
                }
                yield return current;
            }
        }

        static IEnumerator EmptyEnumerator()
        {
            yield break;
        }
    }
}

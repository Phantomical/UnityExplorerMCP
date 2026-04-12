using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// JSON-RPC 2.0 and MCP protocol message types.
    /// </summary>
    public static class McpProtocol
    {
        public const string JsonRpcVersion = "2.0";
        public const string McpProtocolVersion = "2024-11-05";

        static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

        #region JSON-RPC Messages

        public class JsonRpcRequest
        {
            [JsonPropertyName("jsonrpc")]
            public string JsonRpc { get; set; } = JsonRpcVersion;

            [JsonPropertyName("id")]
            public object Id { get; set; }

            [JsonPropertyName("method")]
            public string Method { get; set; }

            [JsonPropertyName("params")]
            public JsonObject Params { get; set; }
        }

        public class JsonRpcResponse
        {
            [JsonPropertyName("jsonrpc")]
            public string JsonRpc { get; set; } = JsonRpcVersion;

            [JsonPropertyName("id")]
            public object Id { get; set; }

            [JsonPropertyName("result")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public object Result { get; set; }

            [JsonPropertyName("error")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public JsonRpcError Error { get; set; }
        }

        public class JsonRpcError
        {
            [JsonPropertyName("code")]
            public int Code { get; set; }

            [JsonPropertyName("message")]
            public string Message { get; set; }

            [JsonPropertyName("data")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public object Data { get; set; }
        }

        #endregion

        #region MCP Initialize

        public class InitializeResult
        {
            [JsonPropertyName("protocolVersion")]
            public string ProtocolVersion { get; set; } = McpProtocolVersion;

            [JsonPropertyName("capabilities")]
            public ServerCapabilities Capabilities { get; set; } = new();

            [JsonPropertyName("serverInfo")]
            public ServerInfo ServerInfo { get; set; } = new();
        }

        public class ServerCapabilities
        {
            [JsonPropertyName("tools")]
            [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
            public ToolsCapability Tools { get; set; } = new();
        }

        public class ToolsCapability
        {
            [JsonPropertyName("listChanged")]
            public bool ListChanged { get; set; } = false;
        }

        public class ServerInfo
        {
            [JsonPropertyName("name")]
            public string Name { get; set; } = "unity-explorer-mcp";

            [JsonPropertyName("version")]
            public string Version { get; set; } = "0.1.0";
        }

        #endregion

        #region MCP Tools

        public class ToolDefinition
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("inputSchema")]
            public JsonObject InputSchema { get; set; }
        }

        public class ToolsListResult
        {
            [JsonPropertyName("tools")]
            public List<ToolDefinition> Tools { get; set; } = new();
        }

        public class ToolCallParams
        {
            [JsonPropertyName("name")]
            public string Name { get; set; }

            [JsonPropertyName("arguments")]
            public JsonObject Arguments { get; set; }
        }

        public class ToolCallResult
        {
            [JsonPropertyName("content")]
            public List<ToolContent> Content { get; set; } = new();

            [JsonPropertyName("isError")]
            public bool IsError { get; set; }
        }

        public class ToolContent
        {
            [JsonPropertyName("type")]
            public string Type { get; set; } = "text";

            [JsonPropertyName("text")]
            public string Text { get; set; }
        }

        #endregion

        #region Helpers

        public static JsonRpcResponse Success(object id, object result) =>
            new() { Id = id, Result = result };

        public static JsonRpcResponse Error(
            object id,
            int code,
            string message,
            object data = null
        ) =>
            new()
            {
                Id = id,
                Error = new JsonRpcError
                {
                    Code = code,
                    Message = message,
                    Data = data,
                },
            };

        public static ToolCallResult ToolSuccess(object result)
        {
            string text;
            if (result is string s)
                text = s;
            else if (result is JsonNode node)
                text = node.ToJsonString(IndentedOptions);
            else
                text = JsonSerializer.Serialize(result, result.GetType(), IndentedOptions);
            return new ToolCallResult { Content = new List<ToolContent> { new() { Text = text } } };
        }

        public static ToolCallResult ToolError(string message) =>
            new()
            {
                IsError = true,
                Content = new List<ToolContent> { new() { Text = message } },
            };

        // JSON-RPC error codes
        public const int ParseError = -32700;
        public const int InvalidRequest = -32600;
        public const int MethodNotFound = -32601;
        public const int InvalidParams = -32602;
        public const int InternalError = -32603;

        #endregion
    }
}

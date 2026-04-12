using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// JSON-RPC 2.0 and MCP protocol message types.
    /// </summary>
    public static class McpProtocol
    {
        public const string JsonRpcVersion = "2.0";
        public const string McpProtocolVersion = "2024-11-05";

        #region JSON-RPC Messages

        public class JsonRpcRequest
        {
            [JsonProperty("jsonrpc")]
            public string JsonRpc { get; set; } = JsonRpcVersion;

            [JsonProperty("id")]
            public object Id { get; set; }

            [JsonProperty("method")]
            public string Method { get; set; }

            [JsonProperty("params")]
            public JObject Params { get; set; }
        }

        public class JsonRpcResponse
        {
            [JsonProperty("jsonrpc")]
            public string JsonRpc { get; set; } = JsonRpcVersion;

            [JsonProperty("id")]
            public object Id { get; set; }

            [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
            public object Result { get; set; }

            [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
            public JsonRpcError Error { get; set; }
        }

        public class JsonRpcError
        {
            [JsonProperty("code")]
            public int Code { get; set; }

            [JsonProperty("message")]
            public string Message { get; set; }

            [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
            public object Data { get; set; }
        }

        #endregion

        #region MCP Initialize

        public class InitializeResult
        {
            [JsonProperty("protocolVersion")]
            public string ProtocolVersion { get; set; } = McpProtocolVersion;

            [JsonProperty("capabilities")]
            public ServerCapabilities Capabilities { get; set; } = new();

            [JsonProperty("serverInfo")]
            public ServerInfo ServerInfo { get; set; } = new();
        }

        public class ServerCapabilities
        {
            [JsonProperty("tools", NullValueHandling = NullValueHandling.Ignore)]
            public ToolsCapability Tools { get; set; } = new();
        }

        public class ToolsCapability
        {
            [JsonProperty("listChanged")]
            public bool ListChanged { get; set; } = false;
        }

        public class ServerInfo
        {
            [JsonProperty("name")]
            public string Name { get; set; } = "unity-explorer-mcp";

            [JsonProperty("version")]
            public string Version { get; set; } = "0.1.0";
        }

        #endregion

        #region MCP Tools

        public class ToolDefinition
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("description")]
            public string Description { get; set; }

            [JsonProperty("inputSchema")]
            public JObject InputSchema { get; set; }
        }

        public class ToolsListResult
        {
            [JsonProperty("tools")]
            public List<ToolDefinition> Tools { get; set; } = new();
        }

        public class ToolCallParams
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("arguments")]
            public JObject Arguments { get; set; }
        }

        public class ToolCallResult
        {
            [JsonProperty("content")]
            public List<ToolContent> Content { get; set; } = new();

            [JsonProperty("isError")]
            public bool IsError { get; set; }
        }

        public class ToolContent
        {
            [JsonProperty("type")]
            public string Type { get; set; } = "text";

            [JsonProperty("text")]
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
            string text = result is string s
                ? s
                : JsonConvert.SerializeObject(result, Formatting.Indented);
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace UnityExplorerMCP.Proxy
{
    /// <summary>
    /// stdio MCP server that forwards tool calls to the in-game SSE server.
    /// It keeps trying to connect in the background, serves the last known
    /// tool list while KSP isn't running, and tells the client whenever the
    /// list changes.
    /// </summary>
    sealed class ProxyServer
    {
        const string StatusToolName = "proxy_status";
        const int MaxWaitSeconds = 600;
        static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

        static readonly JsonSerializerOptions OutputJsonOptions = new()
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        static readonly JsonSerializerOptions StatusJsonOptions = new(OutputJsonOptions)
        {
            WriteIndented = true,
        };

        static readonly string[] SupportedProtocolVersions =
        {
            "2024-11-05",
            "2025-03-26",
            "2025-06-18",
            "2025-11-25",
        };

        const string Instructions =
            "These tools control Kerbal Space Program through the UnityExplorerMCP mod. "
            + "They are relayed by a proxy that stays running while KSP starts and stops, "
            + "so tool calls fail with an error while the game isn't running. After "
            + "launching KSP, call proxy_status with wait_seconds to block until the game "
            + "is reachable. The tool list is refreshed each time the game connects; when "
            + "it changes, a note is appended to the next tool result.";

        readonly Uri _baseUri;
        readonly ToolCache _cache;
        readonly HttpClient _http;
        readonly Stream _stdout;
        readonly SemaphoreSlim _stdoutLock = new(1, 1);

        readonly object _lock = new();
        UpstreamConnection _upstream;
        TaskCompletionSource _connected = NewSignal();
        List<JsonObject> _tools;
        DateTime? _connectedSince;
        DateTime? _lastDisconnect;
        string _lastError;
        string _pendingNotice;
        bool _clientInitialized;

        public ProxyServer(Uri baseUri, ToolCache cache)
        {
            _baseUri = baseUri;
            _cache = cache;
            _tools = cache.Load() ?? new List<JsonObject>();
            _http = new HttpClient(
                new SocketsHttpHandler
                {
                    ConnectTimeout = TimeSpan.FromSeconds(2),
                    UseProxy = false,
                }
            )
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _stdout = Console.OpenStandardOutput();
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _ = Task.Run(() => ConnectLoop(ct));

            using var reader = new StreamReader(
                Console.OpenStandardInput(),
                new UTF8Encoding(false)
            );
            while (!ct.IsCancellationRequested)
            {
                string line = await reader.ReadLineAsync(ct);
                if (line == null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // Handle each message concurrently so a slow tool call
                // doesn't hold up pings or other calls.
                _ = Task.Run(() => HandleClientMessage(line, ct));
            }
        }

        #region Upstream connection

        async Task ConnectLoop(CancellationToken ct)
        {
            bool loggedWaiting = false;
            while (!ct.IsCancellationRequested)
            {
                var upstream = new UpstreamConnection(_http, _baseUri);
                try
                {
                    await upstream.OpenAsync(ct);
                    var tools = await Handshake(upstream, ct);
                    OnConnected(upstream, tools);
                    loggedWaiting = false;

                    await upstream.Completion;
                    OnDisconnected(upstream, "the game closed the connection");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    string reason =
                        ex is OperationCanceledException ? "connection timed out" : ex.Message;
                    bool wasConnected;
                    lock (_lock)
                        wasConnected = _upstream == upstream;
                    if (wasConnected)
                        OnDisconnected(upstream, reason);
                    else
                    {
                        lock (_lock)
                            _lastError = reason;
                        if (!loggedWaiting)
                        {
                            Log.Info($"Waiting for KSP at {_baseUri} ({reason})");
                            loggedWaiting = true;
                        }
                    }
                }
                finally
                {
                    upstream.Dispose();
                }

                try
                {
                    await Task.Delay(RetryInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        static async Task<List<JsonObject>> Handshake(
            UpstreamConnection upstream,
            CancellationToken ct
        )
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var init = await upstream.RequestAsync(
                Request(
                    "initialize",
                    new JsonObject
                    {
                        ["protocolVersion"] = "2024-11-05",
                        ["capabilities"] = new JsonObject(),
                        ["clientInfo"] = new JsonObject
                        {
                            ["name"] = "unity-explorer-mcp-proxy",
                            ["version"] = Version,
                        },
                    }
                ),
                timeout.Token
            );
            ThrowIfError(init);

            await upstream.NotifyAsync(
                new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" },
                timeout.Token
            );

            var list = await upstream.RequestAsync(Request("tools/list", null), timeout.Token);
            ThrowIfError(list);

            return (list["result"]?["tools"] as JsonArray ?? new JsonArray())
                .OfType<JsonObject>()
                .Select(t => (JsonObject)t.DeepClone())
                .ToList();
        }

        void OnConnected(UpstreamConnection upstream, List<JsonObject> tools)
        {
            string notice;
            bool notify;
            lock (_lock)
            {
                _upstream = upstream;
                _connectedSince = DateTime.Now;
                _lastError = null;

                notice = DescribeChanges(_tools, tools);
                if (notice != null)
                {
                    _tools = tools;
                    _pendingNotice = notice;
                }
                notify = notice != null && _clientInitialized;
                _connected.TrySetResult();
            }

            Log.Info($"Connected to KSP at {_baseUri} ({tools.Count} tools)");
            if (notice != null)
            {
                Log.Info(notice);
                _cache.Save(tools);
            }
            if (notify)
                _ = SendToolListChanged();
        }

        void OnDisconnected(UpstreamConnection upstream, string reason)
        {
            lock (_lock)
            {
                if (_upstream != upstream)
                    return;
                _upstream = null;
                _connectedSince = null;
                _lastDisconnect = DateTime.Now;
                _lastError = reason;
                _connected = NewSignal();
            }
            Log.Info($"Disconnected from KSP: {reason}");
        }

        /// <summary>
        /// Summarise how the tool list changed, or return null if it didn't.
        /// </summary>
        static string DescribeChanges(List<JsonObject> before, List<JsonObject> after)
        {
            var old = before.ToDictionary(ToolName, t => t.ToJsonString());
            var now = after.ToDictionary(ToolName, t => t.ToJsonString());

            var added = now.Keys.Where(k => !old.ContainsKey(k)).ToList();
            var removed = old.Keys.Where(k => !now.ContainsKey(k)).ToList();
            var changed = now
                .Keys.Where(k => old.TryGetValue(k, out var o) && o != now[k])
                .ToList();
            if (added.Count == 0 && removed.Count == 0 && changed.Count == 0)
                return null;
            if (old.Count == 0)
                return $"KSP connected and {added.Count} tools are now available.";

            var parts = new List<string>();
            if (added.Count > 0)
                parts.Add($"added {Names(added)}");
            if (removed.Count > 0)
                parts.Add($"removed {Names(removed)}");
            if (changed.Count > 0)
                parts.Add($"updated {Names(changed)}");
            return $"The KSP tool list has changed: {string.Join("; ", parts)}.";

            static string Names(List<string> names) =>
                names.Count <= 20 ? string.Join(", ", names) : $"{names.Count} tools";
        }

        static string ToolName(JsonObject tool) => (string)tool["name"] ?? "";

        #endregion

        #region Client messages

        async Task HandleClientMessage(string line, CancellationToken ct)
        {
            JsonObject message;
            try
            {
                message = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException ex)
            {
                await SendError(null, -32700, $"Parse error: {ex.Message}");
                return;
            }
            if (message == null)
            {
                await SendError(null, -32600, "Invalid request");
                return;
            }

            string method = (string)message["method"];
            JsonNode id = message["id"]?.DeepClone();
            bool isRequest = message.ContainsKey("id") && method != null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        await SendResult(id, InitializeResult(message["params"]));
                        break;
                    case "notifications/initialized":
                        lock (_lock)
                            _clientInitialized = true;
                        break;
                    case "ping":
                        await SendResult(id, new JsonObject());
                        break;
                    case "tools/list":
                        await SendResult(id, ToolsListResult());
                        break;
                    case "tools/call":
                        await SendResult(id, await CallTool(message["params"], ct));
                        break;
                    default:
                        // Notifications (including cancellation) and responses
                        // need no reply.
                        if (isRequest)
                            await SendError(id, -32601, $"Method not found: {method}");
                        break;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Log.Error($"Error handling {method}: {ex}");
                if (isRequest)
                    await SendError(id, -32603, ex.Message);
            }
        }

        static JsonObject InitializeResult(JsonNode requestParams)
        {
            string requested = (string)requestParams?["protocolVersion"];
            string version = SupportedProtocolVersions.Contains(requested)
                ? requested
                : SupportedProtocolVersions[0];

            return new JsonObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject { ["listChanged"] = true },
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "unity-explorer-mcp",
                    ["version"] = Version,
                },
                ["instructions"] = Instructions,
            };
        }

        JsonObject ToolsListResult()
        {
            var tools = new JsonArray { (JsonNode)StatusToolDefinition() };
            lock (_lock)
            {
                foreach (var tool in _tools)
                    tools.Add(tool.DeepClone());
            }
            return new JsonObject { ["tools"] = tools };
        }

        async Task<JsonObject> CallTool(JsonNode callParams, CancellationToken ct)
        {
            string name = (string)callParams?["name"];
            JsonObject result;

            if (name == StatusToolName)
            {
                result = await StatusTool(callParams?["arguments"] as JsonObject, ct);
            }
            else
            {
                UpstreamConnection upstream;
                lock (_lock)
                    upstream = _upstream;

                if (upstream == null)
                    result = ToolError(NotConnectedMessage());
                else
                    result = await ForwardToolCall(upstream, callParams, ct);
            }

            AppendPendingNotice(result);
            return result;
        }

        static async Task<JsonObject> ForwardToolCall(
            UpstreamConnection upstream,
            JsonNode callParams,
            CancellationToken ct
        )
        {
            JsonObject response;
            try
            {
                response = await upstream.RequestAsync(
                    Request("tools/call", callParams?.DeepClone()),
                    ct
                );
            }
            catch (UpstreamClosedException)
            {
                return ToolError(
                    "Lost the connection to KSP before the tool returned a result. "
                        + "The game may have exited, crashed, or reloaded the plugin; "
                        + "call proxy_status to check whether it is reachable again."
                );
            }

            if (response["error"] is JsonObject error)
            {
                return ToolError(
                    $"KSP returned an error: {(string)error["message"] ?? error.ToJsonString()}"
                );
            }
            // Detach the result so it can be re-parented into the reply.
            var result = response["result"] as JsonObject;
            response.Remove("result");
            return result ?? ToolError("KSP returned no result.");
        }

        async Task<JsonObject> StatusTool(JsonObject args, CancellationToken ct)
        {
            double wait = 0;
            if (args?["wait_seconds"] is JsonValue waitValue)
                waitValue.TryGetValue(out wait);
            wait = Math.Clamp(wait, 0, MaxWaitSeconds);

            Task connected;
            lock (_lock)
                connected = _connected.Task;
            if (wait > 0 && !connected.IsCompleted)
                await Task.WhenAny(connected, Task.Delay(TimeSpan.FromSeconds(wait), ct));

            JsonObject status;
            lock (_lock)
            {
                status = new JsonObject
                {
                    ["connected"] = _upstream != null,
                    ["server"] = _baseUri.ToString(),
                    ["tool_count"] = _tools.Count,
                };
                if (_connectedSince is DateTime since)
                    status["connected_since"] = since.ToString("O");
                if (_lastDisconnect is DateTime lost)
                    status["last_disconnect"] = lost.ToString("O");
                if (_upstream == null && _lastError != null)
                    status["last_error"] = _lastError;
            }

            return new JsonObject
            {
                ["content"] = new JsonArray { TextContent(status.ToJsonString(StatusJsonOptions)) },
                ["isError"] = false,
            };
        }

        static JsonObject StatusToolDefinition() =>
            new()
            {
                ["name"] = StatusToolName,
                ["description"] =
                    "Report whether KSP is running and reachable by the MCP proxy. Pass "
                    + "wait_seconds to block until the game connects (e.g. right after "
                    + "launching it; KSP can take several minutes to load). The tool list "
                    + "is refreshed whenever the game connects.",
                ["inputSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["wait_seconds"] = new JsonObject
                        {
                            ["type"] = "number",
                            ["description"] =
                                $"Seconds to wait for KSP to connect if it isn't already (0-{MaxWaitSeconds}). Defaults to 0.",
                        },
                    },
                },
            };

        string NotConnectedMessage()
        {
            lock (_lock)
            {
                var sb = new StringBuilder(
                    $"KSP is not running, or the UnityExplorerMCP mod is not reachable at {_baseUri}."
                );
                if (_lastError != null)
                    sb.Append($" Last error: {_lastError}.");
                sb.Append(
                    " Launch the game (the mod starts its server as soon as KSP starts) and "
                        + "call proxy_status with wait_seconds to wait for it."
                );
                return sb.ToString();
            }
        }

        void AppendPendingNotice(JsonObject result)
        {
            string notice;
            lock (_lock)
            {
                notice = _pendingNotice;
                _pendingNotice = null;
            }
            if (notice == null)
                return;

            if (result["content"] is not JsonArray content)
                result["content"] = content = new JsonArray();
            content.Add(TextContent($"[proxy] {notice} Re-list tools to see the current set."));
        }

        #endregion

        #region Output

        Task SendToolListChanged() =>
            Send(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/tools/list_changed",
                }
            );

        Task SendResult(JsonNode id, JsonNode result) =>
            Send(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = result,
                }
            );

        Task SendError(JsonNode id, int code, string message) =>
            Send(
                new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
                }
            );

        async Task Send(JsonObject message)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message.ToJsonString(OutputJsonOptions) + "\n");
            await _stdoutLock.WaitAsync();
            try
            {
                await _stdout.WriteAsync(bytes);
                await _stdout.FlushAsync();
            }
            finally
            {
                _stdoutLock.Release();
            }
        }

        #endregion

        static JsonObject Request(string method, JsonNode requestParams)
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (requestParams != null)
                message["params"] = requestParams;
            return message;
        }

        static void ThrowIfError(JsonObject response)
        {
            if (response["error"] is JsonObject error)
                throw new InvalidOperationException(
                    $"KSP rejected the request: {(string)error["message"]}"
                );
        }

        static JsonObject ToolError(string message) =>
            new()
            {
                ["content"] = new JsonArray { TextContent(message) },
                ["isError"] = true,
            };

        // Typed as JsonNode so JsonArray uses its non-generic (AOT-safe) Add.
        static JsonNode TextContent(string text) =>
            new JsonObject { ["type"] = "text", ["text"] = text };

        static TaskCompletionSource NewSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        static string Version =>
            typeof(ProxyServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using UnityEngine;
using UnityExplorerMCP.Tools;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// Represents a single MCP client connection over SSE.
    /// Handles the SSE event stream and processes JSON-RPC requests.
    /// </summary>
    public class McpSession
    {
        public string Id { get; }
        public bool IsAlive { get; private set; } = true;

        readonly ToolRegistry _tools;
        readonly ConcurrentQueue<string> _outgoing = new();
        readonly ManualResetEventSlim _outgoingSignal = new(false);
        HttpListenerResponse _sseResponse;
        StreamWriter _sseWriter;

        static readonly JsonSerializerOptions SerializerOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public McpSession(string id, ToolRegistry tools)
        {
            Id = id;
            _tools = tools;
        }

        /// <summary>
        /// Attach the SSE response stream. Called when the client GETs /sse.
        /// This method blocks the calling thread and writes SSE events until closed.
        /// </summary>
        public void AttachSseStream(HttpListenerResponse response)
        {
            _sseResponse = response;
            response.ContentType = "text/event-stream";
            response.Headers.Add("Cache-Control", "no-cache");
            response.Headers.Add("Connection", "keep-alive");
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            _sseWriter = new StreamWriter(response.OutputStream, new UTF8Encoding(false))
            {
                AutoFlush = true,
            };

            // Send the endpoint event telling the client where to POST messages
            SendSseEvent("endpoint", $"/message?sessionId={Id}");

            // SSE write loop — blocks until session is closed
            try
            {
                while (IsAlive)
                {
                    _outgoingSignal.Wait(TimeSpan.FromSeconds(15));
                    _outgoingSignal.Reset();

                    while (_outgoing.TryDequeue(out var json))
                    {
                        WriteSseMessage(json);
                    }

                    // Send keepalive comment to prevent connection timeout
                    if (IsAlive)
                    {
                        try
                        {
                            _sseWriter.Write(":keepalive\n\n");
                        }
                        catch
                        {
                            Close();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.Log($"[UnityExplorerMCP] SSE stream ended for session {Id}: {ex.Message}");
            }
            finally
            {
                Close();
            }
        }

        /// <summary>
        /// Handle an incoming JSON-RPC request (from POST /message).
        /// Dispatches to the main thread for tool execution and sends the response via SSE.
        /// </summary>
        public void HandleMessage(string body)
        {
            McpProtocol.JsonRpcRequest request;
            try
            {
                request = JsonSerializer.Deserialize<McpProtocol.JsonRpcRequest>(
                    body,
                    SerializerOptions
                );
            }
            catch (Exception ex)
            {
                SendResponse(
                    McpProtocol.Error(null, McpProtocol.ParseError, $"Parse error: {ex.Message}")
                );
                return;
            }

            if (request == null || string.IsNullOrEmpty(request.Method))
            {
                SendResponse(
                    McpProtocol.Error(request?.Id, McpProtocol.InvalidRequest, "Invalid request")
                );
                return;
            }

            // Handle protocol methods
            switch (request.Method)
            {
                case "initialize":
                    HandleInitialize(request);
                    break;
                case "notifications/initialized":
                    // Client acknowledgment — no response needed
                    break;
                case "ping":
                    SendResponse(McpProtocol.Success(request.Id, new JsonObject()));
                    break;
                case "tools/list":
                    HandleToolsList(request);
                    break;
                case "tools/call":
                    HandleToolCall(request);
                    break;
                default:
                    SendResponse(
                        McpProtocol.Error(
                            request.Id,
                            McpProtocol.MethodNotFound,
                            $"Method not found: {request.Method}"
                        )
                    );
                    break;
            }
        }

        void HandleInitialize(McpProtocol.JsonRpcRequest request)
        {
            SendResponse(McpProtocol.Success(request.Id, new McpProtocol.InitializeResult()));
        }

        void HandleToolsList(McpProtocol.JsonRpcRequest request)
        {
            var result = new McpProtocol.ToolsListResult { Tools = _tools.ListTools() };
            SendResponse(McpProtocol.Success(request.Id, result));
        }

        void HandleToolCall(McpProtocol.JsonRpcRequest request)
        {
            var callParams = request.Params?.Deserialize<McpProtocol.ToolCallParams>(
                SerializerOptions
            );
            if (callParams == null || string.IsNullOrEmpty(callParams.Name))
            {
                SendResponse(
                    McpProtocol.Error(request.Id, McpProtocol.InvalidParams, "Missing tool name")
                );
                return;
            }

            if (!_tools.HasTool(callParams.Name))
            {
                SendResponse(
                    McpProtocol.Error(
                        request.Id,
                        McpProtocol.MethodNotFound,
                        $"Unknown tool: {callParams.Name}"
                    )
                );
                return;
            }

            if (_tools.IsCoroutine(callParams.Name))
            {
                // Coroutine tool: dispatch to main thread without blocking.
                // The coroutine runs over multiple frames and delivers its result via callback.
                var args = callParams.Arguments ?? new JsonObject();
                MainThreadDispatcher.Instance.Enqueue(() =>
                {
                    var coroutine = _tools.InvokeCoroutine(
                        callParams.Name,
                        args,
                        toolResult =>
                        {
                            var json = JsonSerializer.Serialize(
                                McpProtocol.Success(request.Id, toolResult),
                                SerializerOptions
                            );
                            EnqueueRaw(json);
                        }
                    );
                    MainThreadDispatcher.Instance.StartCoroutine(coroutine);
                });
                return;
            }

            // Synchronous tool: dispatch to main thread and block until complete
            var result = MainThreadDispatcher.Instance.EnqueueAndWait(() =>
            {
                var toolResult = _tools.Invoke(
                    callParams.Name,
                    callParams.Arguments ?? new JsonObject()
                );
                return JsonSerializer.Serialize(
                    McpProtocol.Success(request.Id, toolResult),
                    SerializerOptions
                );
            });

            // Result is already serialized JSON — send directly
            EnqueueRaw(result);
        }

        void SendResponse(McpProtocol.JsonRpcResponse response)
        {
            string json = JsonSerializer.Serialize(response, SerializerOptions);
            EnqueueRaw(json);
        }

        void EnqueueRaw(string json)
        {
            _outgoing.Enqueue(json);
            _outgoingSignal.Set();
        }

        void WriteSseMessage(string json)
        {
            try
            {
                _sseWriter.Write($"event: message\ndata: {json}\n\n");
            }
            catch
            {
                Close();
            }
        }

        void SendSseEvent(string eventType, string data)
        {
            try
            {
                _sseWriter.Write($"event: {eventType}\ndata: {data}\n\n");
            }
            catch
            {
                Close();
            }
        }

        public void Close()
        {
            if (!IsAlive)
                return;
            IsAlive = false;
            _outgoingSignal.Set();
            try
            {
                _sseWriter?.Close();
            }
            catch { }
            try
            {
                _sseResponse?.Close();
            }
            catch { }
        }
    }
}

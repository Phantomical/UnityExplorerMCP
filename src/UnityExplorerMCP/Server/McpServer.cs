using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityExplorerMCP.Tools;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// HTTP server implementing MCP SSE transport.
    /// Listens on the configured port and manages client sessions.
    /// </summary>
    public class McpServer
    {
        // Idle keep-alive connections are dropped after this long.
        const int IdleTimeoutMs = 120_000;

        readonly int _port;
        readonly ToolRegistry _tools;
        readonly ConcurrentDictionary<string, McpSession> _sessions = new();
        readonly ConcurrentDictionary<TcpClient, byte> _connections = new();
        readonly List<TcpListener> _listeners = new();
        volatile bool _running;

        public McpServer(int port, ToolRegistry tools)
        {
            _port = port;
            _tools = tools;
        }

        public void Start()
        {
            if (_running)
                return;

            _running = true;

            // Request handling deliberately avoids the ThreadPool (which Mono's
            // HttpListener and async sockets depend on): other mods can starve
            // it, and SSE sessions would hold pool threads indefinitely.
            StartListener(IPAddress.Loopback, required: true);
            StartListener(IPAddress.IPv6Loopback, required: false);

            Debug.Log($"[UnityExplorerMCP] MCP server started on port {_port}");
        }

        void StartListener(IPAddress address, bool required)
        {
            var listener = new TcpListener(address, _port);
            try
            {
                listener.Start();
            }
            catch (SocketException ex) when (!required)
            {
                Debug.LogWarning(
                    $"[UnityExplorerMCP] Could not listen on {address}:{_port}: {ex.Message}"
                );
                return;
            }

            _listeners.Add(listener);
            new Thread(() => ListenLoop(listener))
            {
                Name = $"McpServer-Listener ({address})",
                IsBackground = true,
            }.Start();
        }

        public void Stop()
        {
            _running = false;

            foreach (var listener in _listeners)
            {
                try
                {
                    listener.Stop();
                }
                catch { }
            }
            _listeners.Clear();

            foreach (var session in _sessions.Values)
                session.Close();
            _sessions.Clear();

            foreach (var client in _connections.Keys)
            {
                try
                {
                    client.Close();
                }
                catch { }
            }
            _connections.Clear();

            Debug.Log("[UnityExplorerMCP] MCP server stopped");
        }

        void ListenLoop(TcpListener listener)
        {
            while (_running)
            {
                try
                {
                    var client = listener.AcceptTcpClient();
                    _connections[client] = 0;
                    new Thread(() => HandleConnection(client))
                    {
                        Name = "McpServer-Connection",
                        IsBackground = true,
                    }.Start();
                }
                catch (Exception ex)
                    when (!_running && ex is SocketException or ObjectDisposedException)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    if (_running)
                        McpLog.Error($"[UnityExplorerMCP] Listener error: {ex}");
                }
            }
        }

        void HandleConnection(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var network = client.GetStream();
                network.ReadTimeout = IdleTimeoutMs;
                var input = new BufferedStream(network);

                while (_running)
                {
                    var request = MiniHttp.ReadRequest(input);
                    if (request == null)
                        break;

                    if (!HandleRequest(request, network))
                        break;
                }
            }
            catch (IOException)
            {
                // Client disconnected or idle timeout
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (_running)
                    McpLog.Error($"[UnityExplorerMCP] Connection error: {ex}");
            }
            finally
            {
                _connections.TryRemove(client, out _);
                try
                {
                    client.Close();
                }
                catch { }
            }
        }

        /// <summary>
        /// Handle a single request. Returns whether the connection may be reused.
        /// </summary>
        bool HandleRequest(HttpRequest request, Stream output)
        {
            try
            {
                // Handle CORS preflight
                if (request.Method == "OPTIONS")
                {
                    MiniHttp.WriteResponse(output, 204, request.KeepAlive);
                    return request.KeepAlive;
                }

                string path = request.Path.TrimEnd('/');

                switch (path)
                {
                    case "/sse":
                        HandleSseConnect(request, output);
                        return false;
                    case "/message":
                        HandleMessage(request, output);
                        return request.KeepAlive;
                    default:
                        MiniHttp.WriteResponse(output, 404, request.KeepAlive);
                        return request.KeepAlive;
                }
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception ex)
            {
                McpLog.Error($"[UnityExplorerMCP] Request handler error: {ex}");
                try
                {
                    MiniHttp.WriteResponse(output, 500, false);
                }
                catch { }
                return false;
            }
        }

        void HandleSseConnect(HttpRequest request, Stream output)
        {
            if (request.Method != "GET")
            {
                MiniHttp.WriteResponse(output, 405, false);
                return;
            }

            string sessionId = Guid.NewGuid().ToString("N");
            var session = new McpSession(sessionId, _tools);
            _sessions[sessionId] = session;

            McpLog.Info($"[UnityExplorerMCP] New SSE session: {sessionId}");

            try
            {
                // This blocks until the session ends
                session.AttachSseStream(MiniHttp.BeginEventStream(output));
            }
            finally
            {
                session.Close();
                _sessions.TryRemove(sessionId, out _);
                McpLog.Info($"[UnityExplorerMCP] SSE session ended: {sessionId}");
            }
        }

        void HandleMessage(HttpRequest request, Stream output)
        {
            if (request.Method != "POST")
            {
                MiniHttp.WriteResponse(output, 405, request.KeepAlive);
                return;
            }

            request.Query.TryGetValue("sessionId", out string sessionId);
            if (
                string.IsNullOrEmpty(sessionId)
                || !_sessions.TryGetValue(sessionId, out var session)
            )
            {
                MiniHttp.WriteResponse(
                    output,
                    400,
                    request.KeepAlive,
                    "text/plain; charset=utf-8",
                    "Invalid or missing sessionId"
                );
                return;
            }

            if (!session.IsAlive)
            {
                MiniHttp.WriteResponse(
                    output,
                    410,
                    request.KeepAlive,
                    "text/plain; charset=utf-8",
                    "Session is closed"
                );
                return;
            }

            string body = Encoding.UTF8.GetString(request.Body);

            // The reply goes out over SSE, so acknowledge first; sync tools
            // block this thread until the main thread has run them.
            MiniHttp.WriteResponse(output, 202, request.KeepAlive);
            session.HandleMessage(body);
        }
    }
}

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
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
        readonly int _port;
        readonly ToolRegistry _tools;
        readonly ConcurrentDictionary<string, McpSession> _sessions = new();
        HttpListener _listener;
        Thread _listenerThread;
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

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://+:{_port}/");
            _running = true;

            try
            {
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                // Fallback to localhost-only if wildcard binding fails (requires admin on Windows)
                Debug.LogWarning(
                    $"[UnityExplorerMCP] Could not bind to +:{_port} ({ex.Message}), falling back to localhost"
                );
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{_port}/");
                _listener.Start();
            }

            _listenerThread = new Thread(ListenLoop)
            {
                Name = "McpServer-Listener",
                IsBackground = true,
            };
            _listenerThread.Start();

            Debug.Log($"[UnityExplorerMCP] MCP server started on port {_port}");
        }

        public void Stop()
        {
            _running = false;

            foreach (var session in _sessions.Values)
                session.Close();
            _sessions.Clear();

            try
            {
                _listener?.Stop();
            }
            catch { }
            try
            {
                _listener?.Close();
            }
            catch { }

            Debug.Log("[UnityExplorerMCP] MCP server stopped");
        }

        void ListenLoop()
        {
            while (_running)
            {
                try
                {
                    var context = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(context));
                }
                catch (HttpListenerException) when (!_running)
                {
                    // Expected when stopping
                }
                catch (ObjectDisposedException) when (!_running)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    if (_running)
                        Debug.LogError($"[UnityExplorerMCP] Listener error: {ex}");
                }
            }
        }

        void HandleRequest(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                // Handle CORS preflight
                if (request.HttpMethod == "OPTIONS")
                {
                    SetCorsHeaders(response);
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                SetCorsHeaders(response);

                string path = request.Url.AbsolutePath.TrimEnd('/');

                switch (path)
                {
                    case "/sse":
                        HandleSseConnect(request, response);
                        break;
                    case "/message":
                        HandleMessage(request, response);
                        break;
                    default:
                        response.StatusCode = 404;
                        response.Close();
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[UnityExplorerMCP] Request handler error: {ex}");
                try
                {
                    response.StatusCode = 500;
                    response.Close();
                }
                catch { }
            }
        }

        void HandleSseConnect(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            string sessionId = Guid.NewGuid().ToString("N");
            var session = new McpSession(sessionId, _tools);
            _sessions[sessionId] = session;

            Debug.Log($"[UnityExplorerMCP] New SSE session: {sessionId}");

            // This blocks until the session ends
            session.AttachSseStream(response);

            _sessions.TryRemove(sessionId, out _);
            Debug.Log($"[UnityExplorerMCP] SSE session ended: {sessionId}");
        }

        void HandleMessage(HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod != "POST")
            {
                response.StatusCode = 405;
                response.Close();
                return;
            }

            string sessionId = request.QueryString["sessionId"];
            if (
                string.IsNullOrEmpty(sessionId)
                || !_sessions.TryGetValue(sessionId, out var session)
            )
            {
                response.StatusCode = 400;
                WriteText(response, "Invalid or missing sessionId");
                return;
            }

            if (!session.IsAlive)
            {
                response.StatusCode = 410;
                WriteText(response, "Session is closed");
                return;
            }

            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding))
                body = reader.ReadToEnd();

            session.HandleMessage(body);

            response.StatusCode = 202;
            response.Close();
        }

        static void SetCorsHeaders(HttpListenerResponse response)
        {
            response.Headers.Add("Access-Control-Allow-Origin", "*");
            response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
        }

        static void WriteText(HttpListenerResponse response, string text)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
    }
}

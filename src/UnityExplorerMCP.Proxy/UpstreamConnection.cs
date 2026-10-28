using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace UnityExplorerMCP.Proxy
{
    /// <summary>
    /// One MCP SSE session with the in-game server. A new instance is created
    /// for every connection attempt; once the event stream ends the instance
    /// is dead and all outstanding requests fail.
    /// </summary>
    sealed class UpstreamConnection : IDisposable
    {
        // The server sends a keepalive every 15s, so silence this long means
        // the connection is gone even if the socket hasn't noticed yet.
        static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
        static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(30);

        readonly HttpClient _http;
        readonly Uri _baseUri;
        readonly CancellationTokenSource _cts = new();
        readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> _pending = new();
        readonly TaskCompletionSource<Uri> _endpoint = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        long _nextId;
        Task _readTask;

        public UpstreamConnection(HttpClient http, Uri baseUri)
        {
            _http = http;
            _baseUri = baseUri;
        }

        /// <summary>Completes when the event stream has ended.</summary>
        public Task Completion => _readTask ?? Task.CompletedTask;

        /// <summary>
        /// Open the SSE stream and wait for the server to announce its message
        /// endpoint. Throws if the server isn't reachable.
        /// </summary>
        public async Task OpenAsync(CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "sse"));
            request.Headers.Accept.ParseAdd("text/event-stream");

            var response = await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );
            response.EnsureSuccessStatusCode();
            var stream = await response.Content.ReadAsStreamAsync(ct);

            _readTask = Task.Run(() => ReadLoop(response, stream));

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(TimeSpan.FromSeconds(10));
            using (linked.Token.Register(() => _endpoint.TrySetCanceled()))
                await _endpoint.Task;
        }

        /// <summary>
        /// Send a JSON-RPC request and wait for its response message. The id
        /// of <paramref name="message"/> is replaced with one unique to this
        /// connection.
        /// </summary>
        public async Task<JsonObject> RequestAsync(JsonObject message, CancellationToken ct)
        {
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonObject>(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _pending[id] = tcs;
            message["id"] = id;

            try
            {
                if (_cts.IsCancellationRequested)
                    throw new UpstreamClosedException();

                await PostAsync(message, ct);
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                    return await tcs.Task;
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        public Task NotifyAsync(JsonObject message, CancellationToken ct) => PostAsync(message, ct);

        async Task PostAsync(JsonObject message, CancellationToken ct)
        {
            var endpoint = await _endpoint.Task;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
            linked.CancelAfter(PostTimeout);

            var content = new StringContent(
                message.ToJsonString(),
                Encoding.UTF8,
                "application/json"
            );
            try
            {
                using var response = await _http.PostAsync(endpoint, content, linked.Token);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Close();
                throw new UpstreamClosedException(ex);
            }
        }

        async Task ReadLoop(HttpResponseMessage response, Stream stream)
        {
            try
            {
                using (response)
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    string eventType = null;
                    var data = new StringBuilder();

                    while (true)
                    {
                        string line;
                        using (
                            var idle = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
                        )
                        {
                            idle.CancelAfter(IdleTimeout);
                            line = await reader.ReadLineAsync(idle.Token);
                        }
                        if (line == null)
                            break;

                        if (line.Length == 0)
                        {
                            if (data.Length > 0)
                                DispatchEvent(eventType ?? "message", data.ToString());
                            eventType = null;
                            data.Clear();
                            continue;
                        }

                        if (line[0] == ':')
                            continue;

                        int colon = line.IndexOf(':');
                        string field = colon < 0 ? line : line.Substring(0, colon);
                        string value = colon < 0 ? "" : line.Substring(colon + 1);
                        if (value.StartsWith(' '))
                            value = value.Substring(1);

                        if (field == "event")
                            eventType = value;
                        else if (field == "data")
                        {
                            if (data.Length > 0)
                                data.Append('\n');
                            data.Append(value);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Any failure here just means the connection is gone.
            }
            finally
            {
                Close();
            }
        }

        void DispatchEvent(string eventType, string data)
        {
            if (eventType == "endpoint")
            {
                _endpoint.TrySetResult(new Uri(_baseUri, data));
                return;
            }
            if (eventType != "message")
                return;

            JsonObject message;
            try
            {
                message = JsonNode.Parse(data) as JsonObject;
            }
            catch (Exception ex)
            {
                Log.Warn($"Ignoring malformed message from KSP: {ex.Message}");
                return;
            }

            // Only responses are expected; the server sends no requests or
            // notifications of its own.
            if (message?["id"] is JsonValue idValue && idValue.TryGetValue(out long id))
            {
                if (_pending.TryGetValue(id, out var tcs))
                    tcs.TrySetResult(message);
            }
        }

        public void Close()
        {
            if (_cts.IsCancellationRequested)
                return;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException) { }

            _endpoint.TrySetException(new UpstreamClosedException());
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new UpstreamClosedException());
        }

        public void Dispose() => Close();
    }

    sealed class UpstreamClosedException : Exception
    {
        public UpstreamClosedException(Exception inner = null)
            : base("Connection to KSP was lost", inner) { }
    }
}

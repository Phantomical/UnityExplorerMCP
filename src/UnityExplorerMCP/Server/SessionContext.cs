using System;

namespace UnityExplorerMCP.Server
{
    /// <summary>
    /// Ambient holder for the current MCP session id, set by <see cref="McpSession"/>
    /// around tool dispatch so handlers can scope per-session state. ThreadStatic
    /// because Unity's main thread runs one tool invocation at a time.
    /// </summary>
    public static class SessionContext
    {
        [ThreadStatic]
        static string _current;

        public static string Current => _current;

        public static IDisposable Enter(string sessionId)
        {
            var prev = _current;
            _current = sessionId;
            return new Scope(prev);
        }

        public static event Action<string> Closed;

        public static void RaiseClosed(string sessionId) => Closed?.Invoke(sessionId);

        sealed class Scope : IDisposable
        {
            readonly string _prev;
            bool _disposed;

            public Scope(string prev)
            {
                _prev = prev;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                _current = _prev;
            }
        }
    }
}

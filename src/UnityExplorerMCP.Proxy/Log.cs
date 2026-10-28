using System;

namespace UnityExplorerMCP.Proxy
{
    /// <summary>
    /// Diagnostics go to stderr; stdout carries the MCP protocol.
    /// </summary>
    static class Log
    {
        static readonly object Lock = new();

        public static void Info(string message) => Write("info", message);

        public static void Warn(string message) => Write("warn", message);

        public static void Error(string message) => Write("error", message);

        static void Write(string level, string message)
        {
            lock (Lock)
                Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}");
        }
    }
}

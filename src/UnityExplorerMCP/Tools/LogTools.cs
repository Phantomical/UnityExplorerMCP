using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class LogTools
    {
        readonly ToolRegistry _tools;
        readonly List<LogEntry> _logBuffer = new();
        const int MaxBufferSize = 1000;
        bool _listening;

        struct LogEntry
        {
            public string Message;
            public string StackTrace;
            public LogType Type;
            public float Timestamp;
            public int FrameCount;
        }

        public LogTools(ToolRegistry tools)
        {
            _tools = tools;
        }

        #region Parameter Types

        public struct GetLogsParams
        {
            [McpParam("Number of most recent log entries (default 50, max 500)")]
            public int? Count { get; set; }

            [McpParam(
                "Filter by log type. Default: all.",
                EnumValues = new[] { "log", "warning", "error", "exception", "assert" }
            )]
            public string LogType { get; set; }

            [McpParam("Only return logs after this Time.realtimeSinceStartup value.")]
            public float? SinceTimestamp { get; set; }
        }

        #endregion

        public void Register()
        {
            StartListening();

            _tools.Register<GetLogsParams>(
                "get_logs",
                "Get recent Unity debug log messages. The server captures logs via Application.logMessageReceived into a ring buffer.",
                GetLogs
            );
        }

        void StartListening()
        {
            if (_listening)
                return;
            _listening = true;
            Application.logMessageReceived += OnLogMessage;
        }

        void OnLogMessage(string message, string stackTrace, LogType type)
        {
            lock (_logBuffer)
            {
                _logBuffer.Add(
                    new LogEntry
                    {
                        Message = message,
                        StackTrace = stackTrace,
                        Type = type,
                        Timestamp = Time.realtimeSinceStartup,
                        FrameCount = Time.frameCount,
                    }
                );

                if (_logBuffer.Count > MaxBufferSize)
                    _logBuffer.RemoveRange(0, _logBuffer.Count - MaxBufferSize);
            }
        }

        McpProtocol.ToolCallResult GetLogs(GetLogsParams args)
        {
            int count = Math.Min(args.Count ?? 50, 500);
            float sinceTimestamp = args.SinceTimestamp ?? 0;

            LogType? typeFilter = null;
            if (!string.IsNullOrEmpty(args.LogType))
            {
                typeFilter = args.LogType.ToLowerInvariant() switch
                {
                    "log" => UnityEngine.LogType.Log,
                    "warning" => UnityEngine.LogType.Warning,
                    "error" => UnityEngine.LogType.Error,
                    "exception" => UnityEngine.LogType.Exception,
                    "assert" => UnityEngine.LogType.Assert,
                    _ => null,
                };
            }

            List<LogEntry> entries;
            lock (_logBuffer)
            {
                entries = _logBuffer
                    .Where(e => sinceTimestamp <= 0 || e.Timestamp > sinceTimestamp)
                    .Where(e => !typeFilter.HasValue || e.Type == typeFilter.Value)
                    .ToList();
            }

            int totalFiltered = entries.Count;
            var recent = entries.Skip(Math.Max(0, entries.Count - count)).Take(count).ToList();

            var logs = new JsonArray();
            foreach (var entry in recent)
            {
                logs.Add(
                    new JsonObject
                    {
                        ["message"] = entry.Message,
                        ["stackTrace"] = entry.StackTrace,
                        ["logType"] = entry.Type.ToString().ToLowerInvariant(),
                        ["timestamp"] = entry.Timestamp,
                        ["frameCount"] = entry.FrameCount,
                    }
                );
            }

            float oldestTimestamp = 0;
            lock (_logBuffer)
            {
                if (_logBuffer.Count > 0)
                    oldestTimestamp = _logBuffer[0].Timestamp;
            }

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["logs"] = logs,
                    ["totalBuffered"] = _logBuffer.Count,
                    ["oldestTimestamp"] = oldestTimestamp,
                }
            );
        }
    }
}

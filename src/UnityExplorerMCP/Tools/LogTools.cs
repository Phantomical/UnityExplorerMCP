using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
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

        public void Register()
        {
            StartListening();

            _tools.Register(
                "get_logs",
                "Get recent Unity debug log messages. The server captures logs via Application.logMessageReceived into a ring buffer.",
                @"{
                    ""count"":          { ""type"": ""integer"", ""description"": ""Number of most recent log entries (default 50, max 500)"" },
                    ""logType"":        { ""type"": ""string"", ""enum"": [""log"",""warning"",""error"",""exception"",""assert""], ""description"": ""Filter by log type. Default: all."" },
                    ""sinceTimestamp"":  { ""type"": ""number"", ""description"": ""Only return logs after this Time.realtimeSinceStartup value."" }
                }",
                null,
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

        McpProtocol.ToolCallResult GetLogs(JObject args)
        {
            int count = Math.Min(GetInt(args, "count", 50), 500);
            string logTypeFilter = GetString(args, "logType");
            float sinceTimestamp = args?["sinceTimestamp"]?.Value<float>() ?? 0;

            LogType? typeFilter = null;
            if (!string.IsNullOrEmpty(logTypeFilter))
            {
                typeFilter = logTypeFilter.ToLowerInvariant() switch
                {
                    "log" => LogType.Log,
                    "warning" => LogType.Warning,
                    "error" => LogType.Error,
                    "exception" => LogType.Exception,
                    "assert" => LogType.Assert,
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

            var logs = new JArray();
            foreach (var entry in recent)
            {
                logs.Add(
                    new JObject
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
                new JObject
                {
                    ["logs"] = logs,
                    ["totalBuffered"] = _logBuffer.Count,
                    ["oldestTimestamp"] = oldestTimestamp,
                }
            );
        }

        static int GetInt(JObject args, string key, int defaultValue = 0)
        {
            var token = args?[key];
            return token != null ? token.Value<int>() : defaultValue;
        }

        static string GetString(JObject args, string key, string defaultValue = null)
        {
            var token = args?[key];
            return token != null && token.Type != JTokenType.Null
                ? token.Value<string>()
                : defaultValue;
        }
    }
}

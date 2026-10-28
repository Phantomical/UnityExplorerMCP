using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace UnityExplorerMCP.Proxy
{
    /// <summary>
    /// Persists the last tool list seen from KSP so the proxy can advertise
    /// the tools before the game has started.
    /// </summary>
    sealed class ToolCache
    {
        readonly string _path;

        public ToolCache(string path)
        {
            _path = path;
        }

        public static string DefaultPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UnityExplorerMCP",
                "tools-cache.json"
            );

        public List<JsonObject> Load()
        {
            if (_path == null || !File.Exists(_path))
                return null;
            try
            {
                var tools = JsonNode.Parse(File.ReadAllText(_path))?["tools"] as JsonArray;
                return tools?.OfType<JsonObject>().Select(t => (JsonObject)t.DeepClone()).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"Ignoring unreadable tool cache {_path}: {ex.Message}");
                return null;
            }
        }

        public void Save(List<JsonObject> tools)
        {
            if (_path == null)
                return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var json = new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(t => t.DeepClone()).ToArray()),
                };

                // Write then rename so a concurrent proxy never reads a partial file.
                string temp = _path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temp, json.ToJsonString());
                File.Move(temp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not write tool cache {_path}: {ex.Message}");
            }
        }
    }
}

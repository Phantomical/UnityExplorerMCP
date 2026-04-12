using System.Text.Json.Nodes;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Base class for tool groups. Provides access to the object registry and tool registry.
    /// </summary>
    public abstract class ToolGroupBase
    {
        protected readonly ObjectRegistry.ObjectRegistry Registry;
        protected readonly ToolRegistry Tools;

        protected ToolGroupBase(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
        {
            Registry = registry;
            Tools = tools;
        }

        public abstract void Register();

        protected static int GetInt(JsonObject args, string key, int defaultValue = 0)
        {
            var node = args?[key];
            if (node == null)
                return defaultValue;
            return node.GetValue<int>();
        }

        protected static string GetString(JsonObject args, string key, string defaultValue = null)
        {
            var node = args?[key];
            if (node == null)
                return defaultValue;
            return node.GetValue<string>();
        }

        protected static bool GetBool(JsonObject args, string key, bool defaultValue = false)
        {
            var node = args?[key];
            if (node == null)
                return defaultValue;
            return node.GetValue<bool>();
        }

        protected static bool HasKey(JsonObject args, string key)
        {
            return args != null && args.ContainsKey(key) && args[key] != null;
        }

        protected McpProtocol.ToolCallResult HandleNotFound(string handle)
        {
            return McpProtocol.ToolError($"Object not found or destroyed (handle: {handle})");
        }
    }
}

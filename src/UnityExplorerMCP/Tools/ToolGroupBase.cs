using Newtonsoft.Json.Linq;
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

        protected static int GetInt(JObject args, string key, int defaultValue = 0)
        {
            var token = args?[key];
            if (token == null)
                return defaultValue;
            return token.Value<int>();
        }

        protected static string GetString(JObject args, string key, string defaultValue = null)
        {
            var token = args?[key];
            if (token == null || token.Type == JTokenType.Null)
                return defaultValue;
            return token.Value<string>();
        }

        protected static bool GetBool(JObject args, string key, bool defaultValue = false)
        {
            var token = args?[key];
            if (token == null)
                return defaultValue;
            return token.Value<bool>();
        }

        protected static bool HasKey(JObject args, string key)
        {
            return args?[key] != null && args[key].Type != JTokenType.Null;
        }

        protected McpProtocol.ToolCallResult HandleNotFound(string handle)
        {
            return McpProtocol.ToolError($"Object not found or destroyed (handle: {handle})");
        }
    }
}

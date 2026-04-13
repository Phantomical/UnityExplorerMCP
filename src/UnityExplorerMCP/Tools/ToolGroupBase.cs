using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Base class for tool groups that need access to the object registry.
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

        protected McpProtocol.ToolCallResult HandleNotFound(string handle)
        {
            return McpProtocol.ToolError($"Object not found or destroyed (handle: {handle})");
        }
    }
}

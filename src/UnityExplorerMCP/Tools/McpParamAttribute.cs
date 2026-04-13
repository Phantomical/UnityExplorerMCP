using System;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Marks a property as an MCP tool parameter and provides schema metadata.
    /// Applied to properties on parameter structs passed to tool handlers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public class McpParamAttribute : Attribute
    {
        public string Description { get; }
        public bool Required { get; set; }
        public string[] EnumValues { get; set; }

        public McpParamAttribute(string description)
        {
            Description = description;
        }
    }
}

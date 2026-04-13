using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnityExplorerMCP.Tools
{
    /// <summary>
    /// Generates MCP-compatible JSON Schema from typed parameter structs.
    /// Properties are discovered via reflection; names are converted to camelCase.
    /// </summary>
    public static class SchemaGenerator
    {
        static readonly JsonNamingPolicy NamingPolicy = JsonNamingPolicy.CamelCase;

        public static JsonObject Generate(Type type)
        {
            var properties = new JsonObject();
            var required = new List<string>();

            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string jsonName = NamingPolicy.ConvertName(prop.Name);
                var paramAttr = prop.GetCustomAttribute<McpParamAttribute>();

                var propSchema = GenerateTypeSchema(prop.PropertyType);

                if (paramAttr != null)
                {
                    if (!string.IsNullOrEmpty(paramAttr.Description))
                        propSchema["description"] = paramAttr.Description;
                    if (paramAttr.EnumValues != null && paramAttr.EnumValues.Length > 0)
                    {
                        var enumArr = new JsonArray();
                        foreach (var v in paramAttr.EnumValues)
                            enumArr.Add((JsonNode)v);
                        propSchema["enum"] = enumArr;
                    }
                    if (paramAttr.Required)
                        required.Add(jsonName);
                }

                properties[jsonName] = propSchema;
            }

            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties };

            if (required.Count > 0)
            {
                var reqArr = new JsonArray();
                foreach (var r in required)
                    reqArr.Add((JsonNode)r);
                schema["required"] = reqArr;
            }

            return schema;
        }

        public static string[] GetRequiredFields(Type type)
        {
            var required = new List<string>();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var paramAttr = prop.GetCustomAttribute<McpParamAttribute>();
                if (paramAttr?.Required == true)
                    required.Add(NamingPolicy.ConvertName(prop.Name));
            }
            return required.ToArray();
        }

        static JsonObject GenerateTypeSchema(Type type)
        {
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null)
                type = underlying;

            if (type == typeof(string))
                return new JsonObject { ["type"] = "string" };
            if (type == typeof(int) || type == typeof(long))
                return new JsonObject { ["type"] = "integer" };
            if (type == typeof(float) || type == typeof(double))
                return new JsonObject { ["type"] = "number" };
            if (type == typeof(bool))
                return new JsonObject { ["type"] = "boolean" };

            if (type.IsArray)
            {
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = GenerateTypeSchema(type.GetElementType()),
                };
            }

            // Nested object type — recurse into its properties
            var nested = new JsonObject { ["type"] = "object" };
            var props = new JsonObject();
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                string name = NamingPolicy.ConvertName(prop.Name);
                var innerSchema = GenerateTypeSchema(prop.PropertyType);
                var innerAttr = prop.GetCustomAttribute<McpParamAttribute>();
                if (innerAttr != null && !string.IsNullOrEmpty(innerAttr.Description))
                    innerSchema["description"] = innerAttr.Description;
                props[name] = innerSchema;
            }
            if (props.Count > 0)
                nested["properties"] = props;

            return nested;
        }
    }
}

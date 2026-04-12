using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using UnityEngine;
using UnityExplorerMCP.ObjectRegistry;

namespace UnityExplorerMCP.Serialization
{
    /// <summary>
    /// Converts C# values to JSON-friendly representations for MCP tool responses.
    /// </summary>
    public class ValueSerializer
    {
        readonly ObjectRegistry.ObjectRegistry _registry;
        const int MaxCollectionItems = 20;
        const int MaxStringLength = 2000;
        const int MaxDepth = 3;

        public ValueSerializer(ObjectRegistry.ObjectRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// Serialize a value to a JsonNode suitable for JSON responses.
        /// Returns the serialized value and optionally an object handle.
        /// </summary>
        public JsonNode Serialize(object value, Type declaredType = null, int depth = 0)
        {
            if (value == null)
                return null;

            var type = value.GetType();
            declaredType ??= type;

            // Primitives
            if (value is bool b)
                return (JsonNode)b;
            if (value is string s)
                return (JsonNode)(
                    s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) + "..." : s
                );
            if (IsNumeric(type))
                return BoxedToNode(value);

            // Enums
            if (type.IsEnum)
                return new JsonObject
                {
                    ["enumValue"] = value.ToString(),
                    ["numericValue"] = BoxedToNode(
                        Convert.ChangeType(value, Enum.GetUnderlyingType(type))
                    ),
                };

            // Unity types
            if (value is Vector2 v2)
                return new JsonObject { ["x"] = v2.x, ["y"] = v2.y };
            if (value is Vector3 v3)
                return new JsonObject
                {
                    ["x"] = v3.x,
                    ["y"] = v3.y,
                    ["z"] = v3.z,
                };
            if (value is Vector4 v4)
                return new JsonObject
                {
                    ["x"] = v4.x,
                    ["y"] = v4.y,
                    ["z"] = v4.z,
                    ["w"] = v4.w,
                };
            if (value is Quaternion q)
                return new JsonObject
                {
                    ["x"] = q.x,
                    ["y"] = q.y,
                    ["z"] = q.z,
                    ["w"] = q.w,
                };
            if (value is Color c)
                return new JsonObject
                {
                    ["r"] = c.r,
                    ["g"] = c.g,
                    ["b"] = c.b,
                    ["a"] = c.a,
                };
            if (value is Color32 c32)
                return new JsonObject
                {
                    ["r"] = (int)c32.r,
                    ["g"] = (int)c32.g,
                    ["b"] = (int)c32.b,
                    ["a"] = (int)c32.a,
                };
            if (value is Rect rect)
                return new JsonObject
                {
                    ["x"] = rect.x,
                    ["y"] = rect.y,
                    ["width"] = rect.width,
                    ["height"] = rect.height,
                };
            if (value is Bounds bounds)
                return new JsonObject
                {
                    ["center"] = Serialize(bounds.center, null, depth + 1),
                    ["size"] = Serialize(bounds.size, null, depth + 1),
                };

            // Type references
            if (value is Type typeRef)
                return new JsonObject
                {
                    ["typeName"] = typeRef.Name,
                    ["typeFullName"] = typeRef.FullName,
                    ["assemblyName"] = typeRef.Assembly.GetName().Name,
                };

            // Prevent infinite recursion
            if (depth >= MaxDepth)
                return ObjectSummary(value);

            // Collections
            if (value is IDictionary dict)
                return SerializeDictionary(dict, depth);
            if (value is IList list)
                return SerializeList(list, depth);
            if (value is IEnumerable enumerable && !(value is string))
                return SerializeEnumerable(enumerable, depth);

            // Unity Object — provide handle
            if (value is UnityEngine.Object unityObj)
                return ObjectSummary(unityObj);

            // Fallback for complex objects — provide handle and toString
            return ObjectSummary(value);
        }

        JsonNode SerializeList(IList list, int depth)
        {
            var items = new JsonArray();
            int count = Math.Min(list.Count, MaxCollectionItems);
            for (int i = 0; i < count; i++)
                items.Add(Serialize(list[i], null, depth + 1));

            return new JsonObject
            {
                ["count"] = list.Count,
                ["items"] = items,
                ["truncated"] = list.Count > MaxCollectionItems,
            };
        }

        JsonNode SerializeDictionary(IDictionary dict, int depth)
        {
            var entries = new JsonArray();
            int i = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (i++ >= MaxCollectionItems)
                    break;
                entries.Add(
                    new JsonObject
                    {
                        ["key"] = Serialize(entry.Key, null, depth + 1),
                        ["value"] = Serialize(entry.Value, null, depth + 1),
                    }
                );
            }

            return new JsonObject
            {
                ["count"] = dict.Count,
                ["entries"] = entries,
                ["truncated"] = dict.Count > MaxCollectionItems,
            };
        }

        JsonNode SerializeEnumerable(IEnumerable enumerable, int depth)
        {
            var items = new JsonArray();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxCollectionItems)
                    break;
                items.Add(Serialize(item, null, depth + 1));
            }

            return new JsonObject { ["items"] = items, ["truncated"] = count > MaxCollectionItems };
        }

        JsonNode ObjectSummary(object value)
        {
            if (value == null)
                return null;

            var result = new JsonObject
            {
                ["type"] = value.GetType().Name,
                ["typeFullName"] = value.GetType().FullName,
            };

            try
            {
                result["toString"] = value.ToString();
            }
            catch
            {
                result["toString"] = $"<{value.GetType().Name}>";
            }

            // Register and provide a handle for further inspection
            if (value is UnityEngine.Object unityObj)
            {
                result["handle"] = _registry.Register(unityObj);
                result["instanceId"] = unityObj.GetInstanceID();
                result["name"] = unityObj.name;
            }
            else
            {
                result["handle"] = _registry.RegisterManaged(value);
            }

            return result;
        }

        /// <summary>
        /// Get a simple string representation of a value suitable for inline display.
        /// </summary>
        public string ToDisplayString(object value)
        {
            if (value == null)
                return "null";
            if (value is string s)
                return s.Length > 200 ? s.Substring(0, 200) + "..." : s;
            if (value is bool b)
                return b ? "true" : "false";
            if (IsNumeric(value.GetType()))
                return value.ToString();
            if (value.GetType().IsEnum)
                return value.ToString();
            if (value is UnityEngine.Object unityObj)
                return unityObj != null
                    ? $"{unityObj.GetType().Name} \"{unityObj.name}\""
                    : "null (destroyed)";

            try
            {
                return value.ToString();
            }
            catch
            {
                return $"<{value.GetType().Name}>";
            }
        }

        /// <summary>
        /// Convert a boxed primitive value to a JsonNode.
        /// </summary>
        internal static JsonNode BoxedToNode(object value)
        {
            if (value == null)
                return null;
            return value switch
            {
                bool b => (JsonNode)b,
                byte v => (JsonNode)v,
                sbyte v => (JsonNode)v,
                short v => (JsonNode)v,
                ushort v => (JsonNode)v,
                int v => (JsonNode)v,
                uint v => (JsonNode)v,
                long v => (JsonNode)v,
                ulong v => (JsonNode)v,
                float v => (JsonNode)v,
                double v => (JsonNode)v,
                decimal v => (JsonNode)v,
                string s => (JsonNode)s,
                _ => (JsonNode)value.ToString(),
            };
        }

        static bool IsNumeric(Type type)
        {
            return type == typeof(byte)
                || type == typeof(sbyte)
                || type == typeof(short)
                || type == typeof(ushort)
                || type == typeof(int)
                || type == typeof(uint)
                || type == typeof(long)
                || type == typeof(ulong)
                || type == typeof(float)
                || type == typeof(double)
                || type == typeof(decimal);
        }
    }
}

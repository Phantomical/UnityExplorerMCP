using System;
using System.Collections;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
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
        /// Serialize a value to a JToken suitable for JSON responses.
        /// Returns the serialized value and optionally an object handle.
        /// </summary>
        public JToken Serialize(object value, Type declaredType = null, int depth = 0)
        {
            if (value == null)
                return JValue.CreateNull();

            var type = value.GetType();
            declaredType ??= type;

            // Primitives
            if (value is bool b)
                return new JValue(b);
            if (value is string s)
                return new JValue(
                    s.Length > MaxStringLength ? s.Substring(0, MaxStringLength) + "..." : s
                );
            if (IsNumeric(type))
                return JToken.FromObject(value);

            // Enums
            if (type.IsEnum)
                return new JObject
                {
                    ["enumValue"] = value.ToString(),
                    ["numericValue"] = JToken.FromObject(
                        Convert.ChangeType(value, Enum.GetUnderlyingType(type))
                    ),
                };

            // Unity types
            if (value is Vector2 v2)
                return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            if (value is Vector3 v3)
                return new JObject
                {
                    ["x"] = v3.x,
                    ["y"] = v3.y,
                    ["z"] = v3.z,
                };
            if (value is Vector4 v4)
                return new JObject
                {
                    ["x"] = v4.x,
                    ["y"] = v4.y,
                    ["z"] = v4.z,
                    ["w"] = v4.w,
                };
            if (value is Quaternion q)
                return new JObject
                {
                    ["x"] = q.x,
                    ["y"] = q.y,
                    ["z"] = q.z,
                    ["w"] = q.w,
                };
            if (value is Color c)
                return new JObject
                {
                    ["r"] = c.r,
                    ["g"] = c.g,
                    ["b"] = c.b,
                    ["a"] = c.a,
                };
            if (value is Color32 c32)
                return new JObject
                {
                    ["r"] = c32.r,
                    ["g"] = c32.g,
                    ["b"] = c32.b,
                    ["a"] = c32.a,
                };
            if (value is Rect rect)
                return new JObject
                {
                    ["x"] = rect.x,
                    ["y"] = rect.y,
                    ["width"] = rect.width,
                    ["height"] = rect.height,
                };
            if (value is Bounds bounds)
                return new JObject
                {
                    ["center"] = Serialize(bounds.center, null, depth + 1),
                    ["size"] = Serialize(bounds.size, null, depth + 1),
                };

            // Type references
            if (value is Type typeRef)
                return new JObject
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

        JToken SerializeList(IList list, int depth)
        {
            var items = new JArray();
            int count = Math.Min(list.Count, MaxCollectionItems);
            for (int i = 0; i < count; i++)
                items.Add(Serialize(list[i], null, depth + 1));

            return new JObject
            {
                ["count"] = list.Count,
                ["items"] = items,
                ["truncated"] = list.Count > MaxCollectionItems,
            };
        }

        JToken SerializeDictionary(IDictionary dict, int depth)
        {
            var entries = new JArray();
            int i = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (i++ >= MaxCollectionItems)
                    break;
                entries.Add(
                    new JObject
                    {
                        ["key"] = Serialize(entry.Key, null, depth + 1),
                        ["value"] = Serialize(entry.Value, null, depth + 1),
                    }
                );
            }

            return new JObject
            {
                ["count"] = dict.Count,
                ["entries"] = entries,
                ["truncated"] = dict.Count > MaxCollectionItems,
            };
        }

        JToken SerializeEnumerable(IEnumerable enumerable, int depth)
        {
            var items = new JArray();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= MaxCollectionItems)
                    break;
                items.Add(Serialize(item, null, depth + 1));
            }

            return new JObject { ["items"] = items, ["truncated"] = count > MaxCollectionItems };
        }

        JToken ObjectSummary(object value)
        {
            if (value == null)
                return JValue.CreateNull();

            var result = new JObject
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

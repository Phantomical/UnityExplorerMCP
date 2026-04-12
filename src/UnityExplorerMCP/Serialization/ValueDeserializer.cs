using System;
using System.Globalization;
using UnityEngine;
using UnityExplorerMCP.ObjectRegistry;

namespace UnityExplorerMCP.Serialization
{
    /// <summary>
    /// Parses string values into C# types based on the target type.
    /// Used for set_value and invoke_method argument parsing.
    /// </summary>
    public class ValueDeserializer
    {
        readonly ObjectRegistry.ObjectRegistry _registry;

        public ValueDeserializer(ObjectRegistry.ObjectRegistry registry)
        {
            _registry = registry;
        }

        /// <summary>
        /// Parse a string value into the specified target type.
        /// </summary>
        public object Deserialize(string value, Type targetType)
        {
            if (value == null || value == "null")
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;
                throw new ArgumentException($"Cannot assign null to value type {targetType.Name}");
            }

            // Handle object references
            if (ObjectHandle.IsUnityHandle(value) || ObjectHandle.IsManagedHandle(value))
            {
                object resolved = _registry.Resolve(value);
                if (resolved != null && targetType.IsAssignableFrom(resolved.GetType()))
                    return resolved;
            }

            // Nullable — unwrap
            Type underlying = Nullable.GetUnderlyingType(targetType);
            if (underlying != null)
                targetType = underlying;

            // Primitives and common types
            if (targetType == typeof(string))
                return value;
            if (targetType == typeof(bool))
                return bool.Parse(value);
            if (targetType == typeof(byte))
                return byte.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(sbyte))
                return sbyte.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(short))
                return short.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(ushort))
                return ushort.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(int))
                return int.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(uint))
                return uint.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(long))
                return long.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(ulong))
                return ulong.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(float))
                return float.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(double))
                return double.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(decimal))
                return decimal.Parse(value, CultureInfo.InvariantCulture);
            if (targetType == typeof(char))
                return value.Length > 0 ? value[0] : '\0';

            // Enums
            if (targetType.IsEnum)
            {
                if (Enum.IsDefined(targetType, value))
                    return Enum.Parse(targetType, value);
                // Try numeric
                if (int.TryParse(value, out int numVal))
                    return Enum.ToObject(targetType, numVal);
                return Enum.Parse(targetType, value, ignoreCase: true);
            }

            // Unity Vector types — support "(x, y, z)" and "x,y,z" formats
            if (targetType == typeof(Vector2))
                return ParseVector2(value);
            if (targetType == typeof(Vector3))
                return ParseVector3(value);
            if (targetType == typeof(Vector4))
                return ParseVector4(value);
            if (targetType == typeof(Quaternion))
            {
                var v = ParseVector4(value);
                return new Quaternion(v.x, v.y, v.z, v.w);
            }

            // Color — support "(r,g,b,a)" or "#RRGGBB" or "#RRGGBBAA"
            if (targetType == typeof(Color))
                return ParseColor(value);

            // Type reference
            if (targetType == typeof(Type))
                return TypeResolver.FindType(value);

            throw new ArgumentException($"Cannot parse '{value}' as {targetType.FullName}");
        }

        static float[] ParseFloats(string value)
        {
            value = value.Trim().TrimStart('(').TrimEnd(')');
            string[] parts = value.Split(',');
            var floats = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                floats[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
            return floats;
        }

        static Vector2 ParseVector2(string value)
        {
            var f = ParseFloats(value);
            return new Vector2(f.Length > 0 ? f[0] : 0, f.Length > 1 ? f[1] : 0);
        }

        static Vector3 ParseVector3(string value)
        {
            var f = ParseFloats(value);
            return new Vector3(
                f.Length > 0 ? f[0] : 0,
                f.Length > 1 ? f[1] : 0,
                f.Length > 2 ? f[2] : 0
            );
        }

        static Vector4 ParseVector4(string value)
        {
            var f = ParseFloats(value);
            return new Vector4(
                f.Length > 0 ? f[0] : 0,
                f.Length > 1 ? f[1] : 0,
                f.Length > 2 ? f[2] : 0,
                f.Length > 3 ? f[3] : 0
            );
        }

        static Color ParseColor(string value)
        {
            value = value.Trim();
            if (value.StartsWith("#"))
            {
                if (ColorUtility.TryParseHtmlString(value, out Color color))
                    return color;
            }

            var f = ParseFloats(value);
            return new Color(
                f.Length > 0 ? f[0] : 0,
                f.Length > 1 ? f[1] : 0,
                f.Length > 2 ? f[2] : 0,
                f.Length > 3 ? f[3] : 1
            );
        }
    }
}

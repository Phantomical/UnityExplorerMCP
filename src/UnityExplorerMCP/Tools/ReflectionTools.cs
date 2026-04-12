using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using UnityExplorerMCP.Serialization;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class ReflectionTools : ToolGroupBase
    {
        readonly ValueSerializer _serializer;
        readonly ValueDeserializer _deserializer;

        const BindingFlags AllFlags =
            BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.Instance
            | BindingFlags.Static;

        public ReflectionTools(ObjectRegistry.ObjectRegistry registry, ToolRegistry tools)
            : base(registry, tools)
        {
            _serializer = new ValueSerializer(registry);
            _deserializer = new ValueDeserializer(registry);
        }

        public override void Register()
        {
            Tools.Register(
                "get_members",
                "List fields, properties, methods, and constructors of an object or type. Auto-evaluates simple field/property values. Supports filtering by member type, scope, and name.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of an object instance to inspect (mutually exclusive with typeName)"" },
                    ""typeName"":     { ""type"": ""string"", ""description"": ""Full type name for static-only inspection (mutually exclusive with objectHandle)"" },
                    ""memberTypes"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Filter: ['field','property','method','constructor']. Default: all."" },
                    ""scope"":        { ""type"": ""string"", ""enum"": [""all"",""instance"",""static""], ""description"": ""Filter by scope. Default: all."" },
                    ""nameFilter"":   { ""type"": ""string"", ""description"": ""Case-insensitive substring filter on member names."" },
                    ""limit"":        { ""type"": ""integer"", ""description"": ""Max results (default 50)"" },
                    ""offset"":       { ""type"": ""integer"", ""description"": ""Pagination offset (default 0)"" }
                }",
                null,
                GetMembers
            );

            Tools.Register(
                "get_value",
                "Get the value of a specific field or property. Supports indexed properties via indexArguments.",
                @"{
                    ""objectHandle"":   { ""type"": ""string"", ""description"": ""Handle of the object instance (omit for static)"" },
                    ""typeName"":       { ""type"": ""string"", ""description"": ""Required if objectHandle is omitted (static access)"" },
                    ""memberName"":     { ""type"": ""string"", ""description"": ""Name of the field or property"" },
                    ""indexArguments"":  { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Index arguments for indexed properties"" }
                }",
                new[] { "memberName" },
                GetValue
            );

            Tools.Register(
                "set_value",
                "Set the value of a field or property. Value is passed as a string and parsed based on the member's type.",
                @"{
                    ""objectHandle"": { ""type"": ""string"", ""description"": ""Handle of the object instance (omit for static)"" },
                    ""typeName"":     { ""type"": ""string"", ""description"": ""For static members"" },
                    ""memberName"":   { ""type"": ""string"", ""description"": ""Name of the field or property"" },
                    ""value"":        { ""type"": ""string"", ""description"": ""Value as a string, parsed using the member's declared type"" }
                }",
                new[] { "memberName", "value" },
                SetValue
            );

            Tools.Register(
                "invoke_method",
                "Invoke a method on an object or type. Arguments are passed as strings and parsed based on parameter types.",
                @"{
                    ""objectHandle"":         { ""type"": ""string"", ""description"": ""Handle of the object instance (omit for static)"" },
                    ""typeName"":             { ""type"": ""string"", ""description"": ""For static methods"" },
                    ""methodName"":           { ""type"": ""string"", ""description"": ""Name of the method to invoke"" },
                    ""arguments"":            { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Method arguments as strings, in order"" },
                    ""genericTypeArguments"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Type names for generic method arguments"" }
                }",
                new[] { "methodName" },
                InvokeMethod
            );

            Tools.Register(
                "get_type_info",
                "Get detailed information about a C# type: base type, interfaces, enum values, member counts.",
                @"{
                    ""typeName"": { ""type"": ""string"", ""description"": ""Full or short name of the type"" }
                }",
                new[] { "typeName" },
                GetTypeInfo
            );
        }

        McpProtocol.ToolCallResult GetMembers(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            string typeName = GetString(args, "typeName");
            string scope = GetString(args, "scope", "all");
            string nameFilter = GetString(args, "nameFilter");
            int limit = GetInt(args, "limit", 50);
            int offset = GetInt(args, "offset", 0);

            var memberTypeFilter = new HashSet<string>();
            if (args?["memberTypes"] is JsonArray mtArr)
                foreach (var t in mtArr)
                    memberTypeFilter.Add(t.GetValue<string>().ToLowerInvariant());

            object instance = null;
            Type type;

            if (!string.IsNullOrEmpty(handle))
            {
                instance = Registry.Resolve(handle);
                if (instance == null)
                    return HandleNotFound(handle);
                type = instance.GetType();
            }
            else if (!string.IsNullOrEmpty(typeName))
            {
                type = TypeResolver.FindType(typeName);
                if (type == null)
                    return McpProtocol.ToolError($"Type not found: {typeName}");
            }
            else
            {
                return McpProtocol.ToolError("Either objectHandle or typeName is required");
            }

            bool isStaticOnly = instance == null;
            var allMembers = new List<JsonObject>();

            // Fields
            if (memberTypeFilter.Count == 0 || memberTypeFilter.Contains("field"))
            {
                foreach (var field in type.GetFields(AllFlags))
                {
                    if (!MatchesScope(field.IsStatic, scope, isStaticOnly))
                        continue;
                    if (!MatchesNameFilter(field.Name, nameFilter))
                        continue;

                    var member = new JsonObject
                    {
                        ["name"] = field.Name,
                        ["memberType"] = "field",
                        ["declaringType"] = field.DeclaringType?.Name,
                        ["isStatic"] = field.IsStatic,
                        ["returnType"] = field.FieldType.Name,
                        ["canRead"] = true,
                        ["canWrite"] = !field.IsLiteral && !field.IsInitOnly,
                        ["signature"] = FormatFieldSignature(field),
                    };

                    TryAutoEvaluateField(field, instance, member);
                    allMembers.Add(member);
                }
            }

            // Properties
            if (memberTypeFilter.Count == 0 || memberTypeFilter.Contains("property"))
            {
                foreach (var prop in type.GetProperties(AllFlags))
                {
                    bool isStatic = (prop.GetMethod ?? prop.SetMethod)?.IsStatic ?? false;
                    if (!MatchesScope(isStatic, scope, isStaticOnly))
                        continue;
                    if (!MatchesNameFilter(prop.Name, nameFilter))
                        continue;

                    var indexParams = prop.GetIndexParameters();
                    var member = new JsonObject
                    {
                        ["name"] = prop.Name,
                        ["memberType"] = "property",
                        ["declaringType"] = prop.DeclaringType?.Name,
                        ["isStatic"] = isStatic,
                        ["returnType"] = prop.PropertyType.Name,
                        ["canRead"] = prop.CanRead,
                        ["canWrite"] = prop.CanWrite,
                        ["signature"] = FormatPropertySignature(prop),
                    };

                    if (indexParams.Length > 0)
                        member["parameters"] = FormatParameters(indexParams);
                    else
                        TryAutoEvaluateProperty(prop, instance, member);

                    allMembers.Add(member);
                }
            }

            // Methods
            if (memberTypeFilter.Count == 0 || memberTypeFilter.Contains("method"))
            {
                foreach (var method in type.GetMethods(AllFlags))
                {
                    if (!MatchesScope(method.IsStatic, scope, isStaticOnly))
                        continue;
                    if (!MatchesNameFilter(method.Name, nameFilter))
                        continue;
                    // Skip property accessors and event methods
                    if (method.IsSpecialName)
                        continue;

                    var member = new JsonObject
                    {
                        ["name"] = method.Name,
                        ["memberType"] = "method",
                        ["declaringType"] = method.DeclaringType?.Name,
                        ["isStatic"] = method.IsStatic,
                        ["returnType"] = method.ReturnType.Name,
                        ["canRead"] = false,
                        ["canWrite"] = false,
                        ["signature"] = FormatMethodSignature(method),
                    };

                    var parameters = method.GetParameters();
                    if (parameters.Length > 0)
                        member["parameters"] = FormatParameters(parameters);

                    if (method.IsGenericMethodDefinition)
                    {
                        var genArgs = method.GetGenericArguments();
                        member["genericArguments"] = new JsonArray(
                            genArgs.Select(a => (JsonNode)a.Name).ToArray()
                        );
                    }

                    allMembers.Add(member);
                }
            }

            // Constructors
            if (memberTypeFilter.Count == 0 || memberTypeFilter.Contains("constructor"))
            {
                foreach (var ctor in type.GetConstructors(AllFlags))
                {
                    if (!MatchesScope(ctor.IsStatic, scope, isStaticOnly))
                        continue;

                    var member = new JsonObject
                    {
                        ["name"] = ".ctor",
                        ["memberType"] = "constructor",
                        ["declaringType"] = type.Name,
                        ["isStatic"] = ctor.IsStatic,
                        ["returnType"] = type.Name,
                        ["canRead"] = false,
                        ["canWrite"] = false,
                        ["signature"] = FormatConstructorSignature(ctor, type),
                    };

                    var parameters = ctor.GetParameters();
                    if (parameters.Length > 0)
                        member["parameters"] = FormatParameters(parameters);

                    allMembers.Add(member);
                }
            }

            int totalCount = allMembers.Count;
            var paged = allMembers.Skip(offset).Take(limit).ToList();

            return McpProtocol.ToolSuccess(
                new JsonObject
                {
                    ["targetHandle"] = handle,
                    ["typeName"] = type.Name,
                    ["typeFullName"] = type.FullName,
                    ["assemblyName"] = type.Assembly.GetName().Name,
                    ["isStaticInspection"] = isStaticOnly,
                    ["totalCount"] = totalCount,
                    ["members"] = new JsonArray(paged.ToArray()),
                }
            );
        }

        McpProtocol.ToolCallResult GetValue(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            string typeName = GetString(args, "typeName");
            string memberName = GetString(args, "memberName");

            var (instance, type, error) = ResolveTarget(handle, typeName);
            if (error != null)
                return error;

            // Try field
            var field = type.GetField(memberName, AllFlags);
            if (field != null)
            {
                object value = field.GetValue(instance);
                return FormatValueResult(memberName, value, field.FieldType);
            }

            // Try property
            var prop = type.GetProperty(memberName, AllFlags);
            if (prop != null)
            {
                if (!prop.CanRead)
                    return McpProtocol.ToolError($"Property '{memberName}' is write-only.");

                object[] indexArgs = null;
                if (args?["indexArguments"] is JsonArray idxArr && idxArr.Count > 0)
                {
                    var indexParams = prop.GetIndexParameters();
                    indexArgs = new object[idxArr.Count];
                    for (int i = 0; i < idxArr.Count; i++)
                        indexArgs[i] = _deserializer.Deserialize(
                            idxArr[i].GetValue<string>(),
                            indexParams[i].ParameterType
                        );
                }

                object value = prop.GetValue(instance, indexArgs);
                return FormatValueResult(memberName, value, prop.PropertyType);
            }

            return McpProtocol.ToolError(
                $"Member '{memberName}' not found on type '{type.FullName}'."
            );
        }

        McpProtocol.ToolCallResult SetValue(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            string typeName = GetString(args, "typeName");
            string memberName = GetString(args, "memberName");
            string valueStr = GetString(args, "value");

            var (instance, type, error) = ResolveTarget(handle, typeName);
            if (error != null)
                return error;

            // Try field
            var field = type.GetField(memberName, AllFlags);
            if (field != null)
            {
                if (field.IsLiteral || field.IsInitOnly)
                    return McpProtocol.ToolError($"Field '{memberName}' is read-only.");

                object parsed = _deserializer.Deserialize(valueStr, field.FieldType);
                field.SetValue(instance, parsed);

                return McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["success"] = true,
                        ["memberName"] = memberName,
                        ["newValue"] = _serializer.Serialize(parsed, field.FieldType),
                    }
                );
            }

            // Try property
            var prop = type.GetProperty(memberName, AllFlags);
            if (prop != null)
            {
                if (!prop.CanWrite)
                    return McpProtocol.ToolError($"Property '{memberName}' is read-only.");

                object parsed = _deserializer.Deserialize(valueStr, prop.PropertyType);
                prop.SetValue(instance, parsed);

                return McpProtocol.ToolSuccess(
                    new JsonObject
                    {
                        ["success"] = true,
                        ["memberName"] = memberName,
                        ["newValue"] = _serializer.Serialize(parsed, prop.PropertyType),
                    }
                );
            }

            return McpProtocol.ToolError(
                $"Member '{memberName}' not found on type '{type.FullName}'."
            );
        }

        McpProtocol.ToolCallResult InvokeMethod(JsonObject args)
        {
            string handle = GetString(args, "objectHandle");
            string typeName = GetString(args, "typeName");
            string methodName = GetString(args, "methodName");

            var (instance, type, error) = ResolveTarget(handle, typeName);
            if (error != null)
                return error;

            // Parse generic type arguments if present
            Type[] genericArgs = null;
            if (args?["genericTypeArguments"] is JsonArray genArr && genArr.Count > 0)
            {
                genericArgs = new Type[genArr.Count];
                for (int i = 0; i < genArr.Count; i++)
                {
                    genericArgs[i] = TypeResolver.FindType(genArr[i].GetValue<string>());
                    if (genericArgs[i] == null)
                        return McpProtocol.ToolError(
                            $"Generic type argument not found: {genArr[i]}"
                        );
                }
            }

            // Find matching method
            var argStrings = new List<string>();
            if (args?["arguments"] is JsonArray argArr)
                foreach (var a in argArr)
                    argStrings.Add(a.GetValue<string>());

            MethodInfo method = FindMethod(type, methodName, argStrings.Count, genericArgs);
            if (method == null)
                return McpProtocol.ToolError(
                    $"Method '{methodName}' with {argStrings.Count} arguments not found on type '{type.FullName}'."
                );

            if (method.IsGenericMethodDefinition && genericArgs != null)
                method = method.MakeGenericMethod(genericArgs);

            // Parse arguments
            var parameters = method.GetParameters();
            var invokeArgs = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < argStrings.Count)
                    invokeArgs[i] = _deserializer.Deserialize(
                        argStrings[i],
                        parameters[i].ParameterType
                    );
                else if (parameters[i].HasDefaultValue)
                    invokeArgs[i] = parameters[i].DefaultValue;
                else
                    invokeArgs[i] = parameters[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(parameters[i].ParameterType)
                        : null;
            }

            object result;
            try
            {
                result = method.Invoke(instance, invokeArgs);
            }
            catch (TargetInvocationException ex)
            {
                return McpProtocol.ToolError(
                    $"Method threw exception: {ex.InnerException?.Message ?? ex.Message}"
                );
            }

            bool isVoid = method.ReturnType == typeof(void);

            var response = new JsonObject
            {
                ["success"] = true,
                ["methodName"] = methodName,
                ["isVoid"] = isVoid,
            };

            if (!isVoid && result != null)
            {
                response["returnValue"] = _serializer.Serialize(result, method.ReturnType);
                response["returnType"] = result.GetType().Name;
                if (!(result is ValueType) && !(result is string))
                    response["returnHandle"] = Registry.RegisterManaged(result);
            }

            return McpProtocol.ToolSuccess(response);
        }

        McpProtocol.ToolCallResult GetTypeInfo(JsonObject args)
        {
            string typeName = GetString(args, "typeName");
            Type type = TypeResolver.FindType(typeName);

            if (type == null)
                return McpProtocol.ToolSuccess(new JsonObject { ["found"] = false });

            var result = new JsonObject
            {
                ["found"] = true,
                ["typeName"] = type.Name,
                ["typeFullName"] = type.FullName,
                ["assemblyQualifiedName"] = type.AssemblyQualifiedName,
                ["assemblyName"] = type.Assembly.GetName().Name,
                ["namespace"] = type.Namespace,
                ["baseType"] = type.BaseType?.FullName,
                ["isAbstract"] = type.IsAbstract,
                ["isSealed"] = type.IsSealed,
                ["isEnum"] = type.IsEnum,
                ["isValueType"] = type.IsValueType,
                ["isGenericType"] = type.IsGenericType,
                ["isInterface"] = type.IsInterface,
                ["interfaces"] = new JsonArray(
                    type.GetInterfaces().Select(i => (JsonNode)i.FullName).ToArray()
                ),
            };

            if (type.IsGenericType)
                result["genericArguments"] = new JsonArray(
                    type.GetGenericArguments().Select(a => (JsonNode)a.Name).ToArray()
                );

            if (type.IsEnum)
            {
                var enumValues = new JsonObject();
                foreach (var name in Enum.GetNames(type))
                    enumValues[name] = ValueSerializer.BoxedToNode(
                        Convert.ChangeType(Enum.Parse(type, name), Enum.GetUnderlyingType(type))
                    );
                result["enumValues"] = enumValues;
            }

            result["memberCounts"] = new JsonObject
            {
                ["fields"] = type.GetFields(AllFlags).Length,
                ["properties"] = type.GetProperties(AllFlags).Length,
                ["methods"] = type.GetMethods(AllFlags).Count(m => !m.IsSpecialName),
                ["constructors"] = type.GetConstructors(AllFlags).Length,
            };

            return McpProtocol.ToolSuccess(result);
        }

        #region Helpers

        (object instance, Type type, McpProtocol.ToolCallResult error) ResolveTarget(
            string handle,
            string typeName
        )
        {
            if (!string.IsNullOrEmpty(handle))
            {
                object instance = Registry.Resolve(handle);
                if (instance == null)
                    return (null, null, HandleNotFound(handle));
                return (instance, instance.GetType(), null);
            }

            if (!string.IsNullOrEmpty(typeName))
            {
                Type type = TypeResolver.FindType(typeName);
                if (type == null)
                    return (null, null, McpProtocol.ToolError($"Type not found: {typeName}"));
                return (null, type, null);
            }

            return (
                null,
                null,
                McpProtocol.ToolError("Either objectHandle or typeName is required")
            );
        }

        void TryAutoEvaluateField(FieldInfo field, object instance, JsonObject member)
        {
            if (instance == null && !field.IsStatic)
                return;

            try
            {
                object value = field.GetValue(instance);
                member["value"] = _serializer.Serialize(value, field.FieldType);
                member["valueType"] = value?.GetType().Name;
                if (value != null && !(value is ValueType) && !(value is string))
                    member["valueHandle"] = Registry.RegisterManaged(value);
            }
            catch
            {
                member["value"] = "<error reading value>";
            }
        }

        void TryAutoEvaluateProperty(PropertyInfo prop, object instance, JsonObject member)
        {
            if (instance == null && !(prop.GetMethod?.IsStatic ?? false))
                return;
            if (!prop.CanRead)
                return;

            try
            {
                object value = prop.GetValue(instance);
                member["value"] = _serializer.Serialize(value, prop.PropertyType);
                member["valueType"] = value?.GetType().Name;
                if (value != null && !(value is ValueType) && !(value is string))
                    member["valueHandle"] = Registry.RegisterManaged(value);
            }
            catch
            {
                member["value"] = "<error reading value>";
            }
        }

        McpProtocol.ToolCallResult FormatValueResult(
            string memberName,
            object value,
            Type declaredType
        )
        {
            var result = new JsonObject
            {
                ["memberName"] = memberName,
                ["value"] = _serializer.Serialize(value, declaredType),
                ["isNull"] = value == null,
                ["valueString"] = _serializer.ToDisplayString(value),
            };

            if (value != null)
            {
                result["valueType"] = value.GetType().Name;
                result["valueTypeFullName"] = value.GetType().FullName;
                if (!(value is ValueType) && !(value is string))
                    result["valueHandle"] = Registry.RegisterManaged(value);
            }

            return McpProtocol.ToolSuccess(result);
        }

        static MethodInfo FindMethod(Type type, string name, int argCount, Type[] genericArgs)
        {
            var methods = type.GetMethods(AllFlags)
                .Where(m => m.Name == name)
                .Where(m =>
                    m.GetParameters().Length == argCount
                    || m.GetParameters().Count(p => !p.HasDefaultValue) <= argCount
                )
                .ToList();

            if (genericArgs != null)
                methods = methods
                    .Where(m =>
                        m.IsGenericMethodDefinition
                        && m.GetGenericArguments().Length == genericArgs.Length
                    )
                    .ToList();

            // Prefer exact parameter count match
            return methods.FirstOrDefault(m => m.GetParameters().Length == argCount)
                ?? methods.FirstOrDefault();
        }

        static bool MatchesScope(bool isStatic, string scope, bool isStaticOnly)
        {
            if (isStaticOnly && !isStatic)
                return false;
            return scope switch
            {
                "instance" => !isStatic,
                "static" => isStatic,
                _ => true,
            };
        }

        static bool MatchesNameFilter(string name, string filter)
        {
            if (string.IsNullOrEmpty(filter))
                return true;
            return name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static JsonArray FormatParameters(ParameterInfo[] parameters)
        {
            var arr = new JsonArray();
            foreach (var p in parameters)
            {
                arr.Add(
                    new JsonObject
                    {
                        ["name"] = p.Name,
                        ["typeName"] = p.ParameterType.Name,
                        ["isOptional"] = p.IsOptional,
                        ["defaultValue"] =
                            p.HasDefaultValue && p.DefaultValue != null
                                ? ValueSerializer.BoxedToNode(p.DefaultValue)
                                : null,
                    }
                );
            }
            return arr;
        }

        static string FormatFieldSignature(FieldInfo field)
        {
            string modifiers = field.IsStatic ? "static " : "";
            if (field.IsLiteral)
                modifiers += "const ";
            else if (field.IsInitOnly)
                modifiers += "readonly ";
            return $"{modifiers}{field.FieldType.Name} {field.Name}";
        }

        static string FormatPropertySignature(PropertyInfo prop)
        {
            bool isStatic = (prop.GetMethod ?? prop.SetMethod)?.IsStatic ?? false;
            string modifiers = isStatic ? "static " : "";
            string accessors = "";
            if (prop.CanRead)
                accessors += "get; ";
            if (prop.CanWrite)
                accessors += "set; ";
            return $"{modifiers}{prop.PropertyType.Name} {prop.Name} {{ {accessors.TrimEnd()} }}";
        }

        static string FormatMethodSignature(MethodInfo method)
        {
            string modifiers = method.IsStatic ? "static " : "";
            string genericPart = "";
            if (method.IsGenericMethodDefinition)
                genericPart =
                    $"<{string.Join(", ", method.GetGenericArguments().Select(a => a.Name))}>";
            string paramPart = string.Join(
                ", ",
                method.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")
            );
            return $"{modifiers}{method.ReturnType.Name} {method.Name}{genericPart}({paramPart})";
        }

        static string FormatConstructorSignature(ConstructorInfo ctor, Type type)
        {
            string paramPart = string.Join(
                ", ",
                ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")
            );
            return $"{type.Name}({paramPart})";
        }

        #endregion
    }
}

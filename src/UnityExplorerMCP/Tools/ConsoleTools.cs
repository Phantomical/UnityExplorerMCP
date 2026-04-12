using System;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class ConsoleTools
    {
        readonly ToolRegistry _tools;
        object _evaluator;
        MethodInfo _evaluateMethod;
        MethodInfo _runMethod;
        bool _initialized;
        string _initError;
        readonly StringBuilder _output = new();

        public ConsoleTools(ToolRegistry tools)
        {
            _tools = tools;
        }

        public void Register()
        {
            _tools.Register(
                "evaluate_csharp",
                "Execute C# code using the Mono runtime compiler. Variables and classes persist across calls within the session. Default usings: System, System.Linq, System.Collections.Generic, UnityEngine.",
                @"{
                    ""code"":      { ""type"": ""string"", ""description"": ""C# code to evaluate. Can be expressions, statements, using directives, or class definitions."" },
                    ""addUsings"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Additional using directives to add before evaluating."" }
                }",
                new[] { "code" },
                EvaluateCSharp
            );
        }

        McpProtocol.ToolCallResult EvaluateCSharp(JObject args)
        {
            string code = args?["code"]?.Value<string>();
            if (string.IsNullOrEmpty(code))
                return McpProtocol.ToolError("Code is required");

            if (!EnsureInitialized())
                return McpProtocol.ToolError(
                    $"C# evaluator could not be initialized: {_initError}"
                );

            // Add usings if requested
            if (args?["addUsings"] is JArray usings)
            {
                foreach (var u in usings)
                {
                    string usingCode = $"using {u.Value<string>()};";
                    try
                    {
                        InvokeRun(usingCode);
                    }
                    catch { }
                }
            }

            _output.Clear();

            try
            {
                // First try Evaluate (for expressions that return a value)
                object result = InvokeEvaluate(code);

                var response = new JObject { ["success"] = true, ["output"] = _output.ToString() };

                if (result != null)
                {
                    response["result"] = result.ToString();
                    response["resultType"] = result.GetType().Name;
                }

                return McpProtocol.ToolSuccess(response);
            }
            catch
            {
                // Try Run (for statements/definitions that don't return a value)
                try
                {
                    _output.Clear();
                    bool success = InvokeRun(code);

                    return McpProtocol.ToolSuccess(
                        new JObject
                        {
                            ["success"] = success,
                            ["output"] = _output.ToString(),
                            ["isCompileError"] = !success,
                        }
                    );
                }
                catch (Exception ex2)
                {
                    return McpProtocol.ToolSuccess(
                        new JObject
                        {
                            ["success"] = false,
                            ["error"] = ex2.InnerException?.Message ?? ex2.Message,
                            ["output"] = _output.ToString(),
                            ["isCompileError"] = true,
                        }
                    );
                }
            }
        }

        bool EnsureInitialized()
        {
            if (_initialized)
                return _evaluator != null;

            _initialized = true;

            try
            {
                // Try to find Mono.CSharp.Evaluator through reflection
                // It may be in mcs.dll or Mono.CSharp.dll
                Type evaluatorType = null;

                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    evaluatorType = asm.GetType("Mono.CSharp.Evaluator");
                    if (evaluatorType != null)
                        break;
                }

                if (evaluatorType == null)
                {
                    // Try to load the assembly
                    try
                    {
                        var asm = Assembly.Load("Mono.CSharp");
                        evaluatorType = asm.GetType("Mono.CSharp.Evaluator");
                    }
                    catch { }
                }

                if (evaluatorType == null)
                {
                    _initError =
                        "Mono.CSharp.Evaluator not found. The mcs.dll assembly may not be loaded.";
                    return false;
                }

                // Create CompilerSettings and CompilerContext
                var settingsType = evaluatorType.Assembly.GetType("Mono.CSharp.CompilerSettings");
                var contextType = evaluatorType.Assembly.GetType("Mono.CSharp.CompilerContext");
                var reporterType =
                    evaluatorType.Assembly.GetType("Mono.CSharp.ConsoleReportPrinter")
                    ?? evaluatorType.Assembly.GetType("Mono.CSharp.StreamReportPrinter");

                var settings = Activator.CreateInstance(settingsType);
                var writer = new StringWriter(_output);
                var reporter = Activator.CreateInstance(reporterType, writer);
                var context = Activator.CreateInstance(contextType, settings, reporter);

                _evaluator = Activator.CreateInstance(evaluatorType, context);

                _evaluateMethod = evaluatorType.GetMethod("Evaluate", new[] { typeof(string) });
                _runMethod = evaluatorType.GetMethod("Run", new[] { typeof(string) });

                // Add default usings
                string[] defaultUsings =
                {
                    "using System;",
                    "using System.Linq;",
                    "using System.Text;",
                    "using System.Collections;",
                    "using System.Collections.Generic;",
                    "using System.Reflection;",
                    "using UnityEngine;",
                };

                foreach (var u in defaultUsings)
                {
                    try
                    {
                        InvokeRun(u);
                    }
                    catch { }
                }

                // Reference common assemblies
                var refMethod = evaluatorType.GetMethod(
                    "ReferenceAssembly",
                    new[] { typeof(Assembly) }
                );
                if (refMethod != null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            refMethod.Invoke(_evaluator, new object[] { asm });
                        }
                        catch { }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _initError = ex.Message;
                return false;
            }
        }

        object InvokeEvaluate(string code)
        {
            return _evaluateMethod.Invoke(_evaluator, new object[] { code });
        }

        bool InvokeRun(string code)
        {
            return (bool)_runMethod.Invoke(_evaluator, new object[] { code });
        }
    }
}

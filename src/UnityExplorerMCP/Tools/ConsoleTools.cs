using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using UnityExplorerMCP.Server;

namespace UnityExplorerMCP.Tools
{
    public class ConsoleTools
    {
        const string DefaultStateKey = "__default__";

        sealed class EvaluatorState
        {
            public object Evaluator;
            public MethodInfo EvaluateMethod; // Evaluate(string)
            public MethodInfo EvaluateMethod3; // Evaluate(string, out object, out bool)
            public MethodInfo RunMethod; // Run(string)
            public StringBuilder Output;
            public TextWriter Writer;
            public string InitError;
        }

        readonly ToolRegistry _tools;
        readonly Dictionary<string, EvaluatorState> _states = new();
        readonly object _statesLock = new();

        Type _evaluatorType;
        bool _typeLookupDone;
        string _typeLookupError;

        public ConsoleTools(ToolRegistry tools)
        {
            _tools = tools;
            SessionContext.Closed += OnSessionClosed;
        }

        void OnSessionClosed(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;
            lock (_statesLock)
                _states.Remove(sessionId);
        }

        #region Parameter Types

        public struct EvaluateCSharpParams
        {
            [McpParam(
                "C# code to evaluate. Can be expressions, statements, using directives, or class definitions.",
                Required = true
            )]
            public string Code { get; set; }

            [McpParam("Additional using directives to add before evaluating.")]
            public string[] AddUsings { get; set; }
        }

        public struct ResetCSharpEvaluatorParams { }

        #endregion

        public void Register()
        {
            _tools.Register<EvaluateCSharpParams>(
                "evaluate_csharp",
                "Execute C# code using the Mono runtime compiler. Variables and classes persist across calls within this MCP session and are isolated from other sessions. Default usings: System, System.Linq, System.Collections.Generic, UnityEngine.",
                EvaluateCSharp
            );
            _tools.Register<ResetCSharpEvaluatorParams>(
                "reset_csharp_evaluator",
                "Drop and recreate the C# evaluator for this session. All variables and class definitions from prior evaluate_csharp calls in this session are lost. Other sessions are unaffected. Use this if the evaluator gets wedged (e.g. after submitting an incomplete class definition).",
                ResetCSharpEvaluator
            );
        }

        McpProtocol.ToolCallResult EvaluateCSharp(EvaluateCSharpParams args)
        {
            if (string.IsNullOrEmpty(args.Code))
                return McpProtocol.ToolError("Code is required");

            if (!EnsureEvaluatorType())
                return McpProtocol.ToolError(
                    $"C# evaluator could not be initialized: {_typeLookupError}"
                );

            string key = CurrentKey();
            var state = GetOrCreateState(key);
            if (state == null)
                return McpProtocol.ToolError(
                    $"C# evaluator could not be initialized: {GetInitError(key)}"
                );

            // Add usings if requested
            if (args.AddUsings != null)
            {
                foreach (var u in args.AddUsings)
                {
                    string usingCode = $"using {u};";
                    try
                    {
                        InvokeRun(state, usingCode);
                    }
                    catch { }
                }
            }

            state.Output.Clear();

            try
            {
                // Try Evaluate (for expressions that return a value). The 3-arg overload
                // returns null on success and a non-null leftover string when the parser
                // saw partial input — that's the "wedge" case.
                object[] callArgs = { args.Code, null, false };
                string leftover = (string)state.EvaluateMethod3.Invoke(state.Evaluator, callArgs);

                if (leftover != null)
                {
                    string output = state.Output.ToString();
                    ResetState(key);
                    return McpProtocol.ToolSuccess(
                        new JsonObject
                        {
                            ["success"] = false,
                            ["isPartialInput"] = true,
                            ["error"] =
                                "Incomplete input (e.g. unclosed brace). The C# evaluator for this session has been reset; prior REPL state was discarded.",
                            ["output"] = output,
                        }
                    );
                }

                object result = callArgs[1];
                bool resultSet = (bool)callArgs[2];

                var response = new JsonObject
                {
                    ["success"] = true,
                    ["output"] = state.Output.ToString(),
                };

                if (resultSet && result != null)
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
                    state.Output.Clear();
                    bool success = InvokeRun(state, args.Code);

                    return McpProtocol.ToolSuccess(
                        new JsonObject
                        {
                            ["success"] = success,
                            ["output"] = state.Output.ToString(),
                            ["isCompileError"] = !success,
                        }
                    );
                }
                catch (Exception ex2)
                {
                    return McpProtocol.ToolSuccess(
                        new JsonObject
                        {
                            ["success"] = false,
                            ["error"] = ex2.InnerException?.Message ?? ex2.Message,
                            ["output"] = state.Output.ToString(),
                            ["isCompileError"] = true,
                        }
                    );
                }
            }
        }

        McpProtocol.ToolCallResult ResetCSharpEvaluator(ResetCSharpEvaluatorParams _)
        {
            if (!EnsureEvaluatorType())
                return McpProtocol.ToolError(
                    $"C# evaluator could not be initialized: {_typeLookupError}"
                );

            string key = CurrentKey();
            try
            {
                var state = ResetState(key);
                if (state.Evaluator == null)
                    return McpProtocol.ToolError($"Failed to reset evaluator: {state.InitError}");
                return McpProtocol.ToolSuccess(new JsonObject { ["success"] = true });
            }
            catch (Exception ex)
            {
                return McpProtocol.ToolError($"Failed to reset evaluator: {ex.Message}");
            }
        }

        static string CurrentKey() => SessionContext.Current ?? DefaultStateKey;

        EvaluatorState GetOrCreateState(string key)
        {
            lock (_statesLock)
            {
                if (_states.TryGetValue(key, out var existing))
                    return existing.Evaluator != null ? existing : null;

                var state = CreateEvaluatorState();
                _states[key] = state;
                return state.Evaluator != null ? state : null;
            }
        }

        EvaluatorState ResetState(string key)
        {
            lock (_statesLock)
            {
                var state = CreateEvaluatorState();
                _states[key] = state;
                return state;
            }
        }

        string GetInitError(string key)
        {
            lock (_statesLock)
                return _states.TryGetValue(key, out var s) ? s.InitError : "(unknown)";
        }

        bool EnsureEvaluatorType()
        {
            if (_typeLookupDone)
                return _evaluatorType != null;

            _typeLookupDone = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _evaluatorType = asm.GetType("Mono.CSharp.Evaluator");
                    if (_evaluatorType != null)
                        break;
                }

                if (_evaluatorType == null)
                {
                    try
                    {
                        var asm = Assembly.Load("Mono.CSharp");
                        _evaluatorType = asm.GetType("Mono.CSharp.Evaluator");
                    }
                    catch { }
                }

                if (_evaluatorType == null)
                {
                    _typeLookupError =
                        "Mono.CSharp.Evaluator not found. The mcs.dll assembly may not be loaded.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _typeLookupError = ex.Message;
                return false;
            }
        }

        EvaluatorState CreateEvaluatorState()
        {
            var state = new EvaluatorState { Output = new StringBuilder() };

            try
            {
                var asm = _evaluatorType.Assembly;
                var settingsType = asm.GetType("Mono.CSharp.CompilerSettings");
                var contextType = asm.GetType("Mono.CSharp.CompilerContext");
                var reporterType =
                    asm.GetType("Mono.CSharp.ConsoleReportPrinter")
                    ?? asm.GetType("Mono.CSharp.StreamReportPrinter");

                var settings = Activator.CreateInstance(settingsType);
                state.Writer = new StringWriter(state.Output);
                var reporter = Activator.CreateInstance(reporterType, state.Writer);
                var context = Activator.CreateInstance(contextType, settings, reporter);

                state.Evaluator = Activator.CreateInstance(_evaluatorType, context);

                // Mono.CSharp's default InteractiveBaseClass is Mono.CSharp.InteractiveBase.
                // When mcs.dll is merged into another assembly with ILRepack's `internalize`
                // option (as UnityExplorerKSP does), InteractiveBase becomes internal. The
                // evaluator generates the wrapper `<InteractiveExpressionClass N>` as public,
                // which then fails to compile with CS0060 "Inconsistent accessibility". Point
                // it at a known-public type so the wrapper's base is always visible.
                var baseClassProp = _evaluatorType.GetProperty("InteractiveBaseClass");
                if (baseClassProp != null)
                    baseClassProp.SetValue(state.Evaluator, typeof(object), null);

                state.EvaluateMethod = _evaluatorType.GetMethod(
                    "Evaluate",
                    new[] { typeof(string) }
                );
                state.EvaluateMethod3 = _evaluatorType.GetMethod(
                    "Evaluate",
                    new[]
                    {
                        typeof(string),
                        typeof(object).MakeByRefType(),
                        typeof(bool).MakeByRefType(),
                    }
                );
                state.RunMethod = _evaluatorType.GetMethod("Run", new[] { typeof(string) });

                if (state.EvaluateMethod3 == null)
                {
                    state.InitError =
                        "Mono.CSharp.Evaluator.Evaluate(string, out object, out bool) overload not found.";
                    state.Evaluator = null;
                    return state;
                }

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
                        InvokeRun(state, u);
                    }
                    catch { }
                }

                var refMethod = _evaluatorType.GetMethod(
                    "ReferenceAssembly",
                    new[] { typeof(Assembly) }
                );
                if (refMethod != null)
                {
                    foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            refMethod.Invoke(state.Evaluator, new object[] { loaded });
                        }
                        catch { }
                    }
                }

                return state;
            }
            catch (Exception ex)
            {
                state.InitError = ex.Message;
                state.Evaluator = null;
                return state;
            }
        }

        static bool InvokeRun(EvaluatorState state, string code)
        {
            return (bool)state.RunMethod.Invoke(state.Evaluator, new object[] { code });
        }
    }
}

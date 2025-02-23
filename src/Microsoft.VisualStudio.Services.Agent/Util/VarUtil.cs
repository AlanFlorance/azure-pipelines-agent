// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Agent.Sdk;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Services.WebApi;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;

namespace Microsoft.VisualStudio.Services.Agent.Util
{
    public static class VarUtil
    {
        public static StringComparer EnvironmentVariableKeyComparer
        {
            get
            {
                if (PlatformUtil.RunningOnWindows)
                {
                    return StringComparer.OrdinalIgnoreCase;
                }

                return StringComparer.Ordinal;
            }
        }

        public static string OS
        {
            get
            {
                switch (PlatformUtil.HostOS)
                {
                    case PlatformUtil.OS.Linux:
                        return "Linux";
                    case PlatformUtil.OS.OSX:
                        return "Darwin";
                    case PlatformUtil.OS.Windows:
                        return Environment.GetEnvironmentVariable("OS");
                    default:
                        throw new NotSupportedException(); // Should never reach here.
                }
            }
        }

        public static string OSArchitecture
        {
            get
            {
                switch (PlatformUtil.HostArchitecture)
                {
                    case Architecture.X86:
                        return "X86";
                    case Architecture.X64:
                        return "X64";
                    case Architecture.Arm:
                        return "ARM";
                    case Architecture.Arm64:
                        return "ARM64";
                    default:
                        throw new NotSupportedException(); // Should never reach here.
                }
            }
        }

        /// <summary>
        /// Returns value in environment variables format.
        /// Example: env.var -> ENV_VAR
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string ConvertToEnvVariableFormat(string value)
        {
            return value?.Replace('.', '_').Replace(' ', '_').ToUpperInvariant() ?? string.Empty;
        }

        public static JToken ExpandEnvironmentVariables(IHostContext context, JToken target)
        {
            var mapFuncs = new Dictionary<JTokenType, Func<JToken, JToken>>
            {
                {
                    JTokenType.String,
                    (t)=> {
                        var token = new Dictionary<string, string>()
                        {
                            {
                                "token", t.ToString()
                            }
                        };
                        ExpandEnvironmentVariables(context, token);
                        return token["token"];
                    }
                }
            };

            return target.Map(mapFuncs);
        }

        public static void ExpandEnvironmentVariables(IHostContext context, IDictionary<string, string> target)
        {
            ArgUtil.NotNull(context, nameof(context));
            Tracing trace = context.GetTrace(nameof(VarUtil));
            trace.Entering();

            // Copy the environment variables into a dictionary that uses the correct comparer.
            var source = new Dictionary<string, string>(EnvironmentVariableKeyComparer);
            IDictionary environment = Environment.GetEnvironmentVariables();

            foreach (DictionaryEntry entry in environment)
            {
                string key = entry.Key as string ?? string.Empty;
                string val = entry.Value as string ?? string.Empty;
                source[key] = val;
            }

            // Expand the target values.
            ExpandValues(context, source, target);
        }

        public static JToken ExpandValues(IHostContext context, IDictionary<string, string> source, JToken target)
        {
            var mapFuncs = new Dictionary<JTokenType, Func<JToken, JToken>>
            {
                {
                    JTokenType.String,
                    (t)=> {
                        var token = new Dictionary<string, string>()
                        {
                            {
                                "token", t.ToString()
                            }
                        };
                        ExpandValues(context, source, token);
                        return token["token"];
                    }
                }
            };

            return target.Map(mapFuncs);
        }

        public static void ExpandValues(
            IHostContext context,
            IDictionary<string, string> source,
            IDictionary<string, string> target,
            string taskName = null
            )
        {
            ArgUtil.NotNull(context, nameof(context));
            ArgUtil.NotNull(source, nameof(source));
            Tracing trace = context.GetTrace(nameof(VarUtil));
            trace.Entering();
            target ??= new Dictionary<string, string>();

            // This algorithm does not perform recursive replacement.

            // Process each key in the target dictionary.
            foreach (string targetKey in target.Keys.ToArray())
            {
                trace.Verbose($"Processing expansion for: '{targetKey}'");
                int startIndex = 0;
                int prefixIndex;
                int suffixIndex;
                string targetValue = target[targetKey] ?? string.Empty;

                // Find the next macro within the target value.
                while (startIndex < targetValue.Length &&
                    (prefixIndex = targetValue.IndexOf(Constants.Variables.MacroPrefix, startIndex, StringComparison.Ordinal)) >= 0 &&
                    (suffixIndex = targetValue.IndexOf(Constants.Variables.MacroSuffix, prefixIndex + Constants.Variables.MacroPrefix.Length, StringComparison.Ordinal)) >= 0)
                {
                    // A candidate was found.
                    string variableKey = targetValue.Substring(
                        startIndex: prefixIndex + Constants.Variables.MacroPrefix.Length,
                        length: suffixIndex - prefixIndex - Constants.Variables.MacroPrefix.Length);
                    trace.Verbose($"Found macro candidate: '{variableKey}'");

                    var isVariableKeyPresent = !string.IsNullOrEmpty(variableKey);
                    WellKnownScriptShell shellName;

                    if (isVariableKeyPresent &&
                        !string.IsNullOrEmpty(taskName) &&
                        Constants.Variables.ScriptShellsPerTasks.TryGetValue(taskName, out shellName) &&
                        shellName != WellKnownScriptShell.Cmd &&
                        Constants.Variables.VariablesVulnerableToExecution.Contains(variableKey, StringComparer.OrdinalIgnoreCase))
                    {
                        trace.Verbose($"Found a macro with vulnerable variables. Replacing with env variables for the {shellName} shell.");

                        var envVariableParts = Constants.ScriptShells.EnvVariablePartsPerShell[shellName];
                        var envVariableName = ConvertToEnvVariableFormat(variableKey);
                        var envVariable = envVariableParts.Prefix + envVariableName + envVariableParts.Suffix;

                        targetValue =
                            targetValue[..prefixIndex]
                            + envVariable
                            + targetValue[(suffixIndex + Constants.Variables.MacroSuffix.Length)..];

                        startIndex = prefixIndex + envVariable.Length;
                    }
                    else if (isVariableKeyPresent &&
                        TryGetValue(trace, source, variableKey, out string variableValue))
                    {
                        // A matching variable was found.
                        // Update the target value.
                        trace.Verbose("Macro found.");

                        if (!string.IsNullOrEmpty(taskName) &&
                            Constants.Variables.ScriptShellsPerTasks.TryGetValue(taskName, out shellName) &&
                            shellName == WellKnownScriptShell.Cmd)
                        {
                            trace.Verbose("CMD shell found. Custom macro processing.");

                            // When matching "&", "|", "<" and ">" cmd commands adds "^" before them.
                            var cmdCommandsRegex = new Regex(@"[&|\||<|>]");
                            variableValue = cmdCommandsRegex.Replace(variableValue, "^$&");
                        }

                        targetValue = string.Concat(
                            targetValue[..prefixIndex],
                            variableValue,
                            targetValue[(suffixIndex + Constants.Variables.MacroSuffix.Length)..]);

                        // Bump the start index to prevent recursive replacement.
                        startIndex = prefixIndex + (variableValue ?? string.Empty).Length;
                    }
                    else
                    {
                        // A matching variable was not found.
                        trace.Verbose("Macro not found.");
                        startIndex = prefixIndex + 1;
                    }
                }

                target[targetKey] = targetValue ?? string.Empty;
            }
        }

        private static bool TryGetValue(Tracing trace, IDictionary<string, string> source, string name, out string val)
        {
            if (source.TryGetValue(name, out val))
            {
                val = val ?? string.Empty;
                trace.Verbose($"Get '{name}': '{val}'");
                return true;
            }

            val = null;
            trace.Verbose($"Get '{name}' (not found)");
            return false;
        }
    }
}
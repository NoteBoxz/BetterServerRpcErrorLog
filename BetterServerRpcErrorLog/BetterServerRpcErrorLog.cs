using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;
using Mono.Cecil;
using Mono.Cecil.Cil;
using OpCode = System.Reflection.Emit.OpCode;
using OpCodes = System.Reflection.Emit.OpCodes;

namespace BetterServerRpcErrorLog;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class BetterServerRpcErrorLog : BaseUnityPlugin
{
    public new static ManualLogSource Logger { get; private set; } = null!;
    private static Harmony Harmony = null!;
    public static ConfigEntry<bool> LogAssemblyScanning { get; private set; } = null!;
    public static ConfigEntry<bool> RemoveFirstLineOfStackTrace { get; private set; } = null!;
    public static int ServerRpcCount { get; private set; } = 0;
    public static int RpcHandlerCount { get; private set; } = 0;


    private void Awake()
    {
        Harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
        Logger = base.Logger;

        Logger.LogInfo($"Active, waiting for game to start before dynamic patching...");

        LogAssemblyScanning = Config.Bind(
        "General",
        "LogAssemblyScanning",
        false,
        "Enable/disable logging of assembly scanning progress");

        RemoveFirstLineOfStackTrace = Config.Bind(
        "General",
        "RemoveFirstLineOfStackTrace",
        true,
        "Enable/disable removing the first line of the stack trace in the error log. This first line is usually from the mod itself and not the actual error.");

        Patch();
    }

    public static void Patch()
    {
        Logger.LogDebug($"Patching...");
        Harmony.PatchAll(Assembly.GetExecutingAssembly());
        Logger.LogDebug($"Patching complete!");
    }


    public static void PatchDynamic()
    {
        Logger.LogInfo($"Dynamic patching...");
        ServerRpcCount = 0;
        // Start a coroutine to perform the patching off the main thread
        GameObject go = new GameObject("SharedCoroutineStarter");
        DontDestroyOnLoad(go);
        SharedCoroutineStarter.Instance = go.AddComponent<SharedCoroutineStarter>();
        SharedCoroutineStarter.Instance.StartCoroutine(PatchDynamicCoroutine());
    }

    private static IEnumerator PatchDynamicCoroutine()
    {
        // Create a task to run the actual patching work
        Task patchingTask = Task.Run(() =>
        {
            List<Assembly> assemblies = AppDomain.CurrentDomain.GetAssemblies().ToList();
            int totalCount = assemblies.Count;
            int currentCount = 0;

            foreach (var assembly in assemblies)
            {
                try
                {
                    ScanAssemblyToPatch(assembly);
                    currentCount++;
                    if (currentCount % 5 == 0 || currentCount == totalCount)
                    {
                        Logger.LogMessage($"Patching progress: {currentCount}/{totalCount} assemblies processed " +
                                        $"{ServerRpcCount} ServerRpc methods patched, {RpcHandlerCount} RPC handlers patched");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error scanning assembly {assembly.FullName}: {ex.Message}");
                }
            }

            Logger.LogInfo($"Dynamic patching complete. Processed {totalCount} assemblies. " +
                          $"Patched {ServerRpcCount} ServerRpc methods and {RpcHandlerCount} RPC handlers.");
        });

        // Wait for the task to complete while yielding control back to Unity
        while (!patchingTask.IsCompleted)
        {
            yield return null;
        }

        // Handle any exceptions that may have occurred
        if (patchingTask.Exception != null)
        {
            Logger.LogError($"Error during async patching: {patchingTask.Exception}");
        }

        Destroy(SharedCoroutineStarter.Instance.gameObject);
    }



    public static void ScanAssemblyToPatch(Assembly assembly)
    {
        // Find all methods
        List<MethodInfo> methodsToPatch = new List<MethodInfo>();
        List<MethodInfo> rpcHandlersToPatch = new List<MethodInfo>();
        List<string> methodsPatched = new List<string>();
        if (LogAssemblyScanning.Value)
            Logger.LogMessage($"----Scanning assembly {assembly.GetName().Name} for patch methods----");

        try
        {
            // Safely try to get types from the assembly
            Type[] assemblyTypes;
            try
            {
                assemblyTypes = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // This handles the case where some types couldn't be loaded
                int nullTypeCount = ex.Types.Count(t => t == null);
                if (LogAssemblyScanning.Value)
                {
                    Logger.LogWarning($"Some types in assembly {assembly.GetName().Name} couldn't be loaded: {ex.Message}");
                    Logger.LogWarning($"Failed to load {nullTypeCount} types from {ex.Types.Length} total types");
                }

                assemblyTypes = ex.Types.Where(t => t != null).ToArray();
            }

            // Get methods
            foreach (var type in assemblyTypes)
            {
                try
                {
                    // Get ServerRpc methods
                    methodsToPatch.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Where(m => m.Name.EndsWith("ServerRpc")));

                    // Get RPC handler methods
                    var rpcHandlers = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                        .Where(m => m.Name.StartsWith("__rpc_handler_") &&
                                   IsRpcHandlerSignatureMatch(m));

                    foreach (var handler in rpcHandlers)
                    {
                        if (ContainsOwnershipCheckAndErrorLog(handler))
                        {
                            rpcHandlersToPatch.Add(handler);
                            if (LogAssemblyScanning.Value)
                                Logger.LogInfo($"Found RPC handler with ownership check: {GetUniqueMethodSignature(handler)}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error accessing methods for type {type.Name}: {ex.Message}");
                }
            }

            // Patch ServerRpc methods
            foreach (MethodInfo method in methodsToPatch)
            {
                try
                {
                    string methodName = GetUniqueMethodSignature(method);

                    if (methodsPatched.Contains(methodName))
                    {
                        if (LogAssemblyScanning.Value)
                            Logger.LogWarning($"Already patched {methodName}, skipping");
                        continue;
                    }
                    ServerRpcAttribute attr = (ServerRpcAttribute)method.GetCustomAttribute(typeof(ServerRpcAttribute));
                    if (attr == null)
                    {
                        if (LogAssemblyScanning.Value)
                            Logger.LogWarning($"No ServerRpcAttribute found for {methodName} despite ending with 'ServerRpc', skipping");
                        continue;
                    }
                    if (attr.RequireOwnership == false)
                    {
                        if (LogAssemblyScanning.Value)
                            Logger.LogInfo($"ServerRpcAttribute found for {methodName} but RequireOwnership is false, skipping");
                        continue;
                    }

                    // Create and apply a dynamic patch
                    try
                    {
                        Harmony.Patch(
                            original: method,
                            prefix: new HarmonyMethod(typeof(BetterServerRpcErrorLog).GetMethod(nameof(ServerRpcPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                        );
                        ServerRpcCount++;
                        if (LogAssemblyScanning.Value)
                            Logger.LogInfo($"Successfully patched ServerRpc {methodName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to patch {methodName}: {ex.Message}");
                    }
                    methodsPatched.Add(methodName);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error processing patch method {method.Name}: {ex.Message}");
                }
            }

            // Patch RPC handler methods
            foreach (MethodInfo handler in rpcHandlersToPatch)
            {
                try
                {
                    string methodName = GetUniqueMethodSignature(handler);

                    if (methodsPatched.Contains(methodName))
                    {
                        if (LogAssemblyScanning.Value)
                            Logger.LogWarning($"Already patched RPC handler {methodName}, skipping");
                        continue;
                    }

                    try
                    {
                        Harmony.Patch(
                            original: handler,
                            transpiler: new HarmonyMethod(typeof(BetterServerRpcErrorLog).GetMethod(nameof(RpcHandlerTranspiler), BindingFlags.Static | BindingFlags.NonPublic))
                        );
                        RpcHandlerCount++;
                        if (LogAssemblyScanning.Value)
                            Logger.LogInfo($"Successfully patched RPC handler {methodName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Failed to patch RPC handler {methodName}: {ex.Message}");
                    }
                    methodsPatched.Add(methodName);
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error processing RPC handler {handler.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error getting types from assembly {assembly.GetName().Name}: {ex.Message}");
            return;
        }
    }

    private static bool IsRpcHandlerSignatureMatch(MethodInfo method)
    {
        var parameters = method.GetParameters();
        // Check if the method has 3 parameters matching the RPC handler signature
        return parameters.Length == 3 &&
               parameters[0].ParameterType == typeof(NetworkBehaviour) &&
               parameters[1].ParameterType.Name == "FastBufferReader" &&
               parameters[2].ParameterType.Name == "__RpcParams";
    }

    private static bool ContainsOwnershipCheckAndErrorLog(MethodInfo method)
    {
        try
        {
            // Try to get the IL instructions
            var methodBody = method.GetMethodBody();
            if (methodBody == null) return false;

            // Use Cecil to access IL instructions
            var assemblyPath = method.Module.Assembly.Location;
            if (string.IsNullOrEmpty(assemblyPath)) return false;

            using (var assembly = AssemblyDefinition.ReadAssembly(assemblyPath))
            {
                var methodDef = FindMethodDefinition(assembly, method);
                if (methodDef == null) return false;

                // Look for the error message string in the IL
                foreach (var instruction in methodDef.Body.Instructions)
                {
                    if (instruction.OpCode == Mono.Cecil.Cil.OpCodes.Ldstr &&
                        instruction.Operand is string str &&
                        str == "Only the owner can invoke a ServerRpc that requires ownership!")
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error analyzing method {method.Name}: {ex.Message}");
            return false;
        }
    }

    private static MethodDefinition? FindMethodDefinition(AssemblyDefinition assembly, MethodInfo method)
    {
        foreach (var module in assembly.Modules)
        {
            var declaringType = module.GetType(method.DeclaringType.FullName);
            if (declaringType == null) continue;

            // Try to find the method by name and parameter count
            foreach (var methodDef in declaringType.Methods)
            {
                if (methodDef.Name == method.Name && methodDef.Parameters.Count == method.GetParameters().Length)
                {
                    // Match parameter types
                    bool parametersMatch = true;
                    for (int i = 0; i < methodDef.Parameters.Count; i++)
                    {
                        if (methodDef.Parameters[i].ParameterType.Name != method.GetParameters()[i].ParameterType.Name)
                        {
                            parametersMatch = false;
                            break;
                        }
                    }

                    if (parametersMatch)
                        return methodDef;
                }
            }
        }

        return null;
    }

    // Transpiler to modify the RPC handler methods
    private static IEnumerable<CodeInstruction> RpcHandlerTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
    {
        List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
        bool foundErrorLog = false;

        for (int i = 0; i < codes.Count - 2; i++)
        {
            // Look for: ldstr "Only the owner can invoke a ServerRpc that requires ownership!"
            //           call void [UnityEngine.CoreModule]UnityEngine.Debug::LogError(object)
            if (codes[i].opcode == OpCodes.Ldstr &&
                codes[i].operand is string str &&
                str == "Only the owner can invoke a ServerRpc that requires ownership!" &&
                codes[i + 1].opcode == OpCodes.Call &&
                codes[i + 1].operand is MethodInfo methodInfo &&
                methodInfo.Name == "LogError" &&
                methodInfo.DeclaringType.Name == "Debug")
            {
                foundErrorLog = true;

                // Replace with our custom logging call
                // Insert our custom method call before the Debug.LogError
                codes.Insert(i, new CodeInstruction(OpCodes.Ldarg_0)); // Load NetworkBehaviour (target)
                codes.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_2)); // Load __RpcParams
                codes.Insert(i + 2, new CodeInstruction(OpCodes.Call,
                    typeof(BetterServerRpcErrorLog).GetMethod(nameof(LogDetailedRpcHandlerError),
                        BindingFlags.Static | BindingFlags.NonPublic)));

                // Skip the original Debug.LogError call
                codes[i + 3] = new CodeInstruction(OpCodes.Nop);
                codes[i + 4] = new CodeInstruction(OpCodes.Nop);

                break;
            }
        }

        if (!foundErrorLog)
        {
            Logger.LogWarning($"RPC handler transpiler couldn't find the error log in {__originalMethod.Name}");
        }

        return codes;
    }

    private static void LogDetailedRpcHandlerError(NetworkBehaviour target, Unity.Netcode.__RpcParams rpcParams)
    {
        try
        {
            NetworkManager networkManager = target.NetworkManager;
            if ((object)networkManager == null) return;

            string trace = Environment.StackTrace;

            if (RemoveFirstLineOfStackTrace.Value)
            {
                // Split stack trace into lines
                string[] stackLines = trace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                // Find the lines to remove (our methods)
                List<int> indicesToRemove = new List<int>();
                for (int i = 0; i < stackLines.Length; i++)
                {
                    if (stackLines[i].Contains("BetterServerRpcErrorLog.LogDetailedRpcHandlerError") ||
                        stackLines[i].Contains("RpcHandlerTranspiler"))
                    {
                        indicesToRemove.Add(i);
                    }
                }

                // Remove our method's line and reconstruct the stack trace
                if (indicesToRemove.Count > 0)
                {
                    trace = string.Join(Environment.NewLine,
                        stackLines.Where((line, index) => !indicesToRemove.Contains(index)));
                }
            }

            Logger.LogError($"[RPC Handler] Only the owner can invoke a ServerRpc that requires ownership!" +
                          $"\nCalled by client {rpcParams.Server.Receive.SenderClientId}, but owner is {target.OwnerClientId}" +
                          $"\nObject: {target.name} ({target.GetType().Name})\n" +
                          $"{trace}");
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error in LogDetailedRpcHandlerError: {ex.Message}");
        }
    }



    // This is the postfix method that will be applied to all ServerRpc methods
    private static void ServerRpcPrefix(MethodBase __originalMethod, NetworkBehaviour __instance)
    {
        try
        {
            NetworkManager networkManager = __instance.NetworkManager;

            if ((object)networkManager == null || !networkManager.IsListening)
            {
                return;
            }

            // Check if the owner of the NetworkBehaviour is the one calling the method
            if (__instance.__rpc_exec_stage != NetworkBehaviour.__RpcExecStage.Server && (networkManager.IsClient || networkManager.IsHost)
                && __instance.OwnerClientId != networkManager.LocalClientId)
            {
                // Get a stack trace to identify the caller
                string callerInfo = string.Empty;
                string trace = Environment.StackTrace;

                if (RemoveFirstLineOfStackTrace.Value)
                {
                    // Split stack trace into lines
                    string[] stackLines = trace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                    // Find the index of our own method in the stack trace
                    int indexToRemove = -1;
                    for (int i = 0; i < stackLines.Length; i++)
                    {
                        if (stackLines[i].Contains("BetterServerRpcErrorLog.ServerRpcPrefix") ||
                            stackLines[i].Contains("ServerRpcPrefix"))
                        {
                            indexToRemove = i;
                            break;
                        }
                    }

                    // Remove our method's line and reconstruct the stack trace
                    if (indexToRemove >= 0 && stackLines.Length > indexToRemove + 1)
                    {
                        trace = string.Join(Environment.NewLine,
                            stackLines.Where((line, index) => index != indexToRemove));
                    }
                }

                Logger.LogError($"stage({__instance.__rpc_exec_stage.ToString()}) Non-owner called ServerRpc that requires ownership: {GetUniqueMethodSignature(__originalMethod)}" +
                               $"\nCalled by client {networkManager.LocalClientId}, but owner is {__instance.OwnerClientId}\n" +
                               $"{trace}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error in ServerRpcPrefix for {GetUniqueMethodSignature(__originalMethod)}: {ex.Message}");
        }
    }

    private static object GetUniqueMethodSignature(MethodBase method)
    {
        // Get base method signature
        string parameterList = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        string methodSig = $"{method.DeclaringType}.{method.Name}({parameterList})";

        // Add generic arguments if present
        if (method.IsGenericMethod)
        {
            string genericArgs = string.Join(", ", method.GetGenericArguments().Select(t => t.Name));
            methodSig += $"<{genericArgs}>";
        }

        return methodSig;
    }

    private static string GetUniqueMethodSignature(MethodInfo method)
    {
        // Get base method signature
        string parameterList = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
        string methodSig = $"{method.DeclaringType}.{method.Name}({parameterList})";

        // Add generic arguments if present
        if (method.IsGenericMethod)
        {
            string genericArgs = string.Join(", ", method.GetGenericArguments().Select(t => t.Name));
            methodSig += $"<{genericArgs}>";
        }

        return methodSig;
    }
}
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
using System.Threading.Tasks;
using Unity.Netcode;
using UnityEngine;

namespace BetterServerRpcErrorLog;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
public class BetterServerRpcErrorLog : BaseUnityPlugin
{
    public new static ManualLogSource Logger { get; private set; } = null!;
    private static Harmony Harmony = null!;
    public static ConfigEntry<bool> LogAssemblyScanning { get; private set; } = null!;
    public static ConfigEntry<bool> RemoveFirstLineOfStackTrace { get; private set; } = null!;
    public static int ServerRpcCount { get; private set; } = 0;

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
                        Logger.LogMessage($"Patching progress: {currentCount}/{totalCount} assemblies processed {ServerRpcCount} ServerRpc methods patched");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error scanning assembly {assembly.FullName}: {ex.Message}");
                }
            }

            Logger.LogInfo($"Dynamic patching complete. Processed {totalCount} assemblies.");
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
                    methodsToPatch.AddRange(type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Where(m => m.Name.EndsWith("ServerRpc")));
                }
                catch (Exception ex)
                {
                    Logger.LogError($"Error accessing methods for type {type.Name}: {ex.Message}");
                }
            }

            foreach (MethodInfo method in methodsToPatch)
            {
                try
                {
                    string methodName = GetUniqueMethodSignature(method);

                    //Logger.LogInfo($"Found ServerRpc: {methodName}");

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
                            Logger.LogWarning($"No ServerRpcAttribute found for {methodName} desipite ending with 'ServerRpc', skipping");
                        continue;
                    }
                    if (attr.RequireOwnership == false)
                    {
                        if (LogAssemblyScanning.Value)
                            Logger.LogInfo($"ServerRpcAttribute found for {methodName} but RequireOwnership is false, skipping");
                        continue;
                    }


                    // Create and apply a dynamic postfix patch
                    try
                    {
                        Harmony.Patch(
                            original: method,
                            prefix: new HarmonyMethod(typeof(BetterServerRpcErrorLog).GetMethod(nameof(ServerRpcPrefix), BindingFlags.Static | BindingFlags.NonPublic))
                        );
                        ServerRpcCount++;
                        if (LogAssemblyScanning.Value)
                            Logger.LogInfo($"Successfully patched {methodName}");
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
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error getting types from assembly {assembly.GetName().Name}: {ex.Message}");
            return;
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
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BetterServerRpcErrorLog;
using Dissonance;
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
                    methodsToPatch.AddRange(type.GetMethods().Where(m => m.Name.EndsWith("ServerRpc")));
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


                    //Logger.LogInfo($"Found ServerRpc: {methodName} ({attr.RequireOwnership})");

                    // Create and apply a dynamic postfix patch
                    try
                    {
                        Harmony.Patch(
                            original: method,
                            postfix: new HarmonyMethod(typeof(BetterServerRpcErrorLog).GetMethod(nameof(ServerRpcPostfix), BindingFlags.Static | BindingFlags.NonPublic))
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
    private static void ServerRpcPostfix(MethodBase __originalMethod, NetworkBehaviour __instance)
    {
        try
        {
            // Get the NetworkManager instance
            NetworkManager networkManager = __instance.NetworkManager;

            // Check if the owner of the NetworkBehaviour is the one calling the method
            if ((int)__instance.__rpc_exec_stage != 1 && (networkManager.IsClient || networkManager.IsHost)
            && __instance.OwnerClientId != networkManager.LocalClientId)
            {
                // Get a stack trace to identify the caller
                System.Diagnostics.StackTrace stackTrace = new System.Diagnostics.StackTrace(true);
                string callerInfo = string.Empty;

                // Get the first few frames of the stack trace (skip the first one as it's this method)
                for (int i = 1; i < Math.Min(4, stackTrace.FrameCount); i++)
                {
                    var frame = stackTrace.GetFrame(i);
                    if (frame != null && frame.GetMethod() != null)
                    {
                        callerInfo += $"\n   at {frame.GetMethod().DeclaringType}.{frame.GetMethod().Name}";

                        if (frame.GetFileName() != null)
                        {
                            callerInfo += $" in {System.IO.Path.GetFileName(frame.GetFileName())}:line {frame.GetFileLineNumber()}";
                        }
                    }
                }

                Logger.LogError($"Non-owner called ServerRpc that requires ownership: {GetUniqueMethodSignature(__originalMethod)}" +
                               $"\nCalled by client {networkManager.LocalClientId}, but owner is {__instance.OwnerClientId}" +
                               $"{callerInfo}");
            }
            else
            {
                // Optional: Log successful owner-based calls
                //Logger.LogInfo($"ServerRpc called by owner: {GetUniqueMethodSignature(__originalMethod)}");
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error in ServerRpcPostfix for {GetUniqueMethodSignature(__originalMethod)}: {ex.Message}");
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
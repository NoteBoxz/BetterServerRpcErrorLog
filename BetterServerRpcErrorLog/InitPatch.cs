using BepInEx;
using BepInEx.Logging;
using BetterServerRpcErrorLog;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Unity.Netcode;
using UnityEngine;

namespace BetterServerRpcErrorLog.Patches;

[HarmonyPatch(typeof(GameNetworkManager))]
internal class InitPatch
{
    [HarmonyPatch(nameof(GameNetworkManager.Start))]
    [HarmonyPriority(Priority.Last)]
    [HarmonyPostfix]
    private static void Postfix()
    {
        try
        {
            BetterServerRpcErrorLog.PatchDynamic();
        }
        catch (Exception ex)
        {
            BetterServerRpcErrorLog.Logger.LogFatal($"[BetterServerRpcErrorLog] Error during dynamic patching: {ex}");
        }
    }
}
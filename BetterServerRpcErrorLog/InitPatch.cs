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

[HarmonyPatch(typeof(InitializeGame))]
internal class InitPatch
{
    [HarmonyPatch(nameof(InitializeGame.Start))]
    [HarmonyPostfix]
    private static void Postfix()
    {
        BetterServerRpcErrorLog.PatchDynamic();
    }
}
using HarmonyLib;
using Il2Cpp;
using Il2CppRewired;
using UnityEngine;

namespace TldCoop;

[HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
internal static class AutoWalkBindingFix
{
    private const int AutoWalkActionId = 100;
    private const string AutoWalkActionName = "AutoWalk";

    private static void Prefix(InputManager_Base __instance)
    {
        var actions = __instance.userData?.actions;
        if (actions == null) return;

        bool hasAutoWalk = false;
        int placeholder = -1;
        for (int i = 0; i < actions.Count; i++)
        {
            InputAction action = actions[i];
            if (action == null || action.id != AutoWalkActionId) continue;

            switch (action.name)
            {
                case AutoWalkActionName when !hasAutoWalk:
                    hasAutoWalk = true;
                    break;
                case "Action0" when placeholder < 0:
                    placeholder = i;
                    break;
                default:
                    return;
            }
        }

        // Rewired builds an ID-indexed action table during Awake. The stock
        // Action0 placeholder overwrites AutoWalk, whose bindings use ID 100.
        if (hasAutoWalk && placeholder >= 0)
            actions.RemoveAt(placeholder);
    }

    internal static void RouteAutoWalk(InputSystemRewired input)
    {
        if (input?.m_ActionToActionId == null || !ReInput.isReady) return;
        var action = ReInput.mapping.GetAction(AutoWalkActionName);
        if (action != null) input.m_ActionToActionId[Il2Cpp.InputManager.InputAction.AutoWalk] = action.id;
    }

    [HarmonyPatch(typeof(InputSystemRewired), nameof(InputSystemRewired.Initialize))]
    private static class InitializeNativeRoute
    {
        private static void Postfix(InputSystemRewired __instance) => RouteAutoWalk(__instance);
    }

    [HarmonyPatch(typeof(InputSystemRewired), nameof(InputSystemRewired.LoadRemapping))]
    private static class ReloadNativeRoute
    {
        private static void Postfix(InputSystemRewired __instance) => RouteAutoWalk(__instance);
    }

    [HarmonyPatch(typeof(vp_FPSPlayer), nameof(vp_FPSPlayer.SetAutoWalkingEnabled))]
    private static class ExplainDisabledAutoWalk
    {
        private static float nextHint;

        private static void Postfix(vp_FPSPlayer __instance, bool enabled)
        {
            if (!enabled || __instance.m_AutoWalking || !MyMod.InOnline() || Time.unscaledTime < nextHint) return;
            HUDMessage.AddMessage("Включите автоходьбу в настройках игры");
            nextHint = Time.unscaledTime + 3f;
        }
    }
}

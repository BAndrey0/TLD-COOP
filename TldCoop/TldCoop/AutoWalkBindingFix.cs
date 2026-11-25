using HarmonyLib;
using Il2CppRewired;

namespace TldCoop;

[HarmonyPatch(typeof(InputManager_Base), nameof(InputManager_Base.Awake))]
internal static class AutoWalkBindingFix
{
    private const int AutoWalkActionId = 100;

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
                case "AutoWalk" when !hasAutoWalk:
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
}

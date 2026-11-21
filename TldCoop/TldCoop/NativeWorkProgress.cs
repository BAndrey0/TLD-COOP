using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace TldCoop;

internal static class NativeWorkProgress
{
    private static bool TryProgress(float elapsed, float duration, out float fraction)
    {
        fraction = 0;
        if (!MyMod.InOnline() || !float.IsFinite(elapsed) || !float.IsFinite(duration) || duration <= 0) return false;
        fraction = Mathf.Clamp01(elapsed / duration);
        return true;
    }

    [HarmonyPatch(typeof(Panel_GenericProgressBar), nameof(Panel_GenericProgressBar.Update))]
    private static class WorldAction
    {
        private static void Postfix(Panel_GenericProgressBar __instance)
        {
            if (!__instance.isActiveAndEnabled ||
                !TryProgress(__instance.m_ProgressBarElapsedSeconds, __instance.m_ProgressBarDurationSeconds, out float fraction)) return;
            if (__instance.m_Slider != null) __instance.m_Slider.value = fraction;
            var foreground = __instance.m_SpriteForground;
            if (foreground != null && foreground.type == UIBasicSprite.Type.Filled)
                foreground.fillAmount = fraction;
        }
    }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.UpdateActionProgressBar))]
    private static class InventoryAction
    {
        private static void Postfix(Panel_Inventory_Examine __instance)
        {
            if (__instance.m_ActionInProgressWindow == null || !__instance.m_ActionInProgressWindow.activeInHierarchy ||
                !TryProgress(__instance.m_ElapsedProgressBarSeconds, __instance.m_ProgressBarTimeSeconds, out float fraction)) return;
            var bar = __instance.GetActionProgress();
            if (bar != null) bar.value = fraction;
        }
    }

    [HarmonyPatch(typeof(Panel_Repair), nameof(Panel_Repair.UpdateRepair))]
    private static class RepairAction
    {
        private static void Postfix(Panel_Repair __instance)
        {
            if (!__instance.RepairInProgress() ||
                !TryProgress(__instance.m_ElapsedProgressBarSeconds, __instance.m_ProgressBarTimeSeconds, out float fraction)) return;
            var bar = __instance.GetActionProgress();
            if (bar != null) bar.value = fraction;
        }
    }
}

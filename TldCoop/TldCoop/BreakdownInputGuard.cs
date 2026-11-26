using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace TldCoop;

[HarmonyPatch(typeof(Panel_BreakDown), nameof(Panel_BreakDown.Enable))]
internal static class BreakdownInputGuard
{
    private static Panel_BreakDown panel;
    private static int openingFrame;

    private static void Prefix(Panel_BreakDown __instance, bool enable)
    {
        if (!MyMod.InOnline()) return;
        panel = enable ? __instance : null;
        openingFrame = Time.frameCount;
    }

    // The panel also polls Continue in Update. Ignore the press which opened it.
    internal static bool CanConfirm(Panel_BreakDown current) =>
        !MyMod.InOnline() || panel != current || Time.frameCount - openingFrame > 1;
}

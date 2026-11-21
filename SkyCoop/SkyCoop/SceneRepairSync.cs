using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;
using Il2CppInteractiveObjects;
using UnityEngine;

namespace SkyCoop;

internal static class SceneRepairSync
{
    private const int MaxRepairsPerScene = 1024;
    private static readonly Dictionary<string, RepairableInteractiveItem> Local = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> LocalIds = new();
    private static readonly Dictionary<string, BrokenDecorationInteraction> Decorations = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> DecorationIds = new();
    private static readonly HashSet<string> Received = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> Saved = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, HashSet<string>> Pending = new(StringComparer.Ordinal);
    private static int seed;
    private static string world;
    private static string scene;

    private static void UpdateWorld()
    {
        int currentSeed = MPSaveManager.GetSeed();
        string currentWorld = WorldSaveIdentity.Active;
        if (currentSeed == seed && currentWorld == world) return;
        seed = currentSeed;
        world = currentWorld;
        Saved.Clear();
        Pending.Clear();
    }

    internal static void Prepare(string area)
    {
        scene = area;
        Local.Clear();
        LocalIds.Clear();
        Decorations.Clear();
        Received.Clear();
        if (area == null) return;
        UpdateWorld();

        var repaired = MyMod.iAmHost ? GetSaved(area) : null;
        bool changed = false;
        foreach (var item in UnityEngine.Object.FindObjectsOfType<RepairableInteractiveItem>(true))
        {
            if (item == null || !item.gameObject.scene.IsValid()) continue;
            string id = Register(item);
            if (repaired == null) continue;
            if (repaired.Contains(id)) Receive(area, id);
            else if (item.IsRepaired) changed |= repaired.Add(id);
        }
        foreach (var item in UnityEngine.Object.FindObjectsOfType<BrokenDecorationInteraction>(true))
        {
            if (item == null || !item.gameObject.scene.IsValid()) continue;
            string id = RegisterDecoration(item);
            if (repaired == null) continue;
            if (repaired.Contains(id)) Receive(area, id);
            else if (item.m_State == BrokenDecorationState.Repaired) changed |= repaired.Add(id);
        }
        if (changed) Save(area, repaired);
    }

    private static string Register(RepairableInteractiveItem item)
    {
        int instance = item.GetInstanceID();
        if (LocalIds.TryGetValue(instance, out string id)) return id;
        id = Id(item);
        Local[id] = item;
        LocalIds[instance] = id;
        return id;
    }

    private static string Id(RepairableInteractiveItem item)
    {
        string guid = item.GetComponent<ObjectGuid>()?.Get();
        if (Valid(guid)) return guid;
        return TransformId("repair-", item.transform);
    }

    private static string TransformId(string prefix, Transform transform)
    {
        string path = "";
        for (var part = transform; part != null; part = part.parent)
            path = part.name + "/" + path;
        var p = transform.position;
        string identity = FormattableString.Invariant($"{path}|{p.x:F2}|{p.y:F2}|{p.z:F2}");
        return prefix + Shared.StableHash(identity).ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string RegisterDecoration(BrokenDecorationInteraction item)
    {
        int instance = item.GetInstanceID();
        if (!DecorationIds.TryGetValue(instance, out string id))
            DecorationIds[instance] = id = TransformId("decoration-repair-", item.transform);
        Decorations[id] = item;
        return id;
    }

    private static bool Valid(string id) => !string.IsNullOrEmpty(id) && id.Length <= 120;

    private static HashSet<string> GetSaved(string area)
    {
        UpdateWorld();
        if (Saved.TryGetValue(area, out var repairs)) return repairs;
        try { repairs = JsonSerializer.Deserialize<HashSet<string>>(MPSaveManager.LoadData(area + "_Repairs", seed) ?? "[]"); }
        catch (JsonException) { repairs = null; }
        return Saved[area] = repairs ?? new(StringComparer.Ordinal);
    }

    private static bool Save(string area, HashSet<string> repairs)
    {
        MPSaveManager.ValidateRootExits();
        return MPSaveManager.SaveData(area + "_Repairs", JsonSerializer.Serialize(repairs), seed);
    }

    internal static IEnumerable<string> Snapshot(string area) => GetSaved(area);

    internal static IEnumerable<string> Requests(string area) =>
        Pending.TryGetValue(area, out var repairs) ? new List<string>(repairs) : Array.Empty<string>();

    internal static bool Accept(string area, string id)
    {
        if (!Valid(id)) return false;
        var repaired = GetSaved(area);
        if (repaired.Contains(id))
        {
            Receive(area, id);
            return true;
        }
        if (repaired.Count >= MaxRepairsPerScene) return false;
        repaired.Add(id);
        if (!Save(area, repaired))
        {
            repaired.Remove(id);
            return false;
        }
        Receive(area, id);
        MelonLoader.MelonLogger.Msg($"[Scene repair] Saved {id} in {area}");
        return true;
    }

    internal static void Receive(string area, string id)
    {
        if (Pending.TryGetValue(area, out var repairs)) repairs.Remove(id);
        if (area != scene || !Valid(id)) return;
        Received.Add(id);
        if (Decorations.TryGetValue(id, out var decoration) && decoration != null)
        {
            if (decoration.m_State != BrokenDecorationState.Repaired)
                decoration.UpdateState(BrokenDecorationState.Repaired);
            return;
        }
        if (!Local.TryGetValue(id, out var item) || item == null) return;
        if (!item.IsRepaired)
            item.Deserialize(new RepairableItemSaveState { m_Guid = item.GetComponent<ObjectGuid>()?.Get() ?? id, m_IsRepaired = true });
        if (item.m_FixedGameobject != null) item.m_FixedGameobject.SetActive(true);
        if (item.m_BrokenGameobject != null) item.m_BrokenGameobject.SetActive(false);
        item.UpdateText();
    }

    private static void Publish(string id)
    {
        if (!MyMod.iAmHost)
        {
            if (!Pending.TryGetValue(scene, out var repairs)) Pending[scene] = repairs = new(StringComparer.Ordinal);
            if (!repairs.Add(id)) return;
        }
        else if (Received.Contains(id)) return;
        ScenePlacementSync.PublishRepair(scene, id);
    }

    [HarmonyPatch(typeof(BrokenDecorationInteraction), nameof(BrokenDecorationInteraction.Awake))]
    private static class DecorationAwake
    {
        private static void Prefix(BrokenDecorationInteraction __instance)
        {
            if (__instance.gameObject.scene.IsValid()) RegisterDecoration(__instance);
        }

        private static void Postfix(BrokenDecorationInteraction __instance)
        {
            if (scene == null || scene != MyMod.level_guid || MyMod.LoadingScreenIsOn) return;
            string id = RegisterDecoration(__instance);
            if (Received.Contains(id) || MyMod.iAmHost && GetSaved(scene).Contains(id)) Receive(scene, id);
        }
    }

    [HarmonyPatch(typeof(BrokenDecorationInteraction), nameof(BrokenDecorationInteraction.OnDestroy))]
    private static class DecorationDestroyed
    {
        private static void Prefix(BrokenDecorationInteraction __instance)
        {
            if (DecorationIds.Remove(__instance.GetInstanceID(), out string id) &&
                Decorations.TryGetValue(id, out var item) && item == __instance) Decorations.Remove(id);
        }
    }

    private static void PublishDecorationRepair(BrokenDecorationInteraction item)
    {
        if (item != null && item.m_State == BrokenDecorationState.Repaired && scene != null &&
            scene == MyMod.level_guid && MyMod.InOnline() && !MyMod.LoadingScreenIsOn)
            Publish(RegisterDecoration(item));
    }

    [HarmonyPatch(typeof(BrokenDecorationInteraction), nameof(BrokenDecorationInteraction.OnRepairSuccessful))]
    private static class DecorationRepaired
    {
        private static void Postfix(BrokenDecorationInteraction __instance) => PublishDecorationRepair(__instance);
    }

    [HarmonyPatch(typeof(Panel_Inventory_Examine), nameof(Panel_Inventory_Examine.RepairSuccessful))]
    private static class RepairPanelSucceeded
    {
        private static void Prefix(Panel_Inventory_Examine __instance, out BrokenDecorationInteraction __state) =>
            __state = __instance.m_Repairable?.TryCast<BrokenDecorationInteraction>();

        private static void Postfix(BrokenDecorationInteraction __state) => PublishDecorationRepair(__state);
    }

    [HarmonyPatch(typeof(RepairableInteractiveItem), nameof(RepairableInteractiveItem.InitializeInteraction))]
    private static class RepairableInitialized
    {
        private static void Postfix(RepairableInteractiveItem __instance)
        {
            if (scene == null || scene != MyMod.level_guid || !MyMod.InOnline() || MyMod.LoadingScreenIsOn) return;
            string id = Register(__instance);
            if (Received.Contains(id) || MyMod.iAmHost && GetSaved(scene).Contains(id)) Receive(scene, id);
        }
    }

    [HarmonyPatch(typeof(RepairableInteractiveItem), nameof(RepairableInteractiveItem.OnAnimatedInteractionEnd))]
    private static class ObserveRepair
    {
        private static void Prefix(RepairableInteractiveItem __instance, out bool __state) => __state = __instance.IsRepaired;

        private static void Postfix(RepairableInteractiveItem __instance, bool __state)
        {
            if (__state || !__instance.IsRepaired || scene == null || scene != MyMod.level_guid || !MyMod.InOnline()) return;
            Publish(Register(__instance));
        }
    }
}

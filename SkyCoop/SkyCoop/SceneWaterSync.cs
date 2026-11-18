using System;
using System.Collections.Generic;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;

namespace SkyCoop;

internal static class SceneWaterSync
{
    internal sealed record Level(string Id, float Liters);

    private static readonly Dictionary<string, Dictionary<string, float>> Saved = new();
    private static readonly Dictionary<string, float> Pending = new();
    private static readonly Dictionary<string, WaterSource> Local = new();
    private static readonly Dictionary<string, float> Received = new();
    private static string world;
    private static string scene;
    private static int seed;
    private static bool ready;

    private static string Id(WaterSource source)
    {
        string guid = source.GetComponent<ObjectGuid>()?.Get();
        if (!string.IsNullOrEmpty(guid)) return guid;
        var p = source.transform.position;
        return "water-" + Shared.StableHash(FormattableString.Invariant(
            $"{GameCompat.StripCloneSuffix(source.name)}|{p.x:F2}|{p.y:F2}|{p.z:F2}")).ToString("X8");
    }

    private static Dictionary<string, float> GetSaved(string area)
    {
        int currentSeed = MPSaveManager.GetSeed();
        string currentWorld = WorldSaveIdentity.Active;
        if (world != currentWorld || seed != currentSeed)
        {
            world = currentWorld;
            seed = currentSeed;
            Saved.Clear();
        }
        if (Saved.TryGetValue(area, out var levels)) return levels;
        try { levels = JsonSerializer.Deserialize<Dictionary<string, float>>(MPSaveManager.LoadData(area + "_WaterLevels", seed) ?? ""); }
        catch (JsonException) { levels = null; }
        return Saved[area] = levels ?? new();
    }

    internal static void Prepare(string area)
    {
        scene = area;
        Local.Clear();
        Pending.Clear();
        Received.Clear();
        ready = MyMod.iAmHost;
        if (area == null) return;
        foreach (var source in UnityEngine.Object.FindObjectsOfType<WaterSource>(true))
            if (source != null && source.gameObject.scene.IsValid()) Register(source);
    }

    private static void Register(WaterSource source)
    {
        string id = Id(source);
        Local[id] = source;
        if (MyMod.iAmHost && GetSaved(scene).TryGetValue(id, out float remaining))
            Receive(scene, new Level(id, remaining));
        else if (Received.TryGetValue(id, out remaining))
            Receive(scene, new Level(id, remaining));
    }

    internal static IEnumerable<Level> Snapshot(string area)
    {
        foreach (var pair in GetSaved(area)) yield return new Level(pair.Key, pair.Value);
    }

    internal static IEnumerable<Level> Requests()
    {
        foreach (var pair in Pending) yield return new Level(pair.Key, pair.Value);
    }

    internal static Level Accept(string area, Level update)
    {
        if (update == null || string.IsNullOrEmpty(update.Id) || update.Id.Length > 120 ||
            !float.IsFinite(update.Liters) || update.Liters < 0f || update.Liters > 1000f) return null;
        var saved = GetSaved(area);
        if (saved.TryGetValue(update.Id, out float previous) && previous <= update.Liters)
            return new Level(update.Id, previous);
        if (!saved.ContainsKey(update.Id) && saved.Count >= 1024) return null;
        var next = new Dictionary<string, float>(saved) { [update.Id] = update.Liters };
        MPSaveManager.ValidateRootExits();
        if (!MPSaveManager.SaveData(area + "_WaterLevels", JsonSerializer.Serialize(next), seed)) return null;
        Saved[area] = next;
        Receive(area, update);
        return update;
    }

    internal static void Receive(string area, Level update)
    {
        if (scene != area || update == null || string.IsNullOrEmpty(update.Id) ||
            !float.IsFinite(update.Liters) || update.Liters < 0f) return;
        if (Received.TryGetValue(update.Id, out var previous)) update = update with { Liters = Math.Min(previous, update.Liters) };
        Received[update.Id] = update.Liters;
        if (Pending.TryGetValue(update.Id, out float pending) && update.Liters <= pending)
            Pending.Remove(update.Id);
        if (Local.TryGetValue(update.Id, out var source) && source != null && source.GetVolumeLiters().ToLiters() > update.Liters)
            source.m_CurrentLiters = GameCompat.Liters(update.Liters);
    }

    internal static void SnapshotComplete(string area)
    {
        if (scene == area) ready = true;
    }

    [HarmonyPatch(typeof(WaterSource), nameof(WaterSource.PerformInteraction))]
    private static class SourceInteraction
    {
        private static bool Prefix()
        {
            if (!MyMod.sendMyPosition || ready && scene == MyMod.level_guid) return true;
            HUDMessage.AddMessage("Синхронизация запасов воды…");
            return false;
        }
    }

    [HarmonyPatch(typeof(WaterSource), nameof(WaterSource.Start))]
    private static class SourceStarted
    {
        private static void Postfix(WaterSource __instance)
        {
            if (scene != null && scene == MyMod.level_guid && !MyMod.LoadingScreenIsOn && MyMod.InOnline())
                Register(__instance);
        }
    }

    [HarmonyPatch(typeof(WaterSource), nameof(WaterSource.RemoveLiters))]
    private static class SourceDrained
    {
        private static void Prefix(WaterSource __instance, out float __state) => __state = __instance.GetVolumeLiters().ToLiters();

        private static void Postfix(WaterSource __instance, float __state) => ObserveWithdrawal(__instance, __state);
    }

    [HarmonyPatch(typeof(Panel_PickWater), nameof(Panel_PickWater.OnTakeWaterComplete))]
    private static class WaterPanelFinished
    {
        private static void Prefix(Panel_PickWater __instance, out (WaterSource Source, float Before) __state)
        {
            var source = __instance.m_WaterSource;
            __state = (source, source == null ? 0 : source.GetVolumeLiters().ToLiters());
        }

        private static void Postfix((WaterSource Source, float Before) __state) => ObserveWithdrawal(__state.Source, __state.Before);
    }

    private static void ObserveWithdrawal(WaterSource source, float before)
    {
        if (scene == null || scene != MyMod.level_guid || !MyMod.InOnline() || source == null) return;
        float liters = source.GetVolumeLiters().ToLiters();
        if (!(liters < before)) return;
        string id = Id(source);
        if (Received.TryGetValue(id, out float confirmed) && confirmed <= liters ||
            Pending.TryGetValue(id, out float queued) && queued <= liters) return;
        Local[id] = source;
        if (!MyMod.iAmHost) Pending[id] = liters;
        ScenePlacementSync.PublishWaterLevel(scene, new Level(id, liters));
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using GameServer;
using UnityEngine;
using System.Text.Json;
using HarmonyLib;
using Il2Cpp;

namespace TldCoop;

internal static class SceneWaterSync
{
    internal sealed record Level(string Id, float Liters);

    private static readonly Dictionary<string, Dictionary<string, float>> Saved = new();
    internal sealed record Withdrawal(Guid Operation, string Id, float Observed, float Requested, bool Potable);
    internal sealed record Grant(Guid Operation, string Id, float Remaining, float Liters, bool Potable);
    private sealed record WaterState(Dictionary<string, float> Levels, Dictionary<Guid, Grant> Grants);
    private static readonly Dictionary<string, WaterState> States = new();
    private static readonly Dictionary<Guid, (string Scene, Withdrawal Request, float Next)> Pending = new();
    internal static bool HasPending => Pending.Count != 0;
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
            States.Clear();
            Pending.Clear();
        }
        if (Saved.TryGetValue(area, out var levels)) return levels;
        string data = MPSaveManager.LoadData(area + "_WaterLevels", seed) ?? "";
        WaterState state = null;
        try { state = JsonSerializer.Deserialize<WaterState>(data); }
        catch (JsonException) { }
        if (state?.Levels == null || state.Grants == null)
        {
            try { levels = JsonSerializer.Deserialize<Dictionary<string, float>>(data); }
            catch (JsonException) { levels = null; }
            state = new WaterState(levels ?? new(), new());
        }
        States[area] = state;
        return Saved[area] = state.Levels;
    }

    internal static void Prepare(string area)
    {
        scene = area;
        Local.Clear();
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

    internal static void Tick()
    {
        if (!MyMod.sendMyPosition || !MyMod.InOnline()) { Pending.Clear(); return; }
        foreach (var pair in Pending.ToArray())
        {
            var entry = pair.Value;
            if (Time.realtimeSinceStartup < entry.Next) continue;
            ScenePlacementSync.RequestWater(entry.Scene, JsonSerializer.Serialize(entry.Request));
            Pending[pair.Key] = (entry.Scene, entry.Request, Time.realtimeSinceStartup + 2f);
        }
    }

    internal static Grant Accept(string area, Withdrawal request, bool requesterInArea)
    {
        if (request == null || request.Operation == Guid.Empty || string.IsNullOrEmpty(request.Id) ||
            request.Id.Length > 120 || !float.IsFinite(request.Observed) || request.Observed is < 0 or > 1000 ||
            !float.IsFinite(request.Requested) || request.Requested is <= 0 or > 1000) return null;
        var levels = GetSaved(area);
        var state = States[area];
        if (state.Grants.TryGetValue(request.Operation, out var replay)) return replay;
        if (!requesterInArea) return new Grant(request.Operation, request.Id, 0, 0, request.Potable);
        if (!levels.TryGetValue(request.Id, out float remaining))
        {
            if (levels.Count >= 1024) return null;
            remaining = request.Observed;
        }
        WaterSource source = null;
        if (area == scene && Local.TryGetValue(request.Id, out source) && source != null)
            remaining = Math.Min(remaining, source.GetVolumeLiters().ToLiters());
        float amount = WaterWithdrawalPolicy.Granted(remaining, request.Requested);
        var grant = new Grant(request.Operation, request.Id, remaining - amount, amount,
            source != null ? source.GetQuality() == LiquidQuality.Potable : request.Potable);
        var next = new WaterState(new Dictionary<string, float>(levels) { [request.Id] = grant.Remaining },
            new Dictionary<Guid, Grant>(state.Grants) { [request.Operation] = grant });
        if (next.Grants.Count > 1024) next.Grants.Remove(next.Grants.Keys.First());
        MPSaveManager.ValidateRootExits();
        if (!MPSaveManager.SaveData(area + "_WaterLevels", JsonSerializer.Serialize(next), seed)) return null;
        States[area] = next;
        Saved[area] = next.Levels;
        Receive(area, new Level(grant.Id, grant.Remaining));
        return grant;
    }

    internal static void ReceiveGrant(Grant grant)
    {
        if (grant == null || !Pending.ContainsKey(grant.Operation)) return;
        if (Award(grant)) Pending.Remove(grant.Operation);
    }

    private static bool Award(Grant grant)
    {
        if (grant.Liters <= 0f)
        {
            HUDMessage.AddMessage("В источнике больше нет доступной воды");
            return true;
        }
        var inventory = GameManager.GetInventoryComponent();
        if (inventory == null || MyMod.LoadingScreenIsOn) return false;
        inventory.AddToWaterSupply(GameCompat.Liters(grant.Liters), grant.Potable ? LiquidQuality.Potable : LiquidQuality.NonPotable);
        return true;
    }
    internal static void Receive(string area, Level update)
    {
        if (scene != area || update == null || string.IsNullOrEmpty(update.Id) ||
            !float.IsFinite(update.Liters) || update.Liters < 0f) return;
        if (Received.TryGetValue(update.Id, out var previous)) update = update with { Liters = Math.Min(previous, update.Liters) };
        Received[update.Id] = update.Liters;
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

    [HarmonyPatch(typeof(Panel_PickWater), nameof(Panel_PickWater.OnTakeWaterComplete))]
    private static class WaterPanelFinished
    {
        private static bool Prefix(Panel_PickWater __instance, bool __0, bool __1)
        {
            var source = __instance.m_WaterSource;
            if (!__0 || __1 || source == null || !MyMod.InOnline() || scene != MyMod.level_guid) return true;
            float requested = __instance.m_numLiters.ToLiters();
            if (!float.IsFinite(requested) || requested <= 0f) return true;
            var request = new Withdrawal(Guid.NewGuid(), Id(source), source.GetVolumeLiters().ToLiters(), requested,
                source.GetQuality() == LiquidQuality.Potable);
            if (MyMod.iAmHost)
            {
                var grant = ScenePlacementSync.AcceptWater(scene, request, 0, true);
                if (grant == null) HUDMessage.AddMessage("Не удалось сохранить запас воды");
                else Award(grant);
            }
            else
            {
                Pending.Add(request.Operation, (scene, request, Time.realtimeSinceStartup + 2f));
                ScenePlacementSync.RequestWater(scene, JsonSerializer.Serialize(request));
            }
            __instance.m_IsTakingWater = false;
            __instance.ExitInterface();
            return false;
        }
    }
}
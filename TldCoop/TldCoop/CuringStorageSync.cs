using System;
using System.Collections.Generic;
using System.Text.Json;
using GameServer;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace TldCoop;

internal static class CuringStorageSync
{
    private sealed record Message(string Id, Guid Token, string State);
    private sealed record Reservation(int Player, int Generation, Guid Token, float Expires);
    private static readonly Dictionary<(string Scene, string Id), Reservation> Leases = new();
    private static CuringStorage current;
    private static string area;
    private static Message pending;
    private static bool authorized;
    private static bool closing;
    private static float deadline;
    private static int session;
    private static (string Scene, Message Update, float Next) saving;
    private static readonly Dictionary<Guid, (int Player, int Generation)> SavedOperations = new();
    private static readonly Queue<Guid> SavedOrder = new();
    internal static bool HasPendingCommit => saving.Update != null;

    private static string Id(CuringStorage box) => ScenePlacementSync.FurnitureIdentity(box.gameObject);
    private static string File(string scene, string id) => scene + "_Curing_" + Shared.StableHash(id).ToString("X8");
    private static string Snapshot(CuringStorage box) => Utils.SerializeObject(box.SerializeCuringStorage());

    internal static void MoveIdentity(string scene, string oldId, string newId)
    {
        int seed = MPSaveManager.GetSeed();
        string state = MPSaveManager.LoadData(File(scene, oldId), seed);
        if (!string.IsNullOrEmpty(state) && string.IsNullOrEmpty(MPSaveManager.LoadData(File(scene, newId), seed)))
            MPSaveManager.SaveData(File(scene, newId), state, seed);
    }

    private static void Restore(CuringStorage box, string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        var state = Utils.DeserializeObject<CuringStorageState>(data);
        if (state == null) return;
        var own = box.SerializeCuringStorage();
        if (own != null) state.m_GUID = own.m_GUID;
        box.DeserializeCuringStorage(state);
    }

    private static void Clear()
    {
        if (MyMod.iAmHost && pending != null && saving.Update?.Token != pending.Token && Leases.TryGetValue((area, pending.Id), out var lease) &&
            lease.Player == 0 && lease.Token == pending.Token)
            Leases.Remove((area, pending.Id));
        current = null;
        pending = null;
        closing = false;
    }

    private static void UpdateSession()
    {
        int next = Server.Generation;
        if (session == next) return;
        session = next;
        Leases.Clear();
        SavedOperations.Clear();
        SavedOrder.Clear();
    }

    private static bool Available(string scene, string id)
    {
        if (!Leases.TryGetValue((scene, id), out var lease)) return true;
        if (lease.Expires > Time.realtimeSinceStartup &&
            (lease.Player == 0 || Server.clients.TryGetValue(lease.Player, out var peer) &&
                peer.Generation == lease.Generation && peer.ConnectionInitialized && peer.IsBusy() && !peer.Departing))
            return false;
        Leases.Remove((scene, id));
        return true;
    }

    [HarmonyPatch(typeof(CuringStorage), nameof(CuringStorage.PrepareAndShow))]
    private static class Open
    {
        private static bool Prefix(CuringStorage __instance)
        {
            if (!MyMod.InOnline() || authorized) return true;
            if (current != null || HasPendingCommit) return false;
            string id = Id(__instance);
            if (string.IsNullOrEmpty(id))
            {
                HUDMessage.AddMessage("Разместите короб перед использованием");
                return false;
            }
            area = MyMod.level_guid;
            pending = new Message(id, Guid.NewGuid(), "");
            if (MyMod.iAmHost)
            {
                UpdateSession();
                if (!Available(area, id)) { Clear(); HUDMessage.AddMessage("Короб занят"); return false; }
                Leases[(area, id)] = new(0, 0, pending.Token, float.PositiveInfinity);
                Restore(__instance, MPSaveManager.LoadData(File(area, id), MPSaveManager.GetSeed()));
            }
            current = __instance;
            deadline = Time.realtimeSinceStartup + 8f;
            if (MyMod.iAmHost) return true;
            ScenePlacementSync.SendCuring(17, area, JsonSerializer.Serialize(pending));
            return false;
        }
    }

    [HarmonyPatch(typeof(CuringStorage), nameof(CuringStorage.CloseCuringStorage))]
    private static class Close
    {
        private static void Postfix(CuringStorage __instance)
        {
            if (current != __instance || pending == null) return;
            closing = true;
            Tick();
        }
    }

    [HarmonyPatch(typeof(CuringStorage), nameof(CuringStorage.Update))]
    private static class FinishAfterNativeClose
    {
        private static void Postfix(CuringStorage __instance)
        {
            if (closing && current == __instance) Tick();
        }
    }

    internal static void Tick()
    {
        if (saving.Update != null)
        {
            if (!MyMod.InOnline()) saving = default;
            else if (Time.realtimeSinceStartup >= saving.Next) TickCommit();
        }
        if (pending == null) return;
        if (!MyMod.InOnline() || current == null || area != MyMod.level_guid) { Clear(); return; }
        if (closing && current.IsPanelClosed() && !current.m_PendingClose)
            Commit();
        else if (!MyMod.iAmHost && !current.m_ShowingPanel && !closing && Time.realtimeSinceStartup > deadline)
        {
            HUDMessage.AddMessage("Короб занят или не ответил");
            Clear();
        }
    }

    private static void Commit()
    {
        string state = Snapshot(current);
        if (string.IsNullOrEmpty(state)) return;
        var update = pending with { State = state };
        saving = (area, update, 0);
        TickCommit();
        Clear();
    }

    private static void TickCommit()
    {
        if (MyMod.iAmHost && Accept(18, 0, saving.Scene, saving.Update)) saving = default;
        else if (!MyMod.iAmHost) ScenePlacementSync.SendCuring(18, saving.Scene, JsonSerializer.Serialize(saving.Update));
        if (saving.Update != null) saving.Next = Time.realtimeSinceStartup + 2f;
    }

    internal static void Acknowledge(string data)
    {
        if (saving.Update != null && Guid.TryParseExact(data, "N", out Guid token) && saving.Update.Token == token)
            saving = default;
    }

    internal static void ReceiveClient(byte kind, int from, string scene, string data) =>
        Accept(kind, from, scene, JsonSerializer.Deserialize<Message>(data));

    private static bool Accept(byte kind, int from, string scene, Message msg)
    {
        if (msg == null || string.IsNullOrEmpty(msg.Id) || msg.Id.Length > 100 || msg.Token == Guid.Empty || msg.State == null || msg.State.Length > 10000) return false;
        UpdateSession();
        int generation = from > 0 && Server.clients.TryGetValue(from, out var client) ? client.Generation : 0;
        if (kind == 18 && SavedOperations.TryGetValue(msg.Token, out var owner) && owner == (from, generation))
        {
            if (from > 0) ScenePlacementSync.AcknowledgeCuring(from, scene, msg.Token);
            return true;
        }
        var key = (scene, msg.Id);
        if (kind == 17)
        {
            if (!Available(scene, msg.Id)) return false;
            var path = File(scene, msg.Id);
            string state = MPSaveManager.LoadData(path, MPSaveManager.GetSeed());
            if (string.IsNullOrEmpty(state) && scene == MyMod.level_guid && CuringStorage.s_AllCuringStorage != null)
                foreach (var box in CuringStorage.s_AllCuringStorage)
                    if (box != null && Id(box) == msg.Id) { state = Snapshot(box); break; }
            Leases[key] = new(from, generation, msg.Token, Time.realtimeSinceStartup + 600f);
            ScenePlacementSync.ReplyCuring(from, scene, JsonSerializer.Serialize(msg with { State = state ?? "" }));
        }
        else if (Leases.TryGetValue(key, out var lease) && lease.Player == from && lease.Generation == generation && lease.Token == msg.Token)
        {
            if (string.IsNullOrEmpty(msg.State)) { Leases.Remove(key); return true; }
            MPSaveManager.ValidateRootExits();
            if (MPSaveManager.SaveData(File(scene, msg.Id), msg.State, MPSaveManager.GetSeed()))
            {
                Leases.Remove(key);
                if (SavedOrder.Count >= 2048) SavedOperations.Remove(SavedOrder.Dequeue());
                SavedOperations[msg.Token] = (from, generation);
                SavedOrder.Enqueue(msg.Token);
                if (from > 0) ScenePlacementSync.AcknowledgeCuring(from, scene, msg.Token);
                MelonLogger.Msg($"[Curing storage] Saved scene={scene} id={msg.Id} player={from}");
                return true;
            }
        }
        return false;
    }

    internal static void Receive(string data)
    {
        var reply = JsonSerializer.Deserialize<Message>(data);
        if (reply == null || pending == null || reply.Token != pending.Token || current == null) return;
        if ((current.transform.position - GameManager.GetPlayerTransform().position).sqrMagnitude > 16f)
        {
            ScenePlacementSync.SendCuring(18, area, JsonSerializer.Serialize(pending));
            Clear();
            return;
        }
        Restore(current, reply.State);
        authorized = true;
        try { current.PrepareAndShow(); }
        finally { authorized = false; }
    }

    internal static void Probe()
    {
        var box = UnityEngine.Object.FindObjectOfType<CuringStorage>();
        if (box == null) { MelonLogger.Warning("[Curing storage probe] no box"); return; }
        var meat = GameManager.GetPlayerManagerComponent().InstantiateItemInPlayerInventory("GEAR_RawMeatDeer", 1);
        if (meat != null)
        {
            box.PrepareAndShow();
            box.AddItem(meat);
            box.CloseCuringStorage();
            box.Update();
        }
        int before = box.SerializeCuringStorage()?.m_CuringItems?.Count ?? 0;
        string source = Snapshot(box);
        Restore(box, source);
        string result = Snapshot(box);
        int after = box.SerializeCuringStorage()?.m_CuringItems?.Count ?? 0;
        Tick();
        string saved = MPSaveManager.LoadData(File(MyMod.level_guid, Id(box)), MPSaveManager.GetSeed());
        int savedCount = string.IsNullOrEmpty(saved) ? 0 : Utils.DeserializeObject<CuringStorageState>(saved)?.m_CuringItems?.Count ?? 0;
        MelonLogger.Msg($"[Curing storage probe] id={Id(box)} items={before}/{after} payload={source.Length} roundTrip={source == result} journalItems={savedCount}");
    }
}

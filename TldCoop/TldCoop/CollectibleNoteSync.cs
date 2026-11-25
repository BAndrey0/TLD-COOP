using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using GameServer;
using HarmonyLib;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace TldCoop;

internal static class CollectibleNoteSync
{
    internal const int PacketId = 200;
    private const string SaveName = "CollectibleNotes";
    private const int MaxNotes = 256;
    private const int MaxNameLength = 96;
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    private static (int Seed, string Id, bool Host) world;
    private static bool hostLoaded;
    private static bool importInventory;
    private static bool applyNeeded;
    private static string appliedScene;
    private static int appliedSceneHandle;
    private static float nextRetry;
    private static float nextHostLoad;
    private static float nextGrant;
    private static float grantDelay;

    private static bool InWorld => MyMod.InOnline() && !MyMod.LoadingScreenIsOn &&
        WildlifeSpawnAuthorityPolicy.WorldScene(MyMod.level_guid) &&
        WildlifeSpawnAuthorityPolicy.WorldScene(MyMod.level_name);

    private static bool RefreshWorld()
    {
        if (!MyMod.InOnline())
        {
            if (world.Seed != 0) Reset(default);
            return false;
        }
        if (GameManager.m_SceneTransitionData == null) return false;
        var current = (Seed: MPSaveManager.GetSeed(), Id: WorldSaveIdentity.Active, Host: MyMod.iAmHost);
        if (current.Seed == 0) return false;
        if (current != world) Reset(current);
        return true;
    }

    private static void Reset((int Seed, string Id, bool Host) current)
    {
        world = current;
        Known.Clear();
        Pending.Clear();
        hostLoaded = false;
        importInventory = current.Host;
        applyNeeded = true;
        appliedScene = null;
        nextRetry = nextHostLoad = nextGrant = 0;
        grantDelay = 2f;
    }

    private static bool ValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length <= 5 || name.Length > MaxNameLength ||
            !name.StartsWith("GEAR_", StringComparison.Ordinal)) return false;
        for (int i = 5; i < name.Length; i++)
            if (name[i] is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_'))
                return false;
        return true;
    }

    private static NarrativeCollectibleItem Note(GearItem gear)
    {
        if (gear == null || gear.m_ResearchItem != null || gear.m_MissionObject != null) return null;
        var note = gear.GetComponent<NarrativeCollectibleItem>();
        // Native memento pickups also update regional discovery and achievements.
        if (note == null || note.m_Type is not (NarrativeCollectibleItem.CollectibleType.Note or
                NarrativeCollectibleItem.CollectibleType.Story) ||
            string.IsNullOrEmpty(note.m_NarrativeTextLocID) || note.IsMementoItem()) return null;
        return note;
    }

    private static GearItem NotePrefab(string name)
    {
        var prefab = Resources.Load<GameObject>(name)?.GetComponent<GearItem>() ?? GameCompat.LoadGearItemPrefab(name);
        return Note(prefab) != null && prefab.GetGearName() == name ? prefab : null;
    }

    private static bool SameNote(NarrativeCollectibleItem note, NarrativeCollectibleItem prefabNote) =>
        note != null && prefabNote != null && note.m_Type == prefabNote.m_Type &&
        note.m_NarrativeTextLocID == prefabNote.m_NarrativeTextLocID;

    // The pickup paths call this after accepting the note, including the no-inspect
    // and container paths. The native reading panel can open before acquisition.
    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.AddItemToPlayerInventory))]
    private static class AcquireNote
    {
        private static void Prefix(GearItem gi, bool trackItemLooted, out string __state)
        {
            __state = null;
            if (!trackItemLooted || !InWorld || gi == null || gi.m_BeenInPlayerInventory ||
                gi.GetComponent<Comps.DroppedGearDummy>() != null || Note(gi) == null) return;
            __state = gi.GetGearName();
        }

        private static void Postfix(GearItem __result, string __state)
        {
            if (!ValidName(__state) || __result == null || !__result.m_InPlayerInventory ||
                !RefreshWorld() || Known.Contains(__state) || Pending.Count >= MaxNotes) return;
            if (!SameNote(Note(__result), Note(NotePrefab(__state)))) return;

            if (MyMod.iAmHost && Record(new[] { __state })) return;
            if (Pending.Add(__state) && !MyMod.iAmHost) SendToHost(__state);
        }
    }

    private static bool LoadHost()
    {
        if (!MyMod.iAmHost || !RefreshWorld()) return false;
        if (hostLoaded) return true;
        if (Time.realtimeSinceStartup < nextHostLoad) return false;
        try
        {
            string saved = MPSaveManager.LoadData(SaveName, world.Seed);
            if (!string.IsNullOrEmpty(saved))
            {
                if (saved.Length > MaxNotes * (MaxNameLength + 3) + 2)
                    throw new JsonException("Too many shared note records.");
                foreach (string name in JsonSerializer.Deserialize<string[]>(saved) ?? Array.Empty<string>())
                    if (ValidName(name) && Known.Count < MaxNotes) Known.Add(name);
            }
            hostLoaded = true;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            nextHostLoad = Time.realtimeSinceStartup + 10f;
            MelonLogger.Warning("[Collectible notes] Cannot read world state: " + ex.Message);
            return false;
        }
    }

    private static bool Record(IEnumerable<string> names)
    {
        if (!LoadHost()) return false;
        var added = names.Where(name => !Known.Contains(name)).Distinct(StringComparer.Ordinal)
            .Take(MaxNotes - Known.Count).ToArray();
        if (added.Length == 0) return true;
        try
        {
            MPSaveManager.ValidateRootExits();
            if (!MPSaveManager.SaveData(SaveName, JsonSerializer.Serialize(Known.Concat(added)), world.Seed))
                return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MelonLogger.Warning("[Collectible notes] Cannot save world state: " + ex.Message);
            return false;
        }
        Known.UnionWith(added);
        applyNeeded = true;
        foreach (var peer in Server.clients.Values)
        {
            if (!peer.Ready || peer.RCON || peer.Departing || !peer.IsBusy()) continue;
            foreach (string name in added) SendNote(peer.id, name);
        }
        return true;
    }

    private static Packet MakePacket(string name)
    {
        var packet = new Packet(PacketId);
        packet.Write(name);
        return packet;
    }

    private static string ReadName(Packet packet)
    {
        int length = packet.UnreadLength() - sizeof(int);
        if (length <= 0 || length > MaxNameLength || packet.ReadInt(false) != length) return null;
        string name = packet.ReadString();
        return ValidName(name) ? name : null;
    }

    private static void SendToHost(string name)
    {
        using var packet = MakePacket(name);
        Pathes.SendTCPData(packet);
    }

    private static void SendNote(int to, string name)
    {
        using var packet = MakePacket(name);
        ServerSend.SendTCPData(to, packet);
    }

    internal static void ReceiveClient(int from, Packet packet)
    {
        if (!MyMod.iAmHost || from <= 0 || from >= MyMod.playersData.Count ||
            !WildlifeSpawnAuthorityPolicy.WorldScene(MyMod.playersData[from]?.m_LevelGuid) ||
            ReadName(packet) is not { } name || !LoadHost()) return;
        if (!Known.Contains(name))
        {
            if (Known.Count < MaxNotes && NotePrefab(name) != null) Record(new[] { name });
            return;
        }
        // A reliable duplicate also confirms an earlier discovery to its sender.
        SendNote(from, name);
    }

    internal static void SendSnapshot(int to)
    {
        if (!LoadHost()) return;
        foreach (string name in Known) SendNote(to, name);
    }

    internal static void Receive(Packet packet)
    {
        if (MyMod.iAmHost || ReadName(packet) is not { } name || !RefreshWorld()) return;
        Pending.Remove(name);
        if (Known.Count < MaxNotes && Known.Add(name)) applyNeeded = true;
        if (Known.Count == MaxNotes) Pending.Clear();
    }

    internal static void Tick()
    {
        if (!RefreshWorld() || !InWorld)
        {
            appliedScene = null;
            return;
        }
        float now = Time.realtimeSinceStartup;
        if (Pending.Count > 0 && now >= nextRetry)
        {
            nextRetry = now + 3f;
            if (MyMod.iAmHost)
            {
                if (Record(Pending)) Pending.Clear();
            }
            else
                foreach (string name in Pending) SendToHost(name);
        }

        if (MyMod.DelayedGearsPickup || GameManager.m_PlayerManager == null ||
            GameManager.m_PlayerManager.IsInspectModeActive()) return;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        bool sceneChanged = appliedScene != MyMod.level_guid || appliedSceneHandle != scene.handle;
        bool refresh = applyNeeded || sceneChanged;
        if (!refresh && (nextGrant == 0 || now < nextGrant)) return;
        var inventory = GameManager.GetInventoryComponent();
        if (inventory == null || inventory.m_Items == null) return;
        if (MyMod.iAmHost && !LoadHost()) return;

        var owned = new HashSet<string>(StringComparer.Ordinal);
        var previous = new List<string>();
        foreach (var entry in inventory.m_Items)
        {
            var gear = entry?.m_GearItem;
            if (Note(gear) is not { } note) continue;
            string name = gear.GetGearName();
            owned.Add(name);
            if (importInventory && ValidName(name) && !Known.Contains(name) &&
                SameNote(note, Note(NotePrefab(name))))
                previous.Add(name);
        }
        importInventory = false;
        if (previous.Count > 0 && !Record(previous))
            foreach (string name in previous)
                if (Pending.Count < MaxNotes) Pending.Add(name);

        // Panel_Log builds its collectible list from the native inventory. The
        // prefab path adds the journal entry without running pickup cinematics.
        bool missing = false;
        bool grantedAny = false;
        foreach (string name in Known)
        {
            if (owned.Contains(name)) continue;
            var prefab = NotePrefab(name);
            var granted = prefab != null ? GameManager.m_PlayerManager.InstantiateItemInPlayerInventory(prefab) : null;
            if (granted != null && granted.m_InPlayerInventory)
            {
                owned.Add(name);
                grantedAny = true;
            }
            else missing = true;
        }
        if (refresh || grantedAny) grantDelay = 2f;
        nextGrant = missing ? now + grantDelay : 0f;
        if (missing) grantDelay = Math.Min(grantDelay * 2f, 30f);

        if (Known.Count > 0 && (refresh || grantedAny))
        {
            foreach (var note in UnityEngine.Object.FindObjectsOfType<NarrativeCollectibleItem>())
            {
                if (note == null || !note.gameObject.scene.IsValid()) continue;
                var gear = note.GetComponent<GearItem>();
                if (gear == null || gear.m_BeenInPlayerInventory || gear.m_InPlayerInventory ||
                    gear.m_InsideContainer || gear.GetComponent<Comps.DroppedGearDummy>() != null) continue;
                string name = gear.GetGearName();
                if (!Known.Contains(name) || !owned.Contains(name) ||
                    !SameNote(Note(gear), Note(NotePrefab(name)))) continue;
                var spawner = gear.GetComponentInParent<RadialObjectSpawner>();
                if (spawner != null) spawner.RemoveFromSpawns(gear.gameObject);
                gear.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(gear.gameObject);
            }
        }
        applyNeeded = false;
        appliedScene = MyMod.level_guid;
        appliedSceneHandle = scene.handle;
    }
}

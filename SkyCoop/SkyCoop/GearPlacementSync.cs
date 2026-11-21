using System;
using System.Text.Json;
using GameServer;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.Placement;
using MelonLoader;
using UnityEngine;

namespace SkyCoop;

internal static class GearPlacementSync
{
    private sealed record Pose(float X, float Y, float Z, float A, float B, float C, float D)
    {
        internal static Pose From(Transform t) => From(t.position, t.rotation);
        internal static Pose From(Vector3 p, Quaternion q) => new(p.x, p.y, p.z, q.x, q.y, q.z, q.w);
        internal Vector3 Position => new(X, Y, Z);
        internal Quaternion Rotation => new(A, B, C, D);
        internal bool Valid => float.IsFinite(Position.sqrMagnitude) &&
            Mathf.Abs(A * A + B * B + C * C + D * D - 1f) < .02f;
    }

    private sealed record Move(Pose Original, Pose Target, int BedVariant, Guid Operation);
    private sealed class Preview
    {
        internal string Scene;
        internal int Key;
        internal GameObject Source;
        internal GameObject Item;
        internal Pose Original;
        internal Move Requested;
        internal float Expires;
        internal float Retry;
        internal string Json = "";
    }

    private static Preview preview;
    internal static bool Busy => preview != null;

    internal static bool Request(Comps.DroppedGearDummy dummy)
    {
        if (preview != null || dummy == null) return false;
        preview = new Preview { Scene = MyMod.level_guid, Key = dummy.m_SearchKey, Source = dummy.gameObject,
            Original = Pose.From(dummy.transform), Expires = Time.realtimeSinceStartup + 12f };
        return true;
    }

    internal static void SendPreview(int player, string scene, int key)
    {
        if (player <= 0 || player >= MyMod.playersData.Count || MyMod.playersData[player]?.m_LevelGuid != scene) return;
        var data = MPSaveManager.RequestSpecificGear(key, scene, Remove: false);
        if (data == null) ScenePlacementSync.ReplyMissingPreview(player, scene, key);
        else Shared.SendDroppedItemToPicker(data.m_Json, player, key, -1, place: true, ThrownFlareSync.SnapshotPickup(data.m_Extra));
    }

    internal static void MissingPreview(int key)
    {
        if (!Expects(key)) return;
        End();
        HUDMessage.AddMessage("Предмет уже убран или недоступен");
    }

    internal static bool Expects(int key) => preview?.Key == key && preview.Scene == MyMod.level_guid && preview.Item == null && preview.Requested == null;

    internal static string ReadPreview(DataStr.SlicedJsonData slice)
    {
        if (!Expects(slice.m_Hash)) return null;
        preview.Json += slice.m_Str;
        return slice.m_Last ? preview.Json : null;
    }

    internal static bool Start(PlayerManager player, GameObject obj, int key)
    {
        if (!Expects(key) || preview.Source == null || !CanInteract(player))
        {
            if (obj != null) UnityEngine.Object.Destroy(obj);
            if (Expects(key)) End();
            return false;
        }
        var current = preview;
        current.Item = obj;
        obj.transform.SetPositionAndRotation(current.Original.Position, current.Original.Rotation);
        if (player.StartPlaceMesh(obj, PlaceMeshFlags.None))
        {
            current.Source.SetActive(false);
            return true;
        }
        End();
        UnityEngine.Object.Destroy(obj);
        return false;
    }

    internal static bool CanInteract(PlayerManager player) => player != null && !player.IsInPlacementMode() &&
        !player.IsInspectModeActive() && player.GetControlMode() is PlayerControlMode.Normal or PlayerControlMode.SafehouseCustomization &&
        !InterfaceManager.IsOverlayActiveImmediate();

    internal static bool Finish(GameObject obj, bool placed)
    {
        if (preview == null || preview.Item != obj) return false;
        var current = preview;
        current.Item = null;
        if (placed)
        {
            var bed = obj.GetComponent<GearItem>()?.m_Bed;
            int variant = bed?.m_Bedroll != null ? (bed.m_BedRollState == BedRollState.Placed ? 1 : 0) : -1;
            current.Requested = new Move(current.Original, Pose.From(obj.transform), variant, Guid.NewGuid());
            current.Expires = Time.realtimeSinceStartup + 12f;
            Send(current);
        }
        else End();
        UnityEngine.Object.Destroy(obj);
        return true;
    }

    internal static void Tick()
    {
        var current = preview;
        if (current == null) return;
        if (!MyMod.InOnline() || current.Scene != MyMod.level_guid || MyMod.LoadingScreenIsOn)
        {
            End();
            return;
        }
        if (current.Item != null)
        {
            var player = GameManager.GetPlayerManagerComponent();
            if (player != null && player.m_ObjectToPlace != current.Item && !player.IsInPlacementMode())
                Finish(current.Item, placed: false);
            return;
        }
        if (Time.realtimeSinceStartup >= current.Expires)
        {
            End();
            HUDMessage.AddMessage("Не удалось подтвердить перенос предмета");
        }
        else if (current.Requested != null && Time.realtimeSinceStartup >= current.Retry) Send(current);
    }

    private static void End()
    {
        var current = preview;
        preview = null;
        if (current?.Source != null && MyMod.DroppedGearsObjs.TryGetValue(current.Key, out var source) && source == current.Source)
            source.SetActive(true);
    }

    private static void Send(Preview current)
    {
        current.Retry = Time.realtimeSinceStartup + 1f;
        string payload = JsonSerializer.Serialize(current.Requested);
        if (MyMod.iAmHost) Accept(current.Scene, current.Key, payload, 0);
        else ScenePlacementSync.SendDroppedMove(current.Scene, current.Key, payload);
    }

    internal static void Accept(string scene, int key, string payload, int sender)
    {
        var move = JsonSerializer.Deserialize<Move>(payload);
        if (move?.Original?.Valid != true || move.Target?.Valid != true || move.BedVariant is < -1 or > 1 || move.Operation == Guid.Empty ||
            Vector3.Distance(move.Original.Position, move.Target.Position) > 12f) return;
        string response = JsonSerializer.Serialize(new Move(null, null, -1, move.Operation));
        var visuals = MPSaveManager.LoadDropVisual(scene);
        var data = MPSaveManager.RequestSpecificGear(key, scene, Remove: false);
        if (visuals != null && visuals.TryGetValue(key, out var visual) && visual?.m_Extra != null && data != null)
        {
            var current = Pose.From(visual.m_Position, visual.m_Rotation);
            if (SamePose(current, move.Original) && (!SamePose(current, move.Target) ||
                move.BedVariant >= 0 && move.BedVariant != visual.m_Extra.m_Variant))
            {
                visual.m_Position = move.Target.Position;
                visual.m_Rotation = move.Target.Rotation;
                if (move.BedVariant >= 0 && visual.m_Extra.m_GearName.Contains("bedroll", StringComparison.OrdinalIgnoreCase))
                {
                    visual.m_Extra.m_Variant = move.BedVariant;
                    data.m_Extra.m_Variant = move.BedVariant;
                    MPSaveManager.AddGearData(scene, key, data);
                }
                MPSaveManager.AddGearVisual(scene, visual);
                current = move.Target;
                string update = JsonSerializer.Serialize(new Move(move.Original, current, visual.m_Extra.m_Variant, move.Operation));
                if (scene == MyMod.level_guid && !MyMod.DedicatedServerAppMode) Receive(key, update, reply: false);
                ScenePlacementSync.BroadcastDroppedMove(scene, key, update);
            }
            response = JsonSerializer.Serialize(new Move(current, current, visual.m_Extra.m_Variant, move.Operation));
        }
        if (sender > 0) ScenePlacementSync.ReplyDroppedMove(sender, scene, key, response);
        else Receive(key, response, reply: true);
    }

    private static bool SamePose(Pose left, Pose right) => left != null && right != null &&
        (left.Position - right.Position).sqrMagnitude < .000001f && Mathf.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) > .99999f;

    internal static void Receive(int key, string payload, bool reply)
    {
        var move = JsonSerializer.Deserialize<Move>(payload);
        if (reply)
        {
            if (preview?.Key != key || preview.Requested?.Operation != move?.Operation) return;
            End();
        }
        if (!MyMod.DroppedGearsObjs.TryGetValue(key, out var obj) || obj == null) return;
        if (move?.Target == null)
        {
            if (reply) MyMod.PickDroppedItem(key, -1);
            return;
        }
        if (!move.Target.Valid || !reply && !SamePose(Pose.From(obj.transform), move.Original)) return;
        var dummy = obj.GetComponent<Comps.DroppedGearDummy>();
        if (dummy == null) return;
        var extra = dummy.m_Extra;
        if (extra.m_GearName.Contains("bedroll", StringComparison.OrdinalIgnoreCase) && extra.m_Variant != move.BedVariant)
        {
            extra.m_Variant = move.BedVariant;
            obj.SetActive(false);
            MyMod.PickDroppedItem(key, -1);
            MyMod.FakeDropItem(-1, move.Target.Position, move.Target.Rotation, key, extra);
        }
        else
        {
            obj.transform.SetPositionAndRotation(move.Target.Position, move.Target.Rotation);
            if (dummy.m_DecoyDummy != null)
                dummy.m_DecoyDummy.transform.SetPositionAndRotation(move.Target.Position, move.Target.Rotation);
        }
    }

    private static void Prune<T>(Il2CppSystem.Collections.Generic.List<T> items) where T : UnityEngine.Object
    {
        if (items == null) return;
        for (int index = items.Count - 1; index >= 0; index--)
            if (items[index] == null) items.RemoveAt(index);
    }

    private static void CleanPlacementLists(PlayerManager player)
    {
        Prune(player.m_ObjectToPlaceColliders);
        Prune(player.m_ObjectToPlaceBoxColliders);
        Prune(player.m_ObjectToPlaceIgnoreColliders);
        Prune(player.m_ObjectToPlaceRenderers);
        Prune(player.m_TintedRenderers);
    }

    [HarmonyPatch(typeof(Utils), nameof(Utils.AreCollidersActiveAndEnabled), new[] { typeof(Il2CppSystem.Collections.Generic.List<Collider>) })]
    private static class ValidateColliders
    {
        private static void Prefix(Il2CppSystem.Collections.Generic.List<Collider> __0)
        {
            if (MyMod.InOnline()) Prune(__0);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.InitializeRenderers))]
    private static class ValidateRenderers
    {
        private static void Prefix(PlayerManager __instance)
        {
            if (MyMod.InOnline()) CleanPlacementLists(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.InitializeObjectToPlace))]
    private static class PreparePlacement
    {
        private static void Prefix(PlayerManager __instance, GameObject __0)
        {
            if (!MyMod.InOnline()) return;
            CleanPlacementLists(__instance);
            if (__0 != null) PickupSync.Remember(__0.GetComponent<GearItem>());
        }
    }

    [HarmonyPatch]
    private static class RecoverPlacement
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.StartPlaceMesh), new[] { typeof(GameObject), typeof(PlaceMeshFlags), typeof(PlaceMeshRules) }),
            AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.StartPlaceMesh), new[] { typeof(GameObject), typeof(float), typeof(PlaceMeshFlags), typeof(PlaceMeshRules) })
        };

        private static Exception Finalizer(PlayerManager __instance, GameObject __0, ref bool __result, Exception __exception)
        {
            if (__exception == null || !MyMod.InOnline()) return __exception;
            MelonLogger.Warning("[Placement] Start failed, cancelling: " + __exception.Message);
            if (__instance.m_ObjectToPlace == __0)
            {
                CleanPlacementLists(__instance);
                try { __instance.CancelPlacementMode(); }
                catch (Exception cleanup)
                {
                    MelonLogger.Warning("[Placement] Native cancel failed: " + cleanup.Message);
                    __instance.ExitMeshPlacement();
                }
            }
            else if (__instance.m_ObjectToPlace == null && __instance.GetControlMode() is PlayerControlMode.PlaceMesh or PlayerControlMode.PlaceMeshPending)
                __instance.ExitMeshPlacement();
            __result = false;
            return null;
        }
    }
}

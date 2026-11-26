using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GameServer;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.IntBackedUnit;
using Il2CppTLD.Interactions;
using Il2CppTLD.Placement;
using Il2CppTLD.UI.Scroll;
using UnityEngine;

namespace TldCoop;

internal static class ScenePlacementSync
{
    [HarmonyPatch(typeof(ScrollBehaviour), nameof(ScrollBehaviour.RebuildItems))]
    private static class CookingListBounds
    {
        private static void Prefix(ScrollBehaviour __instance, ref int __0)
        {
            var panel = GameCompat.Panel<Panel_Cooking>();
            if (panel == null || panel.m_ScrollBehaviour != __instance || panel.m_FoodList == null) return;
            __0 = panel.m_FoodList.Count;
            int itemsPerLine = Math.Max(1, __instance.m_ItemsPerLine);
            int lastLine = Math.Max(0, (__0 + itemsPerLine - 1) / itemsPerLine - Math.Max(1, __instance.m_VisibleLines));
            __instance.m_FirstLine = Math.Clamp(__instance.m_FirstLine, 0, lastLine);
            __instance._SelectedIndex_k__BackingField = Math.Clamp(__instance.SelectedIndex, -1, __0 - 1);
        }
    }

    [HarmonyPatch(typeof(Panel_Cooking), nameof(Panel_Cooking.BuildItem))]
    private static class CookingListRow
    {
        private static bool Prefix(Panel_Cooking __instance, ScrollBehaviourItem __1, int __2)
        {
            bool valid = __instance.m_FoodList != null && (uint)__2 < (uint)__instance.m_FoodList.Count;
            if (__1 == null) return false;
            if (__1.gameObject.activeSelf != valid) __1.gameObject.SetActive(valid);
            return valid;
        }
    }

    internal const int ClientPacket = 199;
    internal const int ServerPacket = 197;

    private sealed record Pose(float X, float Y, float Z, float Qx, float Qy, float Qz, float Qw)
    {
        internal static Pose From(Transform t) => new(t.position.x, t.position.y, t.position.z,
            t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w);
        internal Vector3 Position => new(X, Y, Z);
        internal Quaternion Rotation => new(Qx, Qy, Qz, Qw);
        internal bool Valid => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) &&
            float.IsFinite(Qx) && float.IsFinite(Qy) && float.IsFinite(Qz) && float.IsFinite(Qw);
    }

    private const int FurnitureHistoryLimit = 6;
    private sealed record Move(string Id, string Parent, Pose Pose, string Operation, long Revision, int Version = 0, string Anchor = "", string ParentAnchor = "", string Asset = "", string AssetName = "", float Sx = 1f, float Sy = 1f, float Sz = 1f, bool RegisterOnly = false, bool Removed = false, string Visible = "", string PreviousId = "", Pose[] History = null, long BaseRevision = -1, Pose Origin = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Pose Relative = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool Stored = false);
    private sealed class FurniturePlacement
    {
        internal GameObject Object;
        internal Move[] Moves;
        internal Dictionary<string, GameObject> Objects = new(StringComparer.Ordinal);
        internal GameObject Replacement;
    }
    private static FurniturePlacement placingFurniture;
    private static bool creatingReplica;
    private static float nextFurnitureApply;
    private static float furnitureRetryDelay = 1f;
    private static bool furnitureSectorsDirty;
    private static readonly HashSet<string> FurnitureApplyWarnings = new();
    private static readonly Dictionary<string, int> FurnitureCreateAttempts = new();
    private static readonly Dictionary<string, GameObject> FurnitureAliases = new(StringComparer.Ordinal);
    private static readonly Dictionary<(string Area, string Id), string> NativeFurnitureIds = new();
    private static (string Id, int Seed) nativeFurnitureWorld;
    private sealed record FurnitureBinding(string Area, string Id, string NativeId);
    private static bool bindingsChanged;
    private static readonly Dictionary<string, (Move Move, GameObject Object)> SettlingFurniture = new(StringComparer.Ordinal);
    private sealed record CookingFood(float Condition, float Weight, float Calories, float TotalCalories, float Heat, int Reheats);
    private sealed record Pot(string Gear, Pose Pose, Pose Slot, string Meshes, string Food, string Mode, int Minutes, bool CanTake, int Liquid, string NativeHover, float Liters = 0f, Guid Portion = default,
        CookingFood FoodState = null, float WaterDuringMelt = 0f, float Cooked = 0f, float Elapsed = 0f);
    private const int UseEmptyCookware = 5;
    private sealed record CookwareHandoff(Pose Slot, Pose Pose);
    private sealed record GearTransfer(Guid Id, int Requester, string Gear, string Json, float Heat, int TimesHeated,
        float Condition, int SourceOwner, int SourceKey, CookwareHandoff Placement, CookingFood FoodState = null);
    private sealed record CookingAuthorization(string Scene, int Owner, int Key, int Requester, int Action, Pose Slot, float Expires);
    private static readonly Dictionary<Guid, CookingAuthorization> AuthorizedCookingTransfers = new();
    private sealed record CookingDelivery(string Scene, GearTransfer Gear, string Data, string Recipient, float Expires, float NextSend = 0f);
    private static readonly Dictionary<Guid, CookingDelivery> OutgoingCookingGear = new();
    private static readonly Dictionary<Guid, CookingDelivery> HostCookingGear = new();
    private sealed record WaterTransfer(Guid Id, int Requester, float Liters, bool Potable);
    private static readonly Dictionary<Guid, float> ReceivedOutputs = new();
    private sealed record RemotePot(Pot State, GameObject Visual, Collider[] Hitboxes, float Expires);
    private static readonly Dictionary<string, Dictionary<string, Move>> Saved = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, FurnitureContainerIndex> StoredFurniture = new(StringComparer.Ordinal);
    // LoadData migrates old Unity vectors by lowercasing X, Y and Z before returning JSON.
    private static readonly JsonSerializerOptions FurnitureSaveOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<(string Area, string Id), (Move Move, float SentAt)> Pending = new();
    private static readonly FurnitureSnapshot<Move> JoiningFurniture = new();
    private static Dictionary<string, long> snapshotKnown;
    private static int snapshotRequest;
    private static bool furnitureReady;
    private static GameObject placementOriginObject;
    private static Pose placementOrigin;
    private static long placementRevision;
    private static readonly Dictionary<string, Move> Unapplied = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LatestRevision = new(StringComparer.Ordinal);
    private static readonly HashSet<string> RemovedFurniture = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AcceptedOperations = new(StringComparer.Ordinal);
    private static readonly Queue<string> OperationOrder = new();
    private static readonly Dictionary<int, CookingPotItem> LocalPots = new();
    private static readonly Dictionary<int, GearPlacePoint> CookingPoints = new();
    private static readonly Dictionary<int, (GearPlacePoint Point, GearItem Gear, CookingPotItem Pot)> LocalCooking = new();
    private sealed record CookingPortion(int FoodId, int Action, Guid Id);
    private sealed record CookingRequest(int Owner, int Action, Guid Portion);
    private static readonly Dictionary<int, CookingPortion> LocalCookingPortions = new();
    private static float nextCookingScan;
    private static float nextCookingPublish;
    private sealed record SlotClaim(Pot State, float Expires);
    private static readonly Dictionary<(string Scene, int Owner, int Key), SlotClaim> CookingClaims = new();
    private static readonly HashSet<int> RejectedCooking = new();
    private static int cookingSession;
    private const float SlotMatchSquared = .0064f;
    private static readonly Dictionary<(int Owner, int Key), RemotePot> RemotePots = new();
    private static readonly HashSet<int> PublishedPots = new();
    private static int serverSession;
    private static string furnitureWorld;
    private static int furnitureSeed;
    private static float replayFurnitureAt;
    private static int furnitureReplays;
    private static string scene;
    private static int cookingDepth;
    private static float nextRequest;
    private static long revision;
    internal static bool NativeCooking => cookingDepth > 0;

    internal static bool IsCookingDummy(GearItem gear) =>
        gear != null && IsCookingDummyName(gear.GetGearName());

    internal static bool IsCookingDummyName(string name) => IsPotName(name) && name.EndsWith("Dummy", StringComparison.OrdinalIgnoreCase);

    internal static bool KeepNativeCookingObject(GearItem gear) =>
        gear != null && (IsCookingDummy(gear) || gear.m_CookingPotItem?.GetGearPlacePoint() != null);

    private static bool InWorld => MyMod.InOnline() && !MyMod.LoadingScreenIsOn &&
        WildlifeSpawnAuthorityPolicy.WorldScene(MyMod.level_guid);

    private static string Id(GameObject obj)
    {
        if (obj == null) return "";
        foreach (var alias in FurnitureAliases)
            if (alias.Value == obj) return alias.Key;
        if (OriginalDecoration(obj) is { } original) return original.Id;
        var placeable = obj.GetComponent<Placeable>();
        if (placeable != null && !string.IsNullOrEmpty(placeable.m_Guid) && placeable.m_Addressable != null)
        {
            foreach (var binding in NativeFurnitureIds)
                if (binding.Key.Area == MyMod.level_guid && binding.Value == placeable.m_Guid)
                    return binding.Key.Id;
            return placeable.m_Guid;
        }
        string persistent = obj.GetComponent<ObjectGuid>()?.Get() ?? "";
        if (persistent.Length > 0) return persistent;
        if (placeable != null && !string.IsNullOrEmpty(placeable.m_Guid)) return placeable.m_Guid;
        return "";
    }

    internal static string FurnitureIdentity(GameObject obj)
    {
        var placeable = obj.GetComponentInParent<Placeable>();
        if (placeable != null) return Id(placeable.gameObject);
        var decoration = obj.GetComponentInParent<DecorationItem>();
        return Id(decoration != null ? decoration.gameObject : obj);
    }

    internal static string CanonicalFurnitureId(string area, string nativeId)
    {
        foreach (var binding in NativeFurnitureIds)
            if (binding.Key.Area == area && binding.Value == nativeId) return binding.Key.Id;
        return nativeId;
    }

    private sealed record NativeDecoration(DecorationItem Item, string Id, string Anchor, Pose Origin);
    private static readonly Dictionary<int, NativeDecoration> OriginalDecorations = new();
    private static readonly HashSet<int> ConfirmedNativeDecorations = new();
    private static int nativeConfirmationFrame = -1;

    private static NativeDecoration OriginalDecoration(GameObject obj)
    {
        var decoration = obj != null ? obj.GetComponent<DecorationItem>() : null;
        return decoration != null ? OriginalDecorations.GetValueOrDefault(decoration.GetInstanceID()) : null;
    }

    private static string Anchor(GameObject obj) => OriginalDecoration(obj)?.Anchor ?? "";

    private static bool NativePlacementConfirmed(GameObject obj) => obj != null &&
        nativeConfirmationFrame == Time.frameCount && ConfirmedNativeDecorations.Contains(obj.GetInstanceID());

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.Awake))]
    private static class RememberOriginalFurniture
    {
        private static void Prefix(DecorationItem __instance)
        {
            var obj = __instance.gameObject;
            string name = obj.name;
            if (creatingReplica || !obj.scene.IsValid() || name.Contains("(Clone)", StringComparison.Ordinal) ||
                name.EndsWith(" (PLACED)", StringComparison.Ordinal)) return;
            var pose = Pose.From(obj.transform);
            string identity = FormattableString.Invariant(
                $"{name.Trim()}|{pose.X:F2}|{pose.Y:F2}|{pose.Z:F2}");
            string anchor = Shared.StableHash(identity).ToString("X8");
            string id = obj.GetComponent<ObjectGuid>()?.Get();
            OriginalDecorations[__instance.GetInstanceID()] = new(__instance,
                string.IsNullOrEmpty(id) ? "anchor-" + anchor : id, anchor, pose);
        }
    }

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.OnDestroy))]
    private static class ForgetFurnitureAnchor
    {
        private static void Prefix(DecorationItem __instance) => OriginalDecorations.Remove(__instance.GetInstanceID());
    }

    // Build once per apply batch. Avoid IL2CPP Dictionary.TryGetValue with the
    // game's boxed PlaceableInfo struct as an out argument.
    private sealed class PlacementLookup
    {
        private Dictionary<string, Placeable> byGuid;
        private Dictionary<string, List<Placeable>> byAsset;

        private void EnsureIndex()
        {
            if (byGuid != null) return;
            byGuid = new Dictionary<string, Placeable>(StringComparer.Ordinal);
            byAsset = new Dictionary<string, List<Placeable>>(StringComparer.Ordinal);
            foreach (var placeable in UnityEngine.Object.FindObjectsOfType<Placeable>(true)) Register(placeable);
        }

        internal Placeable Find(string id)
        {
            EnsureIndex();
            return byGuid.TryGetValue(id, out var match) && match != null && !match.m_Invalidated ? match : null;
        }

        internal IEnumerable<Placeable> ForAsset(string asset)
        {
            EnsureIndex();
            return byAsset.TryGetValue(asset, out var candidates) ? candidates : Array.Empty<Placeable>();
        }

        internal void Register(Placeable placeable)
        {
            if (byGuid == null || placeable == null || placeable.m_Invalidated) return;
            string asset = placeable.m_Addressable?.AssetGUID;
            if (!string.IsNullOrEmpty(asset))
            {
                if (!byAsset.TryGetValue(asset, out var candidates)) byAsset[asset] = candidates = new List<Placeable>();
                candidates.Add(placeable);
            }
            string guid = placeable.m_Guid;
            if (!string.IsNullOrEmpty(guid)) byGuid[guid] = placeable;
        }
    }

    private static GameObject Find(string id, string anchor = "", PlacementLookup lookup = null)
    {
        if (!string.IsNullOrEmpty(anchor))
        {
            var originals = OriginalDecorations.Values.Where(original => original.Item != null && original.Anchor == anchor)
                .Take(2).ToArray();
            if (originals.Length == 1) return originals[0].Item.gameObject;
        }
        if (string.IsNullOrEmpty(id)) return null;
        if (FurnitureAliases.TryGetValue(id, out var alias) && alias != null &&
            alias.GetComponent<Placeable>()?.m_Invalidated != true) return alias;
        if (id.StartsWith("breakdown-", StringComparison.Ordinal))
        {
            BreakDown match = null;
            foreach (var item in UnityEngine.Object.FindObjectsOfType<BreakDown>(true))
            {
                if (item == null || BreakdownId(item) != id) continue;
                if (match != null) return null;
                match = item;
            }
            return match != null ? match.gameObject : null;
        }
        var native = OriginalDecorations.Values.Where(original => original.Item != null && original.Id == id).Take(2).ToArray();
        if (native.Length == 1) return native[0].Item.gameObject;
        var obj = ObjectGuidManager.Lookup(id);
        if (DecorationItem.TryGetDecorationFromId(id, out var decoration) && decoration != null) return decoration.gameObject;
        lookup ??= new PlacementLookup();
        var placeable = lookup.Find(id);
        if (placeable != null) return placeable.gameObject;
        if (NativeFurnitureIds.TryGetValue((MyMod.level_guid, id), out string nativeId) &&
            !LatestRevision.ContainsKey(nativeId) && lookup.Find(nativeId) is { } known && known != null)
            return known.gameObject;
        if (obj != null)
        {
            var placed = obj.GetComponent<Placeable>();
            if (placed?.m_Invalidated != true && (placed != null || obj.GetComponent<DecorationItem>() != null)) return obj;
        }
        return null;
    }

    private static string RendererKey(Renderer renderer, Transform root)
    {
        string path = "";
        for (Transform part = renderer.transform; part != root; part = part.parent)
            path = part.name + "#" + part.GetSiblingIndex() + "/" + path;
        return Shared.StableHash(path + renderer.GetIl2CppType().Name).ToString("X8");
    }

    private static bool VisibleRenderer(Renderer renderer) => renderer != null &&
        (renderer.TryCast<MeshRenderer>() != null || renderer.TryCast<SkinnedMeshRenderer>() != null) &&
        renderer.enabled && !renderer.forceRenderingOff && renderer.gameObject.activeInHierarchy;

    private static string CaptureVisibleMeshes(GameObject obj)
    {
        return string.Concat(obj.GetComponentsInChildren<Renderer>(true).Where(VisibleRenderer)
            .Select(renderer => RendererKey(renderer, obj.transform)).Distinct().Take(24));
    }

    private static int RestoreFurnitureMeshes(GameObject obj, string visible)
    {
        if (string.IsNullOrEmpty(visible)) return 0;
        var renderers = obj.GetComponentsInChildren<Renderer>(true);
        if (renderers.Any(VisibleRenderer)) return 0;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i + 8 <= visible.Length; i += 8) keys.Add(visible.Substring(i, 8));
        int restored = 0;
        foreach (var renderer in renderers)
        {
            if (renderer == null || !keys.Contains(RendererKey(renderer, obj.transform))) continue;
            renderer.forceRenderingOff = false;
            renderer.enabled = true;
            if (renderer.transform != obj.transform && renderer.transform.parent.gameObject.activeInHierarchy)
                renderer.gameObject.SetActive(true);
            if (VisibleRenderer(renderer)) restored++;
        }
        return restored;
    }

    private static bool IsFixedBed(GameObject obj) => obj != null &&
        (obj.GetComponent<DecorationItem>() != null || obj.GetComponent<Placeable>() != null) &&
        obj.GetComponentInParent<SnowShelter>() == null && obj.GetComponentInChildren<SnowShelter>(true) == null &&
        obj.GetComponentInChildren<Bed>(true) is { } bed && bed.m_Bedroll == null;

    internal static bool CanPlace(GameObject obj)
    {
        if (!MyMod.InOnline() || obj == null) return true;
        if (obj.GetComponent<Comps.FakeBedDummy>() != null) return false;
        if (!IsFixedBed(obj)) return true;
        if (!BedRest.IsOccupied(obj.GetComponentInChildren<Bed>(true)) && Id(obj).Length > 0) return true;
        HUDMessage.AddMessage("Освободите кровать перед перестановкой");
        return false;
    }

    [HarmonyPatch(typeof(PlayerManager), "AttemptToPlaceMesh")]
    private static class BedPlacementGuard
    {
        private static bool Prefix(PlayerManager __instance) => CanPlace(__instance.m_ObjectToPlace);
    }

    internal static void PublishFurniture(GameObject obj)
    {
        if (placingFurniture != null || NativePlacementConfirmed(obj)) return;
        PublishMove(CaptureFurniture(obj), obj);
    }

    private static Move CaptureFurniture(GameObject obj)
    {
        if (!InWorld || obj == null) return null;
        if (obj.GetComponent<GearItem>()?.m_Bed?.m_Bedroll != null || obj.GetComponent<Comps.FakeBedDummy>() != null ||
            obj.GetComponent<Comps.DropFakeOnLeave>() != null || obj.GetComponent<Comps.DroppedGearDummy>() != null) return null;
        var decoration = obj.GetComponent<DecorationItem>();
        if (decoration == null && obj.GetComponent<Placeable>() == null) return null;
        string id = Id(obj);
        if (id.Length == 0) return null;
        var parent = decoration?.DecorationParent;
        string parentId = parent != null ? Id(parent.gameObject) : "";
        var pose = Pose.From(obj.transform);
        string anchor = Anchor(obj);
        var move = new Move(id, parentId, pose, Guid.NewGuid().ToString("N"), 0, 2,
            anchor, Anchor(parent?.gameObject), Visible: CaptureVisibleMeshes(obj));
        if (anchor.Length > 0)
            return move with { Relative = parentId.Length > 0 ? RelativePose(parent.transform, pose) : null };
        var placeable = obj.GetComponent<Placeable>();
        if (placeable == null || IsFixedBed(obj) || !Guid.TryParseExact(placeable.m_Addressable?.AssetGUID, "N", out _)) return move;
        var scale = obj.transform.localScale;
        return move with { Anchor = "", Asset = placeable.m_Addressable.AssetGUID, AssetName = obj.name, Sx = scale.x, Sy = scale.y, Sz = scale.z };
    }

    private static Pose RelativePose(Transform parent, Pose pose)
    {
        var position = parent.InverseTransformPoint(pose.Position);
        var rotation = Quaternion.Inverse(parent.rotation) * pose.Rotation;
        return new Pose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
    }

    private static Pose WorldPose(Transform parent, Pose relative)
    {
        var position = parent.TransformPoint(relative.Position);
        var rotation = parent.rotation * relative.Rotation;
        return new Pose(position.x, position.y, position.z, rotation.x, rotation.y, rotation.z, rotation.w);
    }

    private static bool IsNativeDecorationMove(Move move) => !string.IsNullOrEmpty(move.Anchor) && string.IsNullOrEmpty(move.Asset);

    private static IEnumerable<DecorationItem> AttachedDecorations(DecorationItem parent)
    {
        if (parent == null || parent.DecorationChildren == null) yield break;
        foreach (var child in parent.DecorationChildren)
        {
            if (child == null || child.DecorationParent != parent) continue;
            yield return child;
            foreach (var descendant in AttachedDecorations(child)) yield return descendant;
        }
    }

    private static void PublishRemoval(Move move)
    {
        if (IsNativeDecorationMove(move) && (RemovedFurniture.Contains(move.Id) ||
            Pending.TryGetValue((MyMod.level_guid, move.Id), out var pending) && pending.Move.Removed)) return;
        PublishMove(move with { Removed = true, Parent = "", ParentAnchor = "", Visible = "", Relative = null });
    }

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.OnBreakdown))]
    private static class DecorationRemoved
    {
        private static void Prefix(DecorationItem __instance, out Move __state) => __state = CaptureFurniture(__instance.gameObject);

        private static void Postfix(Move __state, bool __runOriginal)
        {
            if (__runOriginal && __state != null)
                PublishRemoval(__state);
        }
    }

    private static string BreakdownId(BreakDown item)
    {
        var position = item.transform.position;
        string identity = FormattableString.Invariant(
            $"{GameCompat.StripCloneSuffix(item.name)}|{position.x:F2}|{position.y:F2}|{position.z:F2}");
        return "breakdown-" + Shared.StableHash(identity).ToString("X8");
    }

    internal static Action CaptureBreakdown(BreakDown item)
    {
        if (!InWorld || item == null || !item.gameObject.scene.IsValid()) return null;
        var decoration = item.GetComponentInParent<DecorationItem>();
        var move = decoration != null && (decoration.gameObject == item.gameObject || decoration.m_Breakdown == item)
            ? CaptureFurniture(decoration.gameObject) : null;
        if (move == null && string.IsNullOrEmpty(item.GetComponent<ObjectGuid>()?.Get()))
            move = new Move(BreakdownId(item), "", Pose.From(item.transform), Guid.NewGuid().ToString("N"), 0, 2);
        return move == null ? null : () => PublishRemoval(move);
    }

    private static void PublishMove(Move move, GameObject source = null)
    {
        if (move == null) return;
        if (source != null && source == placementOriginObject && move.BaseRevision < 0)
            move = move with { Origin = placementOrigin, BaseRevision = placementRevision };
        if (move.BaseRevision < 0)
            move = move with { BaseRevision = CurrentFurnitureRevision(string.IsNullOrEmpty(move.PreviousId) ? move.Id : move.PreviousId) };
        if (source != null && !move.Removed && !move.Stored && !string.IsNullOrEmpty(move.Asset) && !FurnitureSettled(source))
        {
            SettlingFurniture[move.Id] = (move, source);
            return;
        }
        SendFurnitureMove(move);
    }

    private static bool FurnitureSettled(GameObject source)
    {
        if (source == null || !source.activeInHierarchy) return false;
        var placeable = source.GetComponent<Placeable>();
        return placeable != null && !placeable.m_Invalidated &&
            PlaceableManager.GetPlacementState(placeable) is not (PlacementState.OnPlayer or PlacementState.Removed) &&
            source.GetComponent<DecorationItem>()?.IsCraftedItemPlacementPending() != true;
    }

    private static long CurrentFurnitureRevision(string id) =>
        MyMod.iAmHost && GetScene(MyMod.level_guid).TryGetValue(id, out var saved)
            ? saved.Revision : LatestRevision.GetValueOrDefault(id);

    private static void SendFurnitureMove(Move move)
    {
        if (move?.Pose?.Valid != true || move.Pose.Position == Vector3.zero) return;
        if (MyMod.iAmHost) AcceptMove(MyMod.level_guid, move);
        else
        {
            Pending[(MyMod.level_guid, move.Id)] = (move, Time.realtimeSinceStartup);
            SendToHost(1, MyMod.level_guid, JsonSerializer.Serialize(move));
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.StartPlaceMesh),
        new[] { typeof(GameObject), typeof(float), typeof(PlaceMeshFlags), typeof(PlaceMeshRules) })]
    private static class RememberPlacementOrigin
    {
        private static bool Prefix(GameObject __0, ref bool __result)
        {
            placementOriginObject = null;
            placementOrigin = null;
            if (!InWorld || __0 == null ||
                (__0.GetComponent<Placeable>() == null && __0.GetComponent<DecorationItem>() == null)) return true;
            string id = Id(__0);
            if (!MyMod.iAmHost && (!furnitureReady || Pending.ContainsKey((MyMod.level_guid, id))))
            {
                HUDMessage.AddMessage("Синхронизация расстановки мебели…");
                __result = false;
                return false;
            }
            placementOriginObject = __0;
            placementRevision = CurrentFurnitureRevision(id);
            var placeable = __0.GetComponent<Placeable>();
            if (__0.activeInHierarchy && (OriginalDecoration(__0) != null || placeable != null && !placeable.m_Invalidated &&
                PlaceableManager.GetPlacementState(placeable) is not (PlacementState.OnPlayer or PlacementState.Removed)))
                placementOrigin = Pose.From(__0.transform);
            return true;
        }
    }

    // Native finalization can reset the preview transform. Keep the confirmed preview's world pose.
    [HarmonyPatch(typeof(PlayerManager), "PlaceMeshInWorld")]
    private static class CapturePlacement
    {
        private static void Prefix(PlayerManager __instance, out FurniturePlacement __state)
        {
            __state = null;
            var obj = __instance.m_ObjectToPlaceDecorationItem?.gameObject ?? __instance.m_ObjectToPlace;
            if (!InWorld || obj == null || !CanPlace(obj) ||
                (obj.GetComponent<DecorationItem>() == null && obj.GetComponent<Placeable>() == null)) return;
            if (IsFixedBed(obj))
            {
                var bedMove = CaptureFurniture(obj);
                if (bedMove == null) return;
                __state = new FurniturePlacement { Object = obj, Moves = new[] { bedMove } };
                __state.Objects[bedMove.Id] = obj;
                placingFurniture = __state;
                return;
            }
            var state = new FurniturePlacement { Object = obj };
            var moves = new List<Move>();
            void Capture(GameObject source)
            {
                var move = CaptureFurniture(source);
                if (move != null && state.Objects.TryAdd(move.Id, source)) moves.Add(move);
            }
            Capture(obj);
            foreach (var item in obj.GetComponentsInChildren<DecorationItem>(true))
                if (OriginalDecoration(item.gameObject) == null) Capture(item.gameObject);
            if (__instance.m_DecorationItemChain != null)
                foreach (var item in __instance.m_DecorationItemChain)
                    if (item.Item != null) Capture(item.Item.gameObject);
            foreach (var child in AttachedDecorations(obj.GetComponent<DecorationItem>()))
                if (OriginalDecoration(child.gameObject) != null) Capture(child.gameObject);
            state.Moves = moves.ToArray();
            placingFurniture = __state = state;
        }

        private static void Postfix(PlayerManager __instance, FurniturePlacement __state, bool __runOriginal)
        {
            if (!__runOriginal || __state == null) return;
            if (__state.Moves.Length == 0)
            {
                PublishMove(CaptureFurniture(__state.Object), __state.Object);
                return;
            }
            foreach (var captured in __state.Moves)
            {
                __state.Objects.TryGetValue(captured.Id, out var obj);
                bool root = ReferenceEquals(obj, __state.Object);
                if (root && __state.Replacement != null)
                {
                    obj = __state.Replacement;
                    FurnitureAliases[captured.Id] = obj;
                    string nativeId = obj.GetComponent<Placeable>()?.m_Guid;
                    if (!string.IsNullOrEmpty(nativeId) && nativeId != captured.Id)
                    {
                        NativeFurnitureIds[(MyMod.level_guid, captured.Id)] = nativeId;
                        bindingsChanged = true;
                    }
                }
                if (obj == null) obj = Find(captured.Id, captured.Anchor);
                var parentObject = obj != null ? obj.GetComponent<DecorationItem>()?.DecorationParent?.gameObject : null;
                string parent = Id(parentObject);
                var parentAnchor = Anchor(parentObject);
                bool native = IsNativeDecorationMove(captured);
                var pose = root || native || obj == null || obj.transform.position == Vector3.zero ? captured.Pose : Pose.From(obj.transform);
                var current = obj != null ? CaptureFurniture(obj) : null;
                var confirmed = (current ?? captured) with { Parent = parent, ParentAnchor = parentAnchor, Pose = pose,
                    Origin = root && __state.Object == placementOriginObject ? placementOrigin : null,
                    BaseRevision = root && __state.Object == placementOriginObject ? placementRevision : -1,
                    PreviousId = current != null && current.Id != captured.Id ? captured.Id : "" };
                if (native)
                {
                    if (nativeConfirmationFrame != Time.frameCount)
                    {
                        nativeConfirmationFrame = Time.frameCount;
                        ConfirmedNativeDecorations.Clear();
                    }
                    if (obj != null) ConfirmedNativeDecorations.Add(obj.GetInstanceID());
                    // The native stacking link can be cleared while finalizing the placement chain.
                    if (!root && parent.Length == 0 && __state.Objects.ContainsKey(captured.Parent))
                    {
                        parent = captured.Parent;
                        parentAnchor = captured.ParentAnchor;
                    }
                    var relative = parent.Length == 0 ? null :
                        captured.Relative != null && (parent == captured.Parent ||
                            parentAnchor.Length > 0 && parentAnchor == captured.ParentAnchor) ? captured.Relative :
                        parentObject != null ? RelativePose(parentObject.transform, pose) : null;
                    confirmed = confirmed with { Id = captured.Id, Anchor = captured.Anchor, Asset = "", PreviousId = "",
                        Parent = parent, ParentAnchor = parentAnchor, Relative = relative };
                }
                PublishMove(confirmed, obj);
            }
        }

        private static void Finalizer(FurniturePlacement __state)
        {
            if (ReferenceEquals(placingFurniture, __state)) placingFurniture = null;
        }
    }

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.FinalizePlacementRecursive))]
    private static class DecorationPlaced
    {
        private static void Prefix(DecorationItem __instance, bool __0, out Move __state)
        {
            __state = __0 && placingFurniture == null && !NativePlacementConfirmed(__instance.gameObject)
                ? CaptureFurniture(__instance.gameObject) : null;
        }

        private static void Postfix(DecorationItem __instance, bool __0, Move __state)
        {
            if (placingFurniture != null) return;
            if (__0 && __state != null)
            {
                var current = CaptureFurniture(__instance.gameObject);
                PublishMove(current != null && current.Pose.Position != Vector3.zero
                    ? current with { PreviousId = current.Id != __state.Id ? __state.Id : "" } : __state, __instance.gameObject);
            }
        }
    }

    [HarmonyPatch(typeof(PlaceableManager), nameof(PlaceableManager.ReturnFromPlayer), new[] { typeof(Placeable) })]
    private static class ReturnCraftedFurniture
    {
        private static void Prefix(Placeable __0, out string __state) =>
            __state = InWorld && !creatingReplica && placingFurniture == null && __0 != null ? Id(__0.gameObject) : "";

        private static void Postfix(Placeable __0, string __state)
        {
            if (!InWorld || creatingReplica || PlaceableManager.s_IsDeserializing || __0 == null) return;
            if (placingFurniture != null)
            {
                var root = placingFurniture.Moves.FirstOrDefault(move => placingFurniture.Objects.TryGetValue(move.Id, out var original) &&
                    ReferenceEquals(original, placingFurniture.Object));
                if (root != null && !string.IsNullOrEmpty(root.Asset) &&
                    __0.m_Addressable?.AssetGUID == root.Asset && (__0.transform.position - root.Pose.Position).sqrMagnitude < .04f)
                    placingFurniture.Replacement = __0.gameObject;
                return;
            }
            var move = CaptureFurniture(__0.gameObject);
            if (move != null && !string.IsNullOrEmpty(move.Asset))
                PublishMove(move with { PreviousId = __state != move.Id ? __state : "" }, __0.gameObject);
        }
    }

    [HarmonyPatch(typeof(Inventory), nameof(Inventory.AddDecoration))]
    private static class PickUpPlacedFurniture
    {
        private static void Prefix(DecorationItem __0, out Move __state)
        {
            __state = null;
            if (!InWorld || creatingReplica || PlaceableManager.s_IsDeserializing || placingFurniture != null ||
                __0 == null || !__0.gameObject.activeInHierarchy || __0.GetComponent<Placeable>() is not { } placeable ||
                PlaceableManager.GetPlacementState(placeable) is PlacementState.OnPlayer or PlacementState.Removed) return;
            var move = CaptureFurniture(__0.gameObject);
            if (move?.Asset.Length > 0) __state = move;
        }

        private static void Postfix(Inventory __instance, DecorationItem __0, Move __state)
        {
            if (__state == null || __0 == null || !__instance.DecorationInInventory(__0)) return;
            SettlingFurniture.Remove(__state.Id);
            PublishMove(__state with { Stored = true, Parent = "", ParentAnchor = "", Visible = "" });
        }
    }

    private static Dictionary<string, Move> GetScene(string area)
    {
        int session = Server.Generation;
        string world = WorldSaveIdentity.Active;
        int seed = MPSaveManager.GetSeed();
        if (session != serverSession || furnitureWorld != world || furnitureSeed != seed)
        {
            serverSession = session;
            furnitureWorld = world;
            furnitureSeed = seed;
            Saved.Clear();
            StoredFurniture.Clear();
            AcceptedOperations.Clear();
            OperationOrder.Clear();
        }
        if (Saved.TryGetValue(area, out var moves)) return moves;
        string data = MPSaveManager.LoadData(area + "_FurniturePositions", seed);
        try { moves = data == null ? null : JsonSerializer.Deserialize<Dictionary<string, Move>>(data, FurnitureSaveOptions); }
        catch (JsonException) { moves = null; }
        moves ??= new Dictionary<string, Move>(StringComparer.Ordinal);
        int invalid = 0, registrations = 0;
        foreach (var entry in moves.ToArray())
        {
            var move = entry.Value;
            if (move?.RegisterOnly == true)
            {
                moves.Remove(entry.Key);
                registrations++;
            }
            else if (!HasFurniturePose(move))
            {
                moves.Remove(entry.Key);
                invalid++;
            }
        }
        if (invalid > 0) MelonLoader.MelonLogger.Warning("[Furniture] Ignored " + invalid + " invalid legacy placements in " + area);
        if (registrations > 0) MelonLoader.MelonLogger.Msg("[Furniture] Ignored " + registrations + " automatic registrations in " + area);
        foreach (var move in moves.Values) revision = Math.Max(revision, move.Revision);
        Saved.Add(area, moves);
        StoredFurniture.Add(area, FurnitureContainerIndex.Load(MPSaveManager.GetPathForName("", seed), area));
        var retired = RetireLegacyCrockeryCopies(moves, moves.Values.Where(move => move.Removed).ToArray());
        if (retired.Count > 0)
        {
            MPSaveManager.ValidateRootExits();
            if (!MPSaveManager.SaveData(area + "_FurniturePositions", JsonSerializer.Serialize(moves), seed))
                foreach (var original in retired) moves[original.Id] = original;
            else MelonLoader.MelonLogger.Msg($"[Furniture] Retired {retired.Count} legacy crockery copies in {area}");
        }
        ReconcileContainerFurniture(area, notify: false);
        return moves;
    }

    internal static bool PrepareContainerWrite(string area)
    {
        GetScene(area);
        return ReconcileContainerFurniture(area, notify: true);
    }

    internal static bool ContainerWritten(string area, string container, string compressed)
    {
        GetScene(area);
        StoredFurniture[area].Record(container, compressed);
        return ReconcileContainerFurniture(area, notify: true);
    }

    private static bool ReconcileContainerFurniture(string area, bool notify)
    {
        var moves = Saved[area];
        var index = StoredFurniture[area];
        var changed = new List<Move>();
        long nextRevision = revision;
        foreach (var move in moves.Values)
        {
            if (move.Removed || move.Stored || string.IsNullOrEmpty(move.Asset)) continue;
            if (index.Owner(move.Id) == null &&
                (!NativeFurnitureIds.TryGetValue((area, move.Id), out string nativeId) || index.Owner(nativeId) == null)) continue;
            changed.Add(move with { Stored = true, Parent = "", ParentAnchor = "", Visible = "",
                Operation = Guid.NewGuid().ToString("N"), Revision = ++nextRevision });
        }
        if (changed.Count == 0) return true;
        var next = new Dictionary<string, Move>(moves, StringComparer.Ordinal);
        foreach (var move in changed) next[move.Id] = move;
        MPSaveManager.ValidateRootExits();
        if (!MPSaveManager.SaveData(area + "_FurniturePositions", JsonSerializer.Serialize(next), MPSaveManager.GetSeed()))
            return false;
        foreach (var move in changed) moves[move.Id] = move;
        revision = nextRevision;
        if (notify)
            foreach (var move in changed)
            {
                ReceiveMove(area, move);
                SendToPeers(1, area, JsonSerializer.Serialize(move));
            }
        MelonLoader.MelonLogger.Msg($"[Furniture] Matched {changed.Count} stored furniture identities in {area}");
        return true;
    }

    private static void AcceptMove(string area, Move move, int sender = 0)
    {
        if (!HasFurniturePose(move) || string.IsNullOrEmpty(move.Id) || move.Id.Length > 100 ||
            (move.Anchor?.Length ?? 0) > 8 || (move.ParentAnchor?.Length ?? 0) > 8 ||
            move.Parent == null || move.Parent.Length > 100 || move.Id == move.Parent || move.Version != 2 ||
            (move.Visible?.Length ?? 0) > 192 || (move.PreviousId?.Length ?? 0) > 100 ||
            !Guid.TryParseExact(move.Operation, "N", out _) || !ValidFurnitureAsset(move) || !ValidAttachment(move) ||
            (move.Stored && (move.Removed || string.IsNullOrEmpty(move.Asset)))) return;
        var moves = GetScene(area);
        if (!move.Stored && !move.Removed && !string.IsNullOrEmpty(move.Asset) &&
            (StoredFurniture[area].Owner(move.Id) != null ||
                NativeFurnitureIds.TryGetValue((area, move.Id), out string nativeId) && StoredFurniture[area].Owner(nativeId) != null))
        {
            AckMove(sender, area, move.Operation);
            return;
        }
        string ancestor = move.Parent;
        for (int depth = 0; ancestor.Length > 0; depth++)
        {
            if (ancestor == move.Id || depth > moves.Count) return;
            if (!moves.TryGetValue(ancestor, out var parent)) break;
            ancestor = parent.Parent ?? "";
        }
        if (!move.RegisterOnly && !AcceptedOperations.Contains(move.Operation))
        {
            if (moves.Count >= 2048 && !moves.ContainsKey(move.Id)) return;
            if (!move.Removed && moves.Values.Any(previous => previous.Removed &&
                (previous.Id == move.Id || !string.IsNullOrEmpty(move.Anchor) && previous.Anchor == move.Anchor)))
            {
                AckMove(sender, area, move.Operation);
                return;
            }
            moves.TryGetValue(move.Id, out var existing);
            if (existing != null && existing with { Operation = move.Operation, Revision = move.Revision, History = null } == move with { History = null })
            {
                AckMove(sender, area, move.Operation);
                return;
            }
            Move previous = existing;
            if (previous == null && !string.IsNullOrEmpty(move.PreviousId))
                moves.TryGetValue(move.PreviousId, out previous);
            bool destroysSameItem = move.Removed && existing != null && !existing.Removed &&
                move.BaseRevision >= 0 && move.BaseRevision < existing.Revision &&
                move.Anchor == existing.Anchor && move.Asset == existing.Asset;
            if (!destroysSameItem && !FurnitureReplicationPolicy.CanCommit(move.BaseRevision, previous?.Revision ?? 0))
            {
                MelonLoader.MelonLogger.Msg($"[Furniture] Rejected stale move scene={area} id={move.Id} player={sender} expected={move.BaseRevision} actual={previous?.Revision ?? 0}");
                if (previous != null)
                {
                    if (sender == 0)
                    {
                        Unapplied[previous.Id] = previous;
                        furnitureRetryDelay = 1f;
                        nextFurnitureApply = 0f;
                    }
                    else
                    {
                        using var correction = MakeServerPacket(21, area, JsonSerializer.Serialize(previous), 0, 0);
                        ServerSend.SendUDPData(sender, correction);
                    }
                }
                AckMove(sender, area, move.Operation);
                return;
            }
            move = move with { Revision = revision + 1, History = FurnitureHistory(move, previous), Origin = null };
            var next = new Dictionary<string, Move>(moves, StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(move.Anchor))
                foreach (var old in moves.Values.Where(item => item.Id != move.Id && item.Anchor == move.Anchor))
                    next.Remove(old.Id);
            Move retired = null;
            if (move.PreviousId is { Length: > 0 } prior && prior != move.Id && moves.TryGetValue(prior, out var oldMove) &&
                !oldMove.Removed && oldMove.Asset == move.Asset)
            {
                retired = oldMove with { Removed = true, Revision = move.Revision, Parent = "", ParentAnchor = "", Visible = "", PreviousId = "" };
                next[prior] = retired;
                foreach (var child in next.Values.Where(item => item.Parent == prior).ToArray())
                    next[child.Id] = child with { Parent = move.Id, Revision = move.Revision };
            }
            next[move.Id] = move;
            var retiredCrockery = move.Removed ? RetireLegacyCrockeryCopies(next, new[] { move }) : null;
            MPSaveManager.ValidateRootExits();
            if (!MPSaveManager.SaveData(area + "_FurniturePositions", JsonSerializer.Serialize(next), MPSaveManager.GetSeed())) return;
            Saved[area] = next;
            revision = move.Revision;
            if (OperationOrder.Count >= 8192) AcceptedOperations.Remove(OperationOrder.Dequeue());
            AcceptedOperations.Add(move.Operation);
            OperationOrder.Enqueue(move.Operation);
            MelonLoader.MelonLogger.Msg("[Furniture] Saved move " + move.Id + " in " + area + " revision=" + move.Revision);
            if (retired != null)
            {
                if (move.AssetName?.Contains("CuringBox", StringComparison.OrdinalIgnoreCase) == true)
                    CuringStorageSync.MoveIdentity(area, retired.Id, move.Id);
                ReceiveMove(area, retired);
                SendToPeers(1, area, JsonSerializer.Serialize(retired));
                foreach (var child in next.Values.Where(item => item.Parent == move.Id && item.Revision == move.Revision))
                {
                    ReceiveMove(area, child);
                    SendToPeers(1, area, JsonSerializer.Serialize(child));
                }
            }
            ReceiveMove(area, move);
            SendToPeers(1, area, JsonSerializer.Serialize(move));
            if (retiredCrockery != null)
                foreach (var original in retiredCrockery)
                {
                    ReceiveMove(area, next[original.Id]);
                    SendToPeers(1, area, JsonSerializer.Serialize(next[original.Id]));
                }
        }
        AckMove(sender, area, move.Operation);
    }

    private static void AckMove(int sender, string area, string operation)
    {
        if (sender <= 0) return;
        using var ack = MakeServerPacket(3, area, operation, 0, 0);
        ServerSend.SendUDPData(sender, ack);
    }

    private static void ReceiveMove(string area, Move move)
    {
        if (area != MyMod.level_guid || !HasFurniturePose(move) || move.RegisterOnly || string.IsNullOrEmpty(move.Id) ||
            (move.History?.Length ?? 0) > FurnitureHistoryLimit || !ValidAttachment(move)) return;
        if (LatestRevision.TryGetValue(move.Id, out long latest) && latest >= move.Revision) return;
        LatestRevision[move.Id] = move.Revision;
        if (move.Removed) RemovedFurniture.Add(move.Id);
        else RemovedFurniture.Remove(move.Id);
        Unapplied[move.Id] = move;
        furnitureRetryDelay = 1f;
        nextFurnitureApply = 0f;
    }

    private static bool HasFurniturePose(Move move) => move?.Pose is { Valid: true } pose &&
        (pose.X != 0f || pose.Y != 0f || pose.Z != 0f) &&
        (pose.Qx != 0f || pose.Qy != 0f || pose.Qz != 0f || pose.Qw != 0f);

    private static bool ValidFurnitureAsset(Move move) => string.IsNullOrEmpty(move.Asset) ||
        (Guid.TryParseExact(move.Asset, "N", out _) && move.AssetName != null && move.AssetName.Length <= 128 &&
         float.IsFinite(move.Sx) && float.IsFinite(move.Sy) && float.IsFinite(move.Sz) &&
         Mathf.Abs(move.Sx) <= 20f && Mathf.Abs(move.Sy) <= 20f && Mathf.Abs(move.Sz) <= 20f);

    private static bool ValidAttachment(Move move) => move.Relative == null ||
        (IsNativeDecorationMove(move) && !move.Removed && !string.IsNullOrEmpty(move.Parent) &&
         move.Relative.Valid && Quaternion.Dot(move.Relative.Rotation, move.Relative.Rotation) > .99f &&
         Quaternion.Dot(move.Relative.Rotation, move.Relative.Rotation) < 1.01f);

    private static bool SameFurniturePose(Pose left, Pose right) =>
        left?.Valid == true && right?.Valid == true &&
        (left.Position - right.Position).sqrMagnitude <= .0001f &&
        Mathf.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) >= .9999f;

    private static Pose[] FurnitureHistory(Move move, Move previous)
    {
        if (string.IsNullOrEmpty(move.Asset)) return null;
        if (previous == null)
            return move.Origin?.Valid == true && !SameFurniturePose(move.Origin, move.Pose) ? new[] { move.Origin } : null;
        if (previous.Removed || previous.Asset != move.Asset) return null;
        var poses = new List<Pose>();
        foreach (var pose in (previous.History ?? Array.Empty<Pose>()).Prepend(previous.Pose))
            if (pose?.Valid == true && !SameFurniturePose(pose, move.Pose) && !poses.Any(known => SameFurniturePose(known, pose)))
                poses.Add(pose);
        // Preserve the initial position as well as the most recent positions for returning players.
        return poses.Count <= FurnitureHistoryLimit ? poses.ToArray() :
            poses.Take(FurnitureHistoryLimit - 1).Append(poses[^1]).ToArray();
    }

    private static bool IsLegacyCrockery(Move move) => string.IsNullOrEmpty(move.Anchor) &&
        !string.IsNullOrEmpty(move.Asset) && move.AssetName != null &&
        (move.AssetName.StartsWith("OBJ_Dish", StringComparison.Ordinal) || move.AssetName.StartsWith("OBJ_Cup", StringComparison.Ordinal));

    private static bool SameCrockeryPose(Pose left, Pose right) => left?.Valid == true && right?.Valid == true &&
        (left.Position - right.Position).sqrMagnitude <= .000004f &&
        Mathf.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) >= .999999f;

    private static List<Move> RetireLegacyCrockeryCopies(Dictionary<string, Move> moves, IEnumerable<Move> removals)
    {
        var retired = new List<Move>();
        var byAsset = moves.Values.Where(move => IsLegacyCrockery(move) && !move.Stored && !move.Removed &&
            !string.IsNullOrEmpty(move.Parent) && move.History?.Length > 0).ToLookup(move => move.Asset);
        foreach (var removal in removals.Where(IsLegacyCrockery).OrderByDescending(move => move.Revision))
        {
            // Old versions assigned a new ID on every move of the same shelf. A current overlap alone
            // is ambiguous. Require two copies on the same shelf with the same prior and current poses.
            var matches = byAsset[removal.Asset].Where(move => move.Revision < removal.Revision &&
                !moves[move.Id].Removed && SameCrockeryPose(move.Pose, removal.Pose)).ToArray();
            foreach (var match in matches)
            {
                if (!matches.Any(other => other.Id != match.Id && other.Parent == match.Parent &&
                    match.History.Any(pose => other.History.Any(prior => SameCrockeryPose(pose, prior))))) continue;
                retired.Add(match);
                moves[match.Id] = match with { Removed = true, Revision = removal.Revision, Parent = "",
                    ParentAnchor = "", Visible = "", Relative = null };
            }
        }
        return retired;
    }

    private static int QueuedParentDepth(Move move)
    {
        int depth = 0;
        string parent = move.Parent;
        while (!string.IsNullOrEmpty(parent) && depth < Unapplied.Count && Unapplied.TryGetValue(parent, out var next))
        {
            depth++;
            parent = next.Parent;
        }
        return depth;
    }

    private static bool Apply(Move move, PlacementLookup lookup)
    {
        if (PlaceableManager.s_IsDeserializing) return false;
        if (!string.IsNullOrEmpty(move.Asset) && !ValidFurnitureAsset(move)) return true;
        var obj = Find(move.Id, move.Anchor, lookup);
        if (IsNativeDecorationMove(move) && (obj == null || Anchor(obj) != move.Anchor)) return false;
        if (!move.Stored && obj == null && !string.IsNullOrEmpty(move.Asset))
        {
            // Before anchors were retained, a move could only identify its scene original by GUID or pose history.
            var originals = lookup.ForAsset(move.Asset).Where(candidate => candidate != null &&
                OriginalDecoration(candidate.gameObject) is { } original && IsFurnitureMatch(move, original.Origin) &&
                !LatestRevision.ContainsKey(original.Id) && !FurnitureAliases.Values.Any(alias => alias == candidate.gameObject))
                .Take(2).ToArray();
            if (originals.Length > 1) return false;
            if (originals.Length == 1) obj = originals[0].gameObject;
        }
        var originalDecoration = OriginalDecoration(obj);
        if (originalDecoration != null && originalDecoration.Id != move.Id)
            FurnitureAliases[move.Id] = obj;
        if (move.Removed || move.Stored)
        {
            if (move.Stored && obj == null)
            {
                var candidates = lookup.ForAsset(move.Asset).Where(candidate => candidate != null &&
                    candidate.gameObject.activeInHierarchy && !candidate.m_Invalidated &&
                    !LatestRevision.ContainsKey(candidate.m_Guid ?? "") &&
                    !FurnitureAliases.Values.Any(alias => alias == candidate.gameObject) &&
                    IsFurnitureMatch(move, Pose.From(candidate.transform))).Take(2).ToArray();
                if (candidates.Length == 1)
                {
                    obj = candidates[0].gameObject;
                    FurnitureAliases[move.Id] = obj;
                    if (!string.IsNullOrEmpty(candidates[0].m_Guid))
                    {
                        NativeFurnitureIds[(MyMod.level_guid, move.Id)] = candidates[0].m_Guid;
                        bindingsChanged = true;
                    }
                }
            }
            if (obj == null) return false;
            if (move.Stored && (!obj.activeInHierarchy ||
                obj.GetComponent<Placeable>() is { } hidden && PlaceableManager.GetPlacementState(hidden) == PlacementState.OnPlayer))
                return true;
            var activePlayer = GameManager.GetPlayerManagerComponent();
            if (activePlayer != null && (activePlayer.m_ObjectToPlace == obj || activePlayer.m_ObjectToPlaceDecorationItem?.gameObject == obj)) return false;
            var removedDecoration = obj.GetComponent<DecorationItem>();
            if (removedDecoration != null && obj.activeSelf)
            {
                BoxSectorManager.s_Instance?.RemoveDecorationFromSystem(removedDecoration);
                removedDecoration.RemoveFromHierarchy();
                furnitureSectorsDirty = true;
            }
            if (move.Removed && obj.GetComponent<Placeable>() is { } removedPlacement)
                removedPlacement.RemoveIfValid();
            if (obj != null) obj.SetActive(false);
            return true;
        }
        if (obj == null && !string.IsNullOrEmpty(move.Asset))
        {
            var unbound = lookup.ForAsset(move.Asset).Where(candidate =>
                candidate != null && candidate.gameObject.activeInHierarchy && !candidate.m_Invalidated &&
                PlaceableManager.GetPlacementState(candidate) is not (PlacementState.Removed or PlacementState.OnPlayer) &&
                !LatestRevision.ContainsKey(candidate.m_Guid ?? "") &&
                !FurnitureAliases.Values.Any(item => item == candidate.gameObject) &&
                !NativeFurnitureIds.Any(item => item.Key.Area == MyMod.level_guid && item.Value == candidate.m_Guid)).ToArray();
            var matches = unbound.Where(candidate => IsFurnitureMatch(move, Pose.From(candidate.transform))).Take(2).ToArray();
            if (matches.Length == 1)
            {
                obj = matches[0].gameObject;
                FurnitureAliases[move.Id] = obj;
                if (!string.IsNullOrEmpty(matches[0].m_Guid))
                {
                    NativeFurnitureIds[(MyMod.level_guid, move.Id)] = matches[0].m_Guid;
                    bindingsChanged = true;
                }
                MelonLoader.MelonLogger.Msg($"[Furniture] Reused {obj.name} native={matches[0].m_Guid} saved={move.Id}");
            }
            else if (!FurnitureReplicationPolicy.CanCreate(matches.Length, move.History?.Length > 0, unbound.Length))
            {
                if (FurnitureApplyWarnings.Add(move.Id))
                    MelonLoader.MelonLogger.Warning($"[Furniture] Deferred replica scene={MyMod.level_guid} id={move.Id} asset={move.AssetName} matches={matches.Length} candidates={unbound.Length}");
                return false;
            }
        }
        if (obj == null && !string.IsNullOrEmpty(move.Asset) && ValidFurnitureAsset(move))
        {
            var saved = new PlaceableSaveData
            {
                m_Addressable = new AssetReferencePlaceable(move.Asset), m_Name = move.AssetName,
                m_Position = move.Pose.Position, m_Rotation = move.Pose.Rotation, m_ActiveSelf = true,
                m_Scale = new Vector3(move.Sx, move.Sy, move.Sz)
            };
            creatingReplica = true;
            try
            {
                var replica = Placeable.FindOrCreateAndDeserialize(move.Id, saved);
                if (replica != null)
                {
                    lookup.Register(replica);
                    obj = replica.gameObject;
                    FurnitureAliases[move.Id] = obj;
                }
            }
            catch (Exception ex)
            {
                if (FurnitureApplyWarnings.Add(move.Id))
                    MelonLoader.MelonLogger.Warning("[Furniture] Could not create " + move.AssetName + ": " + ex.Message);
                FurnitureCreateAttempts.TryGetValue(move.Id, out int attempts);
                FurnitureCreateAttempts[move.Id] = ++attempts;
                return attempts >= 5;
            }
            finally { creatingReplica = false; }
        }
        if (obj == null) return false;
        var placeable = obj.GetComponent<Placeable>();
        if (placeable != null && (placeable.m_Invalidated ||
            PlaceableManager.GetPlacementState(placeable) is PlacementState.Removed or PlacementState.OnPlayer)) return false;
        bool fixedBed = IsFixedBed(obj);
        if (fixedBed && BedRest.IsOccupied(obj.GetComponentInChildren<Bed>(true))) return false;
        var player = GameManager.GetPlayerManagerComponent();
        if (player != null && (player.m_ObjectToPlace == obj || player.m_ObjectToPlaceDecorationItem?.gameObject == obj)) return false;
        var decoration = obj.GetComponent<DecorationItem>();
        if (decoration == null && placeable == null) return true;
        if (!obj.activeSelf && placeable != null && !string.IsNullOrEmpty(move.Asset)) obj.SetActive(true);
        GameObject parent = null;
        bool awaitingParent = false;
        if (decoration != null && !fixedBed)
        {
            parent = Find(move.Parent, move.ParentAnchor, lookup);
            awaitingParent = move.Parent.Length > 0 && (parent == null || !parent.activeInHierarchy);
            if (awaitingParent && originalDecoration != null)
            {
                if (!RemovedFurniture.Contains(move.Parent)) return false;
                // A legacy attachment can refer to a shelf that has since been destroyed.
                parent = null;
                awaitingParent = false;
            }
        }
        var sectors = BoxSectorManager.s_Instance;
        var parentDecoration = parent != null ? parent.GetComponent<DecorationItem>() : null;
        var pose = move.Relative?.Valid == true && parentDecoration != null && !awaitingParent
            ? WorldPose(parent.transform, move.Relative) : move.Pose;
        bool changed = (obj.transform.position - pose.Position).sqrMagnitude > .000001f ||
            Mathf.Abs(Quaternion.Dot(obj.transform.rotation, pose.Rotation)) < .99999f;
        bool changeParent = decoration != null && !fixedBed &&
            (awaitingParent ? decoration.DecorationParent != null && !decoration.DecorationParent.gameObject.activeInHierarchy :
                parentDecoration != decoration.DecorationParent && (parent == null || !parent.transform.IsChildOf(obj.transform)));
        bool repairSectors = changed || changeParent || (!awaitingParent &&
            !obj.GetComponentsInChildren<Renderer>(true).Any(VisibleRenderer));
        var carried = changed && !awaitingParent ? AttachedDecorations(decoration)
            .Where(child => child.gameObject.activeInHierarchy && OriginalDecoration(child.gameObject) != null &&
                (player == null || player.m_ObjectToPlace != child.gameObject && player.m_ObjectToPlaceDecorationItem != child))
            .Select(child => (Item: child, Relative: RelativePose(obj.transform, Pose.From(child.transform)))).ToArray()
            : Array.Empty<(DecorationItem Item, Pose Relative)>();
        if (repairSectors && sectors != null && decoration != null)
        {
            sectors.RemoveDecorationFromSystem(decoration);
            foreach (var child in carried) sectors.RemoveDecorationFromSystem(child.Item);
        }
        if (changeParent) decoration.SetParent(awaitingParent ? null : parentDecoration);
        obj.transform.SetPositionAndRotation(pose.Position, pose.Rotation);
        // DecorationParent is a stacking link. These children can have independent Unity transforms.
        foreach (var child in carried)
        {
            var childPose = WorldPose(obj.transform, child.Relative);
            child.Item.transform.SetPositionAndRotation(childPose.Position, childPose.Rotation);
        }
        if (!string.IsNullOrEmpty(move.Asset)) FurnitureAliases[move.Id] = obj;
        if (repairSectors && sectors != null && decoration != null)
        {
            sectors.MaybeAddDecorationIntoSystem(decoration);
            foreach (var child in carried) sectors.MaybeAddDecorationIntoSystem(child.Item);
            furnitureSectorsDirty = true;
        }
        int restored = RestoreFurnitureMeshes(obj, move.Visible);
        if (restored > 0) MelonLoader.MelonLogger.Msg($"[Furniture] Restored {restored} mesh renderers for {obj.name} id={move.Id}");
        return !awaitingParent;
    }

    private static bool IsFurnitureMatch(Move move, Pose current) =>
        SameFurniturePose(move.Pose, current) || move.History?.Any(prior => SameFurniturePose(prior, current)) == true;

    internal static void ProbeFurnitureContainer(Action<bool, string> check)
    {
        string area = MyMod.level_guid;
        var inventory = GameManager.GetInventoryComponent();
        var box = UnityEngine.Object.FindObjectsOfType<Container>().FirstOrDefault(item =>
            item != null && item.gameObject.activeInHierarchy && item.m_DecorationItems?.Count == 0);
        if (box == null) throw new InvalidOperationException("Furniture fixture requires a container.");
        string boxId = ContainerLootSync.EnsureId(box);
        string id = Guid.NewGuid().ToString();
        var pose = Pose.From(box.transform);
        var floor = new Move(id, "", pose with { X = pose.X + 1f }, Guid.NewGuid().ToString("N"), 0, 2,
            Asset: "1d1568f932e23904cbbb9b6dfca46d06", AssetName: "INTERACTIVE_CuringBox (PLACED)", BaseRevision: 0);
        AcceptMove(area, floor);
        floor = GetScene(area)[id];
        check(Apply(floor, new PlacementLookup()), "furniture journal materializes the crafted box");
        var spawned = Find(id);
        check(spawned != null && spawned.activeInHierarchy, "one visible crafted box before pickup");

        var decoration = spawned.GetComponent<DecorationItem>();
        inventory.AddDecoration(decoration);
        check(inventory.DecorationInInventory(decoration) && GetScene(area)[id].Stored,
            "native pickup marks the furniture identity as stored");
        inventory.RemoveDecoration(decoration);
        box.AddDecorationItem(decoration);
        box.m_Inspected = true;
        string payload = ContainerLootSync.SerializeContents(box);
        check(MPSaveManager.SaveContainer(area, boxId, Shared.CompressString(payload)) &&
            StoredFurniture[area].Owner(id) == boxId, "container commit claims the same furniture ID");
        var stored = GetScene(area)[id];
        AcceptMove(area, floor with { Operation = Guid.NewGuid().ToString("N"), BaseRevision = stored.Revision });
        check(GetScene(area)[id].Stored && !spawned.activeInHierarchy,
            "a placed replay cannot override the container claim");
        var legacy = new Dictionary<string, Move>(GetScene(area)) { [id] = floor with { Revision = stored.Revision } };
        check(MPSaveManager.SaveData(area + "_FurniturePositions", JsonSerializer.Serialize(legacy), MPSaveManager.GetSeed()),
            "legacy duplicate fixture persisted");
        Saved.Remove(area);
        StoredFurniture.Remove(area);
        check(GetScene(area)[id].Stored, "scene reload reconciles an older floor record against the stored identity");
        ContainerLootSync.RestoreContents(box, payload);
        ContainerLootSync.RestoreContents(box, payload);
        check(box.m_DecorationItems.Count == 1, "reopening a container twice keeps one furniture item");

        var withdrawn = box.m_DecorationItems[0];
        box.RemoveDecorationItem(withdrawn);
        check(MPSaveManager.SaveContainer(area, boxId, "-") && StoredFurniture[area].Owner(id) == null,
            "container withdrawal releases the claim");
        ContainerLootSync.RestoreContents(box, "");
        check(box.m_DecorationItems.Count == 0, "an empty container snapshot cannot resurrect withdrawn furniture");
        var moved = floor with { Operation = Guid.NewGuid().ToString("N"), BaseRevision = GetScene(area)[id].Revision,
            Pose = floor.Pose with { X = floor.Pose.X + 1f } };
        AcceptMove(area, moved);
        check(!GetScene(area)[id].Stored && SameFurniturePose(GetScene(area)[id].Pose, moved.Pose),
            "the same identity can be placed after withdrawing it");
    }

    internal static bool ProbeFurniture()
    {
        var lookup = new PlacementLookup();
        foreach (var placeable in UnityEngine.Object.FindObjectsOfType<Placeable>())
        {
            if (placeable == null || placeable.m_Invalidated || string.IsNullOrEmpty(placeable.m_Guid)) continue;
            var obj = placeable.gameObject;
            var move = CaptureFurniture(obj);
            if (move == null || !string.IsNullOrEmpty(move.Parent) || IsFixedBed(obj)) continue;
            return lookup.Find(placeable.m_Guid) == placeable &&
                lookup.Find(Guid.NewGuid().ToString("N")) == null &&
                Find(move.Id, move.Anchor, lookup) == obj && Apply(move, lookup);
        }
        return false;
    }

    internal static void ProbeCrockeryRemoval(Action<bool, string> check)
    {
        string area = MyMod.level_guid;
        const string bowl = "4602b9daf60145a499af4a844f128f38";
        var pose = Pose.From(GameManager.GetPlayerTransform()) with { Y = GameManager.GetPlayerTransform().position.y + 1f };
        var prior = pose with { X = pose.X + 1f };
        var parent = Guid.NewGuid().ToString("D");
        Move NewMove(Pose at) => new(Guid.NewGuid().ToString("D"), "", at, Guid.NewGuid().ToString("N"), 0, 2,
            Asset: bowl, AssetName: "OBJ_DishBowlA2_Prefab (PLACED)", BaseRevision: 0);

        var source = NewMove(pose);
        AcceptMove(area, source);
        var before = GetScene(area)[source.Id];
        check(Apply(before, new PlacementLookup()), "native bowl is materialized from the scene journal");
        var bowlObject = Find(before.Id);
        var breakdown = bowlObject?.GetComponentInChildren<BreakDown>();
        check(breakdown != null, "bowl has the native breakdown interaction");

        var panel = GameCompat.Panel<Panel_BreakDown>();
        panel.m_BreakDown = breakdown;
        panel.Enable(true);
        panel.OnBreakDown();
        check(bowlObject.activeInHierarchy && !panel.IsBreakingDown(), "opening press cannot confirm the breakdown panel");
        panel.Enable(false);

        var move = before with { Operation = Guid.NewGuid().ToString("N"), BaseRevision = before.Revision,
            Pose = pose with { X = pose.X - .2f } };
        AcceptMove(area, move);
        var moved = GetScene(area)[source.Id];
        check(moved.Revision > before.Revision && Apply(moved, new PlacementLookup()), "bowl can move before destruction");
        breakdown.DoBreakDown(false);
        check(GetScene(area)[source.Id].Removed, "native breakdown records permanent removal");

        var oldCopies = new Dictionary<string, Move>(GetScene(area), StringComparer.Ordinal);
        var removal = GetScene(area)[source.Id];
        var first = NewMove(removal.Pose) with { Parent = parent, History = new[] { prior }, Revision = removal.Revision - 1 };
        var second = first with { Id = Guid.NewGuid().ToString("D") };
        var neighbor = NewMove(removal.Pose with { X = removal.Pose.X + .08f }) with
            { Parent = parent, History = new[] { prior }, Revision = removal.Revision - 1 };
        var singleOverlap = NewMove(removal.Pose) with { Parent = parent,
            History = new[] { prior with { X = prior.X + .3f } }, Revision = removal.Revision - 1 };
        foreach (var moveRecord in new[] { first, second, neighbor, singleOverlap }) oldCopies[moveRecord.Id] = moveRecord;
        check(MPSaveManager.SaveData(area + "_FurniturePositions", JsonSerializer.Serialize(oldCopies), MPSaveManager.GetSeed()),
            "legacy bowl copies are saved in the fixture");

        Saved.Remove(area);
        StoredFurniture.Remove(area);
        var reloaded = GetScene(area);
        check(reloaded[source.Id].Removed && reloaded[first.Id].Removed && reloaded[second.Id].Removed,
            "reloading the room keeps destruction and retires both confirmed copies");
        check(!reloaded[neighbor.Id].Removed && !reloaded[singleOverlap.Id].Removed,
            "another position and a single ambiguous overlap are preserved");
        AcceptMove(area, moved with { Operation = Guid.NewGuid().ToString("N"), BaseRevision = moved.Revision });
        check(GetScene(area)[source.Id].Removed, "delayed placement cannot revive a destroyed bowl");
    }

    internal static void ProbeFurnitureInCurrentRoom()
    {
        var lookup = new PlacementLookup();
        var decorations = UnityEngine.Object.FindObjectsOfType<DecorationItem>();
        foreach (string kind in new[] { "bed", "furnitureworkbench", "container" })
        {
            GameObject obj = decorations.Where(item => item != null && item.gameObject.activeInHierarchy)
                .Select(item => item.gameObject).FirstOrDefault(candidate =>
                    (kind == "bed" ? IsFixedBed(candidate) :
                    kind == "container" ? candidate.GetComponentInChildren<Container>(true) != null &&
                        !candidate.name.Contains("furnitureworkbench", StringComparison.OrdinalIgnoreCase) :
                    candidate.name.Contains(kind, StringComparison.OrdinalIgnoreCase)) &&
                    candidate.GetComponentsInChildren<Renderer>(true).Any(VisibleRenderer));
            var move = CaptureFurniture(obj);
            if (move == null)
            {
                MelonLoader.MelonLogger.Warning($"[Furniture room probe] SKIP {kind}: no visible fixture");
                continue;
            }
            var renderers = obj.GetComponentsInChildren<Renderer>(true).Where(VisibleRenderer).ToArray();
            foreach (var renderer in renderers) renderer.forceRenderingOff = true;
            int countBefore = decorations.Count(item => item != null);
            var moved = move with { Pose = move.Pose with { X = move.Pose.X + .1f } };
            bool applied = Apply(moved, lookup);
            BoxSectorManager.s_Instance?.UpdateSectors();
            bool atTarget = (obj.transform.position - moved.Pose.Position).sqrMagnitude < .0001f;
            bool visible = renderers.Any(VisibleRenderer);
            int countAfter = UnityEngine.Object.FindObjectsOfType<DecorationItem>().Length;
            MelonLoader.MelonLogger.Msg($"[Furniture room probe] {kind} applied={applied} target={atTarget} visible={visible} sameCount={countBefore == countAfter} id={move.Id}");
            foreach (var renderer in renderers) renderer.forceRenderingOff = false;
            Apply(move, lookup);
        }
    }

    internal static void ProbeCookingAndBooks()
    {
        var slot = UnityEngine.Object.FindObjectsOfType<CookingSlot>().FirstOrDefault(candidate =>
            candidate.m_GearPlacePoint != null && candidate.m_GearPlacePoint.m_FireToAttach != null &&
            candidate.m_GearPlacePoint.GetPlacedGear() == null);
        if (slot == null) throw new InvalidOperationException("Fixture needs an empty cooking slot.");
        var point = slot.m_GearPlacePoint;
        var pose = Pose.From(point.transform);
        const int owner = 1;
        const int key = -23803;
        string area = MyMod.level_guid;
        void Check(bool pass, string text)
        {
            if (!pass) throw new InvalidOperationException(text);
            MelonLoader.MelonLogger.Msg("[Cooking QA] PASS " + text);
        }
        void Publish(Pot pot)
        {
            using var packet = MakeServerPacket(2, area, JsonSerializer.Serialize(pot), owner, key);
            using var received = new Packet(packet.ToArray());
            received.ReadInt();
            Receive(received);
            Physics.SyncTransforms();
        }

        foreach (var sample in new[] {
            ("GEAR_RecycledCan", "", 1), ("GEAR_CookingPot", "GEAR_UncookedPorridge", 0),
            ("GEAR_Skillet", "GEAR_RawMeatPtarmigan", 0), ("GEAR_CookingPotDummy", "GEAR_RawMeatRabbit", 0) })
        {
            var empty = new Pot(sample.Item1, pose, pose, "", "", "", 0, true, 0, "", Portion: Guid.NewGuid());
            int body = 0;
            if (!IsCookingDummyName(sample.Item1))
            {
                Publish(empty);
                body = RemotePots[(owner, key)].Visual.GetComponentsInChildren<MeshRenderer>().Length;
            }
            var filled = empty with { Food = sample.Item2, Liquid = sample.Item3, Mode = "Cooking", Liters = .5f, Minutes = 25,
                FoodState = sample.Item2 == "GEAR_RawMeatPtarmigan" ? new CookingFood(.97f, .84f, 420f, 420f, 0f, 0) : null };
            Publish(filled);
            var remote = RemotePots[(owner, key)];
            int renders = remote.Visual.GetComponentsInChildren<MeshRenderer>().Count(renderer =>
                renderer.enabled && renderer.sharedMaterials.Length > 0 && renderer.sharedMaterials[0] != null);
            Check(renders > body, sample.Item1 + " renders the contents with material");
            var center = remote.Hitboxes[0].bounds.center;
            foreach (var direction in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right, Vector3.up })
                Check(AimCookware(new Ray(center + direction, -direction), 2f) == remote.Visual, sample.Item1 + " aim " + direction);
            var player = GameManager.GetPlayerManagerComponent();
            var camera = GameManager.GetVpFPSCamera().GetCameraComponent().transform;
            var view = Pose.From(camera);
            try
            {
                var direction = (Vector3.up + Vector3.forward * .35f).normalized;
                camera.SetPositionAndRotation(center + direction, Quaternion.LookRotation(-direction));
                Check(player.GetInteractiveObjectUnderCrosshairs(2.5f) == remote.Visual,
                    sample.Item1 + " actual crosshair selects the center");
                player.FindInteractiveObject(2.5f);
                Check(player.HasInteractiveObjectUnderCrossHair() &&
                    player.ActiveInteraction?.GetInteractiveObject() == remote.Visual &&
                    player.GetInteractiveObjectDisplayTextState() == HoverTextState.CanInteract &&
                    player.GetInteractiveObjectHoverText() == CookingHover(remote.Visual),
                    sample.Item1 + " actual HUD displays the shared status");
                if (sample.Item1 == "GEAR_RecycledCan")
                {
                    Check(player.InteractiveObjectsProcessInteraction() && player.IsInspectModeActive(), "opens the guest's water inspection");
                    var can = inspection.Root.GetComponent<CookingPotItem>();
                    var mesh = can.m_GrubMeshRenderer.GetComponent<MeshFilter>();
                    Check(can.m_GrubMeshRenderer.enabled && mesh.sharedMesh != null && mesh.sharedMesh == can.m_WaterMesh,
                        "native water inspection renders the water surface inside the can");
                    var water = can.GetGearItemForInspectMode()?.m_WaterSupply;
                    Check(water != null && Mathf.Abs(water.m_VolumeInLiters.ToLiters() - filled.Liters) < .001f,
                        "water inspection preserves the owner's volume");
                    player.ExitInspectGearMode(true);
                }
                if (sample.Item1 == "GEAR_Skillet")
                {
                    Check(player.InteractiveObjectsProcessInteraction() && player.IsInspectModeActive(), "opens the guest's raw ptarmigan inspection");
                    var shown = CaptureCookingFood(inspection.Root.GetComponent<CookingPotItem>().GetGearItemForInspectMode());
                    Check(shown != null && Mathf.Abs(shown.Condition - .97f) < .001f &&
                        Mathf.Abs(shown.Weight - .84f) < .001f && Mathf.Abs(shown.Calories - 420f) < .01f,
                        "guest sees the raw ptarmigan's condition, weight and calories");
                    player.ExitInspectGearMode(true);
                }
                if (sample.Item1 == "GEAR_CookingPot")
                {
                    Check(player.InteractiveObjectsProcessInteraction() && player.IsInspectModeActive(),
                        "native use input opens shared cooking inspection");
                    player.ExitInspectGearMode(true);
                    Check(inspection == null && player.ActiveInteraction == null && RemotePots.ContainsKey((owner, key)),
                        "cancel clears shared interaction and keeps the portion on its original slot");
                    player.FindInteractiveObject(2.5f);
                    Check(player.ActiveInteraction?.GetInteractiveObject() == remote.Visual,
                        "cookware can be targeted again after cancel");
                    Publish(filled with { Mode = "Ready" });
                    Check(player.ActiveInteraction == null, "replacing an aimed replica clears the native interaction");
                    player.FindInteractiveObject(2.5f);
                    Check(player.ActiveInteraction?.GetInteractiveObject() == RemotePots[(owner, key)].Visual,
                        "replacement cookware can be targeted");
                }
            }
            finally
            {
                player.ResetInteraction();
                camera.SetPositionAndRotation(view.Position, view.Rotation);
            }
            if (sample.Item1 == "GEAR_CookingPot")
            {
                var obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
                obstacle.transform.position = center + Vector3.up * .55f;
                obstacle.transform.localScale = new Vector3(.2f, .05f, .2f);
                var ray = new Ray(center + Vector3.up, Vector3.down);
                Physics.SyncTransforms();
                bool hitWall = obstacle.GetComponent<Collider>().Raycast(ray, out var occlusion, 2f);
                Check(hitWall && AimCookware(ray, 2f, occlusion) == null, "opaque obstacle blocks remote cookware aim");
                obstacle.SetActive(false);
                UnityEngine.Object.Destroy(obstacle);

            }
            Publish(null);
        }

        GearItem Materialize(string name)
        {
            var prefab = MyMod.GetGearItemObject(name);
            if (prefab == null) throw new InvalidOperationException("Missing fixture prefab " + name);
            var item = UnityEngine.Object.Instantiate(prefab, point.transform.position, pose.Rotation).GetComponent<GearItem>();
            item.SkipSpawnChanceRollInitialDecayAndAutoEvolve();
            item.m_LastUpdatedTODHours = GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused();
            item.ManualStart();
            item.SetNormalizedHP(.72f, false);
            return item;
        }

        point.m_FireToAttach.FireStateSet(FireState.FullBurn);
        var inventory = GameManager.GetInventoryComponent();
        foreach (var name in new[] { "GEAR_CookingPot", "GEAR_Skillet", "GEAR_RecycledCan" })
        {
            var old = Materialize(name);
            point.PlaceGear(old, reposition: false);
            var id = Guid.NewGuid();
            var request = new CookingRequest(owner, UseEmptyCookware, id);
            var transfer = TakeCookingGear(key, request, 0, point, old, old.m_CookingPotItem, handoff: true);
            Check(transfer != null && !old.gameObject.activeSelf, name + " detached without dropping");
            AuthorizedCookingTransfers[id] = new CookingAuthorization(area, owner, key, 0, UseEmptyCookware, pose, Time.realtimeSinceStartup + 8f);
            AcceptCookingGear(area, owner, transfer, JsonSerializer.Serialize(transfer));
            var placed = point.GetPlacedGear();
            Check(placed != null && placed.GetGearName() == name && placed.m_CookingPotItem?.GetGearPlacePoint() == point &&
                !placed.m_InPlayerInventory && placed.gameObject.activeInHierarchy, name + " installed on recipient's cooking slot");
            var picker = GameCompat.Panel<Panel_ActionPicker>();
            Check(picker != null && !picker.isActiveAndEnabled, name + " transfer leaves the action picker closed");
            DeliverGear(area, transfer);
            Check(point.GetPlacedGear() == placed && !picker.isActiveAndEnabled,
                name + " repeated receipt keeps one copy and leaves the action picker closed");
            slot.UpdateInteraction(0f);
            Check(slot.PerformInteraction() && picker.isActiveAndEnabled && picker.m_ActionPickerItemDataList.Count >= 3,
                name + " opens native pick-up, cook and water actions on use");
            picker.Enable(false);
            placed.m_CookingPotItem.DetachFromFire();
            point.ResetPlacedGear();
            RetireCookingGear(placed);
        }

        foreach (var sample in new[] { ("GEAR_Skillet", "GEAR_RawMeatRabbit", "GEAR_CookedMeatRabbit"),
            ("GEAR_Skillet", "GEAR_RawMeatPtarmigan", "GEAR_CookedMeatPtarmigan"),
            ("GEAR_CookingPot", "GEAR_UncookedPorridge", "GEAR_CookedPorridge") })
        {
            var vessel = Materialize(sample.Item1);
            point.PlaceGear(vessel, reposition: false);
            var raw = Materialize(sample.Item2);
            raw.SetNormalizedHP(.42f, false);
            var pot = vessel.m_CookingPotItem;
            pot.m_GearItemBeingCooked = raw;
            pot.m_CookingState = CookingPotItem.CookingState.Ready;
            pot.MaybeSpawnGearItemForInspectMode();
            var ownerPreview = CaptureCookingFood(pot.GetGearItemForInspectMode());
            var id = Guid.NewGuid();
            var request = new CookingRequest(owner, 2, id);
            var transfer = TakeCookingGear(key, request, 0, point, vessel, pot, handoff: false);
            Check(transfer != null && transfer.Gear == sample.Item3 && pot.m_GearItemBeingCooked == null &&
                point.GetPlacedGear() == vessel && !raw.gameObject.activeSelf, sample.Item3 + " uses native output and clears only the food");
            Check(ownerPreview != null && Mathf.Abs(ownerPreview.Condition - transfer.FoodState.Condition) < .001f &&
                Mathf.Abs(ownerPreview.Weight - transfer.FoodState.Weight) < .001f, sample.Item3 + " transfers the owner's preview condition and weight");
            int count = inventory.NumGearInInventory(sample.Item3);
            AuthorizedCookingTransfers[id] = new CookingAuthorization(area, owner, key, 0, 2, pose, Time.realtimeSinceStartup + 8f);
            AcceptCookingGear(area, owner, transfer, JsonSerializer.Serialize(transfer));
            DeliverGear(area, transfer);
            var received = inventory.GearInInventoryAtCondition(sample.Item3, 1, Mathf.RoundToInt(transfer.Condition * 100f));
            Check(inventory.NumGearInInventory(sample.Item3) == count + 1 && received != null &&
                Mathf.Abs(received.GetNormalizedCondition() - transfer.Condition) < .01f &&
                Mathf.Abs(received.m_FoodItem.m_HeatPercent - transfer.Heat) < .01f &&
                Mathf.Abs(received.WeightKG.ToKilograms() - ownerPreview.Weight) < .001f,
                sample.Item3 + " one inventory item with the owner's condition, weight and heat");
            pot.DetachFromFire();
            point.ResetPlacedGear();
            RetireCookingGear(vessel);
        }

        var book = Materialize("GEAR_BookCooking");
        MyMod.PatchBookReadTime(book);
        string bookId = book.m_ObjectGuid.Get();
        book.m_ResearchItem.m_ElapsedHours = book.m_ResearchItem.m_TimeRequirementHours;
        string bookJson = book.SerializeToString();
        var other = Materialize("GEAR_BookCooking");
        other.DeserializeFromJson(bookJson);
        MyMod.PatchBookReadTime(other, personal: true);
        Check(other.m_ResearchItem.m_ElapsedHours == 0f, "received book starts at the guest's unread progress");
        MyMod.BooksResearched[bookId] = 2f;
        MyMod.PatchBookReadTime(other, personal: true);
        Check(other.m_ObjectGuid.Get() == bookId && Mathf.Abs(other.m_ResearchItem.m_ElapsedHours - 2f) < .01f,
            "same book resumes the current reader's progress");
        RetireCookingGear(book);
        RetireCookingGear(other);
    }

    internal static void ProbeGuestCooking()
    {
        var slot = UnityEngine.Object.FindObjectsOfType<CookingSlot>().FirstOrDefault(candidate =>
            candidate.m_GearPlacePoint != null && candidate.m_GearPlacePoint.GetPlacedGear() == null);
        if (slot?.m_GearPlacePoint == null) return;
        var pose = Pose.From(slot.m_GearPlacePoint.transform);
        const int owner = 1;
        const int key = -23802;
        var state = new Pot("GEAR_RecycledCan", pose, pose, "", "", "Cooking", 10, true, 2,
            "Снег\nГотовится " + new string('я', 200), .5f);
        string json = JsonSerializer.Serialize(state);
        try
        {
            using var packet = MakeServerPacket(2, MyMod.level_guid, json, owner, key);
            using var received = new Packet(packet.ToArray());
            received.ReadInt();
            Receive(received);
            var replica = FindRemotePot(slot.m_GearPlacePoint);
            Physics.SyncTransforms();
            int hits = 0;
            var firstCollider = replica?.Value.Hitboxes?.FirstOrDefault();
            if (firstCollider != null)
            {
                Vector3 center = firstCollider.bounds.center;
                foreach (Vector3 direction in new[] { Vector3.forward, Vector3.right, Vector3.back, Vector3.left, Vector3.up })
                    if (AimCookware(new Ray(center + direction, -direction), 2f) == replica.Value.Value.Visual) hits++;
            }
            var player = GameManager.GetPlayerManagerComponent();
            bool opened = replica is { } pot && OpenCookingActions(pot.Value.Visual) && player.IsInspectModeActive();
            MelonLoader.MelonLogger.Msg($"[Guest cooking probe] encoded={json.Length} replica={replica?.Value.Visual != null} hover={!string.IsNullOrEmpty(CookingHover(slot.gameObject))} rays={hits}/5 inspected={opened}");
            if (opened) player.ExitInspectGearMode(true);
        }
        finally { ReceivePot(MyMod.level_guid, owner, key, null); }
    }

    [HarmonyPatch]
    private static class CookingScope
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(Panel_Cooking), nameof(Panel_Cooking.OnCook)),
            AccessTools.Method(typeof(Panel_Cooking), nameof(Panel_Cooking.OnCookNormalFood)),
            AccessTools.Method(typeof(Panel_Cooking), nameof(Panel_Cooking.OnCookRecipe)),
            AccessTools.Method(typeof(Panel_GearSelect), "DoFirePickerAction"),
            AccessTools.Method(typeof(Panel_CookWater), nameof(Panel_CookWater.OnBoil)),
            AccessTools.Method(typeof(Panel_CookWater), nameof(Panel_CookWater.OnMeltSnow)),
            AccessTools.Method(typeof(Panel_CookWater), nameof(Panel_CookWater.OnDoWater)),
            AccessTools.Method(typeof(CookingPotItem), nameof(CookingPotItem.StartCooking))
        };
        private static void Prefix() => cookingDepth++;
        private static void Finalizer() => cookingDepth--;
    }

    [HarmonyPatch(typeof(GearPlacePoint), nameof(GearPlacePoint.DropAndPlaceItem))]
    private static class PlaceCookingGear
    {
        private static bool Prefix(GearPlacePoint __instance, out bool __state)
        {
            __state = false;
            if (!MyMod.InOnline() || __instance.m_FireToAttach == null) return true;
            if (RemotePotAt(__instance))
            {
                HUDMessage.AddMessage("Конфорка занята");
                return false;
            }
            __state = true;
            cookingDepth++;
            return true;
        }

        private static void Finalizer(bool __state)
        {
            if (__state) cookingDepth--;
        }
    }

    [HarmonyPatch(typeof(GearPlacePoint), nameof(GearPlacePoint.IsAllowedGearItem))]
    private static class ValidateSlot
    {
        private static void Postfix(GearPlacePoint __instance, ref bool __result)
        {
            if (RemotePotAt(__instance)) __result = false;
        }
    }

    [HarmonyPatch(typeof(GearPlacePoint), nameof(GearPlacePoint.PlaceGear))]
    private static class ReserveSlot
    {
        private static bool Prefix(GearPlacePoint __instance) => !RemotePotAt(__instance);

        private static void Postfix(GearPlacePoint __instance)
        {
            if (InWorld && __instance.m_FireToAttach != null)
                CookingPoints[__instance.GetInstanceID()] = __instance;
        }
    }

    [HarmonyPatch(typeof(GearPlacePoint), nameof(GearPlacePoint.FindFirstEmptyPlacePoint))]
    private static class FindFreeSlot
    {
        private static void Postfix(GameObject __0, ref GearPlacePoint __result)
        {
            if (__0 == null || !RemotePotAt(__result)) return;
            __result = null;
            foreach (var slot in __0.GetComponentsInChildren<CookingSlot>())
            {
                var point = slot.m_GearPlacePoint;
                if (point == null || point.GetPlacedGear() != null || RemotePotAt(point)) continue;
                __result = point;
                break;
            }
        }
    }

    [HarmonyPatch(typeof(CookingPotItem), nameof(CookingPotItem.AttachToFire))]
    private static class PotAttached
    {
        private static void Postfix(CookingPotItem __instance)
        {
            if (MyMod.InOnline()) LocalPots[__instance.GetInstanceID()] = __instance;
        }
    }

    private static void TickPots()
    {
        float now = Time.realtimeSinceStartup;
        if (now < nextCookingPublish) return;
        nextCookingPublish = now + .45f;
        if (now >= nextCookingScan)
        {
            nextCookingScan = now + 3f;
            foreach (var slot in UnityEngine.Object.FindObjectsOfType<CookingSlot>())
                if (slot.m_GearPlacePoint != null)
                    CookingPoints[slot.m_GearPlacePoint.GetInstanceID()] = slot.m_GearPlacePoint;
            foreach (var pot in UnityEngine.Object.FindObjectsOfType<CookingPotItem>())
                if (pot.GetGearPlacePoint() != null) LocalPots[pot.GetInstanceID()] = pot;
        }

        var potsByPoint = new Dictionary<int, CookingPotItem>();
        foreach (var entry in LocalPots.ToArray())
        {
            var pot = entry.Value;
            var point = pot != null && pot.isActiveAndEnabled ? pot.GetGearPlacePoint() : null;
            if (point == null || pot.m_GearItem == null)
            {
                LocalPots.Remove(entry.Key);
                continue;
            }
            int id = point.GetInstanceID();
            CookingPoints[id] = point;
            if (!potsByPoint.TryGetValue(id, out var previous) || (!previous.IsCookingSomething() && pot.IsCookingSomething()))
                potsByPoint[id] = pot;
        }

        LocalCooking.Clear();
        foreach (var entry in CookingPoints.ToArray())
        {
            var point = entry.Value;
            if (point == null)
            {
                CookingPoints.Remove(entry.Key);
                continue;
            }
            var gear = point.GetPlacedGear();
            potsByPoint.TryGetValue(entry.Key, out var attachedPot);
            var pot = gear?.m_CookingPotItem;
            if (attachedPot != null && (pot == null || (!pot.IsCookingSomething() && attachedPot.IsCookingSomething()))) pot = attachedPot;
            if (pot != null && pot.m_GearItem != null) gear = pot.m_GearItem;
            if (gear == null || gear.m_InPlayerInventory) continue;
            string name = gear.GetGearName();
            if (!IsCookingGearName(name)) continue;
            int key = gear.GetInstanceID();
            LocalCooking[key] = (point, gear, pot);
            int liquid = pot == null ? 0 : pot.m_LitersSnowBeingMelted.ToLiters() > 0 ? 2 :
                pot.m_LitersWaterBeingBoiled.ToLiters() > 0 ? 1 : 0;
            var visible = new System.Text.StringBuilder();
            foreach (var renderer in gear.GetComponentsInChildren<MeshRenderer>(true))
            {
                if (visible.Length >= 256) break;
                if (liquid != 0 && renderer == pot.m_GrubMeshRenderer) continue;
                if (renderer.enabled && renderer.gameObject.activeInHierarchy)
                    visible.Append(PotMeshKey(renderer, gear.transform));
            }
            var food = pot?.m_GearItemBeingCooked;
            int action = liquid == 2 ? 4 : liquid == 1 ? 3 : food != null ? 2 : 1;
            Guid portion = GetCookingPortion(key, food, action);
            bool canTake = pot == null || CanTakeFromPot(pot);
            PublishedPots.Add(key);
            var slotPose = Pose.From(point.transform);
            var gearPose = (gear.transform.position - point.transform.position).sqrMagnitude < .5625f ? Pose.From(gear.transform) : slotPose;
            PublishPot(key, new Pot(name, gearPose, slotPose, visible.ToString(),
                food != null ? food.GetGearName() : "", pot?.GetCookingState().ToString() ?? "",
                pot != null && float.IsFinite(pot.m_MinutesUntilCooked) ? Mathf.CeilToInt(pot.m_MinutesUntilCooked) : 0, canTake, liquid, pot?.GetHoverText() ?? "",
                (liquid == 2 ? pot.m_LitersSnowBeingMelted.ToLiters() : pot?.m_LitersWaterBeingBoiled.ToLiters() ?? 0f), portion,
                CaptureCookingFood(food), liquid == 2 ? pot.m_LitersWaterBeingBoiled.ToLiters() : 0f,
                Mathf.Clamp01(pot?.m_PercentCooked ?? 0f), Mathf.Max(0f, pot?.m_CookingElapsedHours ?? 0f)));
        }
        foreach (int key in PublishedPots.ToArray())
        {
            if (LocalCooking.ContainsKey(key) || !PublishedPots.Remove(key)) continue;
            LocalCookingPortions.Remove(key);
            PublishPot(key, null);
        }
        foreach (int key in RejectedCooking.ToArray())
        {
            if (LocalCooking.ContainsKey(key)) ReleaseCookware(key);
            else RejectedCooking.Remove(key);
        }
    }

    private static bool IsCookingGearName(string name) => !string.IsNullOrEmpty(name) && name.Length <= 64 &&
        name.StartsWith("GEAR_", StringComparison.OrdinalIgnoreCase);

    private static bool CanTakeFromPot(CookingPotItem pot)
    {
        return !pot.m_IsBeingInspected && (!pot.IsDummyPot() || pot.m_GearItemBeingCooked != null);
    }

    private static bool IsPotName(string name) => name != null && name.StartsWith("GEAR_", StringComparison.Ordinal) &&
        name.Length <= 64 && (name.Contains("CookingPot", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Skillet", StringComparison.OrdinalIgnoreCase) || name.Contains("RecycledCan", StringComparison.OrdinalIgnoreCase));

    private static void PublishPot(int key, Pot pot)
    {
        string data = JsonSerializer.Serialize(pot);
        if (MyMod.iAmHost) AcceptCookingState(MyMod.level_guid, 0, key, pot, data);
        else SendToHost(2, MyMod.level_guid, data, key);
    }

    private static void AcceptCookingState(string area, int owner, int key, Pot pot, string data)
    {
        var id = (area, owner, key);
        if (pot == null) CookingClaims.Remove(id);
        else
        {
            if (!ValidPot(pot)) return;
            foreach (var occupied in CookingClaims)
            {
                if (occupied.Key.Scene != area || occupied.Key.Owner == owner ||
                    occupied.Value.Expires <= Time.realtimeSinceStartup ||
                    (occupied.Value.State.Slot.Position - pot.Slot.Position).sqrMagnitude >= SlotMatchSquared) continue;
                if (owner == 0) RejectCooking(key);
                else
                {
                    using var rejection = MakeServerPacket(6, area, "", 0, key);
                    ServerSend.SendUDPData(owner, rejection);
                }
                return;
            }
            if (!CookingClaims.ContainsKey(id) && CookingClaims.Count >= 512) return;
            CookingClaims[id] = new SlotClaim(pot, Time.realtimeSinceStartup + 7f);
        }
        ReceivePot(area, owner, key, pot);
        SendToPeers(2, area, data, owner, key);
    }

    private static void RejectCooking(int key)
    {
        if (RejectedCooking.Add(key)) HUDMessage.AddMessage("Этот слот уже занял другой игрок");
    }

    internal static bool RemotePotAt(GearPlacePoint point) => FindRemotePot(point).HasValue;

    private static bool CookingSlotVacant(GearPlacePoint point)
    {
        if (point == null) return false;
        var item = point.GetPlacedGear();
        return item == null || !item.gameObject.activeInHierarchy ||
            IsCookingDummy(item) && item.m_CookingPotItem?.m_GearItemBeingCooked == null;
    }

    private static KeyValuePair<(int Owner, int Key), RemotePot>? FindRemotePot(GearPlacePoint point)
    {
        if (point == null || !MyMod.InOnline()) return null;
        Vector3 position = point.transform.position;
        foreach (var pot in RemotePots)
            if ((pot.Value.State.Slot.Position - position).sqrMagnitude < SlotMatchSquared) return pot;
        return null;
    }

    private static KeyValuePair<(int Owner, int Key), RemotePot>? FindRemoteCookware(GameObject target)
    {
        if (target == null || !InWorld) return null;
        foreach (var pair in RemotePots)
            if (pair.Value.Visual == target) return pair;
        var slot = target.GetComponentInParent<CookingSlot>();
        var point = slot?.m_GearPlacePoint ?? target.GetComponentInParent<GearPlacePoint>();
        return CookingSlotVacant(point) ? FindRemotePot(point) : null;
    }

    private static readonly Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> CookingOccluders = new(16);

    private static GameObject AimCookware(float range)
    {
        if (RemotePots.Count == 0 || !InWorld || CoopReturnToMenu.InProgress) return null;
        var camera = GameManager.GetVpFPSCamera()?.GetCameraComponent();
        if (camera == null) return null;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        var selected = AimCookware(ray, range);
        var replica = FindRemoteCookware(selected)?.Value;
        if (replica == null) return null;
        float distance = range;
        foreach (var collider in replica.Hitboxes)
            if (collider != null && collider.Raycast(ray, out var hit, distance)) distance = hit.distance;
        int count = Physics.RaycastNonAlloc(ray, CookingOccluders, distance, Utils.m_PhysicalCollisionLayerMask, QueryTriggerInteraction.Ignore);
        var blockers = CookingOccluders;
        if (count == blockers.Length)
        {
            blockers = Physics.RaycastAll(ray, distance, Utils.m_PhysicalCollisionLayerMask, QueryTriggerInteraction.Ignore);
            count = blockers.Length;
        }
        for (int i = 0; i < count; i++)
            if (BlocksCookware(blockers[i], distance, replica.State.Slot.Position)) return null;
        return selected;
    }

    // FindInteractiveObject runs only after a native scene hit. Select the
    // cookware at the crosshair entry points even when only its replica is hit.
    [HarmonyPatch]
    private static class CrosshairCookwareTarget
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.GetInteractiveObjectUnderCrosshairs));
            yield return AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.GetInteractiveObjectNearCrosshairs));
        }

        private static bool Prefix(float __0, ref GameObject __result)
        {
            var target = AimCookware(__0);
            if (target == null) return true;
            __result = target;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.FindInteractiveObject), new[] { typeof(float) })]
    private static class SelectSharedCookwareInteraction
    {
        private static bool Prefix(PlayerManager __instance, float __0)
        {
            var target = AimCookware(__0);
            if (target == null || !target.TryGetComponent<Comps.SharedCookwareInteraction>(out var interaction) || !interaction.IsEnabled) return true;
            var native = interaction.Cast<IInteraction>();
            __instance.m_LocationOfLastInteractHit = target.transform.position;
            __instance.SetCurrentInteraction(native);
            return false;
        }
    }

    [HarmonyPatch(typeof(BaseInteraction), nameof(BaseInteraction.TriggerEvent))]
    private static class SharedCookwareEvents
    {
        // These injected interactions have no serialized events for the native dispatcher.
        private static bool Prefix(BaseInteraction __instance) =>
            __instance.TryCast<Comps.SharedCookwareInteraction>() is null;
    }

    private static void ClearSharedCookwareInteraction(PlayerManager player, GameObject visual = null)
    {
        if (player == null || player.ActiveInteraction?.TryCast<Comps.SharedCookwareInteraction>() is not { } interaction) return;
        if (interaction == null)
        {
            player.ActiveInteraction = null;
            player.IsInteractionNearCrosshair = false;
        }
        else if (visual == null || interaction.gameObject == visual)
            player.ResetInteraction();
    }

    private static GameObject AimCookware(Ray ray, float nearest, RaycastHit worldHit = default)
    {
        GameObject selected = null;
        foreach (var remote in RemotePots.Values)
        {
            if (remote.Hitboxes == null) continue;
            foreach (var collider in remote.Hitboxes)
            {
                if (collider == null || !collider.Raycast(ray, out var hit, nearest) ||
                    BlocksCookware(worldHit, hit.distance, remote.State.Slot.Position)) continue;
                selected = remote.Visual;
                nearest = hit.distance;
            }
        }
        return selected;
    }

    private static bool BlocksCookware(RaycastHit worldHit, float distance, Vector3 slotPosition)
    {
        if (worldHit.collider == null || distance <= worldHit.distance + .025f) return false;
        var hit = worldHit.collider;
        var hitSlot = hit.GetComponentInParent<CookingSlot>();
        if (hitSlot?.m_GearPlacePoint != null &&
            (hitSlot.m_GearPlacePoint.transform.position - slotPosition).sqrMagnitude < SlotMatchSquared) return false;
        var fixture = hit.GetComponentInParent<FireplaceInteraction>();
        var fire = fixture != null ? fixture.Fire : hit.GetComponentInParent<Fire>();
        if (fire == null) return true;
        foreach (var point in CookingPoints.Values)
            if (point != null && point.m_FireToAttach == fire &&
                (point.transform.position - slotPosition).sqrMagnitude < SlotMatchSquared) return false;
        return true;
    }

    internal static string CookingHover(GameObject target) => FindRemoteCookware(target) is { } entry ? CookingLabel(entry) : null;

    private static string CookingLabel(KeyValuePair<(int Owner, int Key), RemotePot> entry)
    {
        var state = entry.Value.State;
        string title = GameCompat.GetGearDisplayName(state.Food.Length > 0 ? state.Food : state.Gear);
        string description = state.Liquid == 2 ? "Тает снег" : state.Liquid == 1 ? "Кипятится вода" : state.Food.Length > 0 ? "Готовится" : "Пустая посуда";
        if (state.Mode == "Ready") description = state.Liquid == 1 ? "Питьевая вода" : "Готово";
        else if (state.Mode == "Ruined") description = state.Liquid == 1 ? "Вода выкипела" : "Испорчено";
        else if (state.Minutes > 0 && (state.Liquid != 0 || state.Food.Length > 0)) description += $" - До готовности: {state.Minutes} мин.";
        string text = !string.IsNullOrWhiteSpace(state.NativeHover) && (state.Food.Length > 0 || state.Liquid != 0)
            ? state.NativeHover.Trim() : title + "\n" + description;
        if (!text.Contains("\n")) text += "\n" + description;
        int owner = entry.Key.Owner;
        if (owner >= 0 && owner < MyMod.playersData.Count && !string.IsNullOrEmpty(MyMod.playersData[owner]?.m_Name))
            text += " · " + MyMod.playersData[owner].m_Name;
        return text;
    }

    [HarmonyPatch(typeof(CookingSlot), nameof(CookingSlot.UpdateInteraction), new[] { typeof(float) })]
    private static class RefreshRemoteCookingHover
    {
        private static void Postfix(CookingSlot __instance)
        {
            if (!CookingSlotVacant(__instance.m_GearPlacePoint)) return;
            string label = CookingHover(__instance.gameObject);
            if (label == null) return;
            __instance.HoverText = label;
            __instance.m_CanInteract = true;
        }
    }

    [HarmonyPatch(typeof(CookingSlot), nameof(CookingSlot.PerformInteraction))]
    private static class InteractWithRemoteCookware
    {
        private static bool Prefix(CookingSlot __instance, ref bool __result)
        {
            if (!CookingSlotVacant(__instance.m_GearPlacePoint) ||
                !FindRemotePot(__instance.m_GearPlacePoint).HasValue) return true;
            __result = OpenCookingActions(__instance.gameObject);
            return false;
        }
    }

    private sealed record CookingInspection(string Scene, int Key, CookingRequest Request, GameObject Root, GearItem Food);
    private static CookingInspection inspection;
    private sealed record CookwareUse(string Scene, Guid Id, float Expires);
    private static CookwareUse cookwareUse;

    internal static bool OpenCookingActions(GameObject target)
    {
        if (CoopReturnToMenu.InProgress || target == null || !target.activeInHierarchy ||
            FindRemoteCookware(target) is not { } entry) return false;
        var player = GameManager.GetPlayerManagerComponent();
        if (inspection != null || player == null || player.GetControlMode() != PlayerControlMode.Normal ||
            player.IsInPlacementMode() || player.IsInspectModeActive() ||
            InterfaceManager.IsPanelEnabled<Panel_Confirmation>() ||
            GameCompat.Panel<Panel_ActionPicker>()?.isActiveAndEnabled == true) return false;
        var state = entry.Value.State;
        if (state.CanTake && TakeAction(state) == 1 && IsPotName(state.Gear) && !IsCookingDummyName(state.Gear))
        {
            cookwareUse = new CookwareUse(MyMod.level_guid, state.Portion, Time.realtimeSinceStartup + 3f);
            RequestSharedCookware(entry.Key.Key, new CookingRequest(entry.Key.Owner, UseEmptyCookware, state.Portion));
            return true;
        }
        var prefab = MyMod.GetGearItemObject(state.Gear);
        if (prefab == null || !state.CanTake) return true;
        var root = UnityEngine.Object.Instantiate(prefab);
        root.transform.position = player.transform.position + Vector3.up * 100f;
        root.AddComponent<Comps.DoNotSerializeThis>();
        foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        var pot = root.GetComponent<CookingPotItem>();
        if (pot == null)
        {
            if (root.GetComponent<GearItem>() != null)
            {
                UnityEngine.Object.Destroy(root);
                RequestSharedCookware(entry.Key.Key, new CookingRequest(entry.Key.Owner, TakeAction(state), state.Portion));
                return true;
            }
            UnityEngine.Object.Destroy(root);
            return true;
        }
        pot.enabled = false;
        pot.m_MinutesUntilCooked = state.Minutes;
        pot.m_PercentCooked = state.Cooked;
        pot.m_CookingElapsedHours = state.Elapsed;
        if (Enum.TryParse<CookingPotItem.CookingState>(state.Mode, out var mode)) pot.m_CookingState = mode;
        pot.m_GearItem = root.GetComponent<GearItem>();
        pot.m_LitersSnowBeingMelted = GameCompat.Liters(state.Liquid == 2 ? state.Liters : 0f);
        pot.m_LitersWaterBeingBoiled = GameCompat.Liters(state.Liquid == 1 ? state.Liters : state.WaterDuringMelt);
        var foodPrefab = state.Food.Length == 0 ? null : MyMod.GetGearItemObject(state.Food);
        GearItem food = null;
        if (foodPrefab != null)
        {
            var foodObject = UnityEngine.Object.Instantiate(foodPrefab, root.transform);
            foreach (var collider in foodObject.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            food = foodObject.GetComponent<GearItem>();
            ApplyCookingFood(food, state.FoodState);
            pot.m_GearItemBeingCooked = food;
        }
        var request = new CookingRequest(entry.Key.Owner, TakeAction(state), state.Portion);
        inspection = new CookingInspection(MyMod.level_guid, entry.Key.Key, request, root, food);
        try
        {
            if (pot.m_GrubMeshRenderer != null)
            {
                if (state.Liquid != 0) pot.SetUpWaterMesh();
                else if (food != null) pot.SetUpGrubMesh();
            }
            if (request.Action == 1) player.EnterInspectGearModeFromCookingPot(root.GetComponent<GearItem>(), pot);
            else pot.StartInspectMode();
            if (!player.IsInspectModeActive()) EndCookingInspection();
        }
        catch (Exception ex)
        {
            EndCookingInspection();
            MelonLoader.MelonLogger.Warning("[Cooking] Inspect: " + ex.Message);
        }
        return true;
    }

    private static CookingFood CaptureCookingFood(GearItem item)
    {
        if (item?.m_FoodItem is not { } food) return null;
        return new CookingFood(item.GetNormalizedCondition(), item.WeightKG.ToKilograms(), food.m_CaloriesRemaining,
            food.m_CaloriesTotal, food.m_HeatPercent, food.m_NumTimesHeatedUp);
    }

    private static void ApplyCookingFood(GearItem item, CookingFood state)
    {
        if (state == null || item?.m_FoodItem is not { } food) return;
        item.m_RolledSpawnChance = true;
        item.SetNormalizedHP(state.Condition, roundToWholeNumber: false);
        item.WeightKG = GameCompat.Kilograms(state.Weight);
        food.m_CaloriesRemaining = state.Calories;
        food.m_CaloriesTotal = state.TotalCalories;
        food.m_HeatPercent = state.Heat;
        food.m_NumTimesHeatedUp = state.Reheats;
        item.UpdateDamageShader();
    }

    private static void RequestSharedCookware(int key, CookingRequest request)
    {
        if (request.Portion == Guid.Empty) return;
        if (MyMod.iAmHost) RequestCookwareRelease(0, key, MyMod.level_guid, request);
        else SendToHost(4, MyMod.level_guid, JsonSerializer.Serialize(request), key);
    }

    private static void EndCookingInspection()
    {
        var current = inspection;
        inspection = null;
        if (current?.Root != null)
        {
            var pot = current.Root.GetComponent<CookingPotItem>();
            var spawned = pot?.m_GearItemSpawnedForInspectMode;
            if (pot != null)
            {
                pot.m_GearItemBeingCooked = null;
                pot.m_GearItemSpawnedForInspectMode = null;
                pot.m_GearItemToEatDrinkFromInspectMode = null;
            }
            if (spawned != null && spawned.gameObject != current.Root && spawned != current.Food && !spawned.m_InPlayerInventory)
                RetireCookingGear(spawned);
            UnityEngine.Object.Destroy(current.Root);
        }
        if (current?.Food != null) RetireCookingGear(current.Food);
    }

    [HarmonyPatch]
    private static class TakeFromSharedInspection
    {
        private static System.Collections.Generic.IEnumerable<System.Reflection.MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.OnPickupFromCookingPotInspection)),
            AccessTools.Method(typeof(PlayerManager), nameof(PlayerManager.OnEquipFromCookingPotInspection))
        };

        private static bool Prefix(PlayerManager __instance)
        {
            return !ConfirmCookingInspection(__instance);
        }
    }

    private static bool ConfirmCookingInspection(PlayerManager player)
    {
        var current = inspection;
        if (current == null || player == null) return false;
        player.ExitInspectGearMode(true);
        if (current.Scene == MyMod.level_guid) RequestSharedCookware(current.Key, current.Request);
        return true;
    }

    [HarmonyPatch(typeof(CookingPotItem), nameof(CookingPotItem.PickUpCookedItem))]
    private static class TakeFromSharedCookingPot
    {
        private static bool Prefix(CookingPotItem __instance) =>
            inspection?.Root != __instance.gameObject || !ConfirmCookingInspection(GameManager.GetPlayerManagerComponent());
    }

    [HarmonyPatch(typeof(CookingPotItem), nameof(CookingPotItem.DoSpecialActionFromInspectMode))]
    private static class DisableRemoteCookSkip
    {
        private static bool Prefix(CookingPotItem __instance, ref bool __result)
        {
            if (inspection?.Root != __instance.gameObject) return true;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(PlayerManager), nameof(PlayerManager.ExitInspectGearMode))]
    private static class CloseSharedInspection
    {
        private static void Postfix(PlayerManager __instance)
        {
            if (inspection == null) return;
            ClearSharedCookwareInteraction(__instance);
            EndCookingInspection();
        }
    }

    private static int TakeAction(Pot state) => state.Liquid == 2 ? 4 : state.Liquid == 1 ? 3 : state.Food.Length > 0 ? 2 : 1;

    private static Guid GetCookingPortion(int key, GearItem food, int action)
    {
        int foodId = food != null ? food.GetInstanceID() : 0;
        if (!LocalCookingPortions.TryGetValue(key, out var portion) || portion.FoodId != foodId || portion.Action != action)
        {
            portion = new CookingPortion(foodId, action, Guid.NewGuid());
            LocalCookingPortions[key] = portion;
        }
        return portion.Id;
    }

    private static void RequestCookwareRelease(int requester, int key, string area, CookingRequest request)
    {
        if (request == null || request.Portion == Guid.Empty) return;
        int owner = request.Owner;
        if (owner == requester || owner < 0 ||
            (owner == 0 ? MyMod.level_guid != area :
             owner >= MyMod.playersData.Count || MyMod.playersData[owner]?.m_LevelGuid != area)) return;
        if (!CookingClaims.TryGetValue((area, owner, key), out var claim) || !claim.State.CanTake ||
            claim.State.Portion != request.Portion) return;
        int action = TakeAction(claim.State);
        if (request.Action != action && !(request.Action == UseEmptyCookware && action == 1 &&
            IsPotName(claim.State.Gear) && !IsCookingDummyName(claim.State.Gear))) return;
        if (action <= 2)
        {
            if (AuthorizedCookingTransfers.Count + HostCookingGear.Count >= 256 || AuthorizedCookingTransfers.ContainsKey(request.Portion)) return;
            AuthorizedCookingTransfers.Add(request.Portion,
                new CookingAuthorization(area, owner, key, requester, request.Action, claim.State.Slot, Time.realtimeSinceStartup + 25f));
        }
        if (owner == 0) ReleaseCookware(key, request, requester);
        else
        {
            using var packet = MakeServerPacket(4, area, JsonSerializer.Serialize(request), requester, key);
            ServerSend.SendUDPData(owner, packet);
        }
    }

    private static void ReleaseCookware(int key, CookingRequest request = null, int requester = -1)
    {
        if (!LocalCooking.TryGetValue(key, out var entry) || entry.Point == null || entry.Gear == null) return;
        var point = entry.Point;
        var pot = entry.Pot;
        var gear = entry.Gear;
        if (pot != null && (pot.GetGearPlacePoint() != point || !CanTakeFromPot(pot))) return;
        if (pot == null && point.GetPlacedGear() != gear) return;

        float water = pot == null ? 0 : pot.m_LitersWaterBeingBoiled.ToLiters();
        bool snow = pot != null && pot.m_LitersSnowBeingMelted.ToLiters() > 0f;
        int currentAction = snow ? 4 : water > 0f ? 3 : pot?.m_GearItemBeingCooked != null ? 2 : 1;
        if (request != null)
        {
            bool handoff = request.Action == UseEmptyCookware && currentAction == 1 && pot != null && !pot.IsDummyPot();
            if ((currentAction != request.Action && !handoff) || request.Portion == Guid.Empty ||
                GetCookingPortion(key, pot?.m_GearItemBeingCooked, currentAction) != request.Portion) return;
            if (currentAction <= 2)
            {
                CommitCookingGear(key, request, requester, point, gear, pot, handoff);
                return;
            }
        }
        if (snow || water > 0f)
        {
            var state = pot.GetCookingState();
            pot.m_LitersSnowBeingMelted = GameCompat.Liters(0);
            pot.m_LitersWaterBeingBoiled = GameCompat.Liters(0);
            LocalCookingPortions.Remove(key);
            pot.UpdateVisuals();
            if (!snow && state != CookingPotItem.CookingState.Ruined)
            {
                int recipient = requester >= 0 ? requester : MyMod.iAmHost ? 0 : ClientUser.myId;
                var transfer = new WaterTransfer(Guid.NewGuid(), recipient, water, state == CookingPotItem.CookingState.Ready);
                if (MyMod.iAmHost) DeliverWater(MyMod.level_guid, transfer);
                else SendToHost(8, MyMod.level_guid, JsonSerializer.Serialize(transfer));
            }
        }
        else
        {
            // A local start can be rejected when the host already gave the slot
            // to another player. Reuse the same extraction and return the item once.
            int local = MyMod.iAmHost ? 0 : ClientUser.myId;
            var rollback = new CookingRequest(local, currentAction, Guid.NewGuid());
            var transfer = TakeCookingGear(key, rollback, local, point, gear, pot, handoff: false);
            if (transfer != null) DeliverGear(MyMod.level_guid, transfer);
        }
    }

    private static void CopyCookingHeat(CookingPotItem pot, GearItem raw, GearItem output,
        CookingPotItem.CookingState cookingState, bool hot)
    {
        var food = output.m_FoodItem;
        if (food == null) return;
        // Reheating can use the same gear on the fire but a fresh copy for inspection.
        bool sameFood = raw.GetGearName() == output.GetGearName();
        if (sameFood && raw.m_FoodItem is { } heated)
        {
            food.m_HeatPercent = heated.m_HeatPercent;
            food.m_NumTimesHeatedUp = Math.Max(food.m_NumTimesHeatedUp, heated.m_NumTimesHeatedUp);
        }
        if (cookingState == CookingPotItem.CookingState.Ready && food.m_HeatPercent <= 0f &&
            (hot || !sameFood && food.m_HeatedWhenCooked))
            pot.SetHeatPercentWhenCooked(food);
    }

    private static void CommitCookingGear(int key, CookingRequest request, int requester,
        GearPlacePoint point, GearItem vessel, CookingPotItem pot, bool handoff)
    {
        if (!MyMod.iAmHost && OutgoingCookingGear.Count >= 64) return;
        var transfer = TakeCookingGear(key, request, requester, point, vessel, pot, handoff);
        if (transfer == null) return;
        string data = JsonSerializer.Serialize(transfer);
        if (MyMod.iAmHost) AcceptCookingGear(MyMod.level_guid, 0, transfer, data);
        else
        {
            float now = Time.realtimeSinceStartup;
            OutgoingCookingGear[transfer.Id] = new CookingDelivery(MyMod.level_guid, transfer, data, "", now + 25f, now + 1f);
            SendToHost(9, MyMod.level_guid, data);
        }
    }

    private static GearTransfer TakeCookingGear(int key, CookingRequest request, int requester,
        GearPlacePoint point, GearItem vessel, CookingPotItem pot, bool handoff)
    {
        var raw = pot?.m_GearItemBeingCooked;
        GearItem source = vessel;
        if (raw != null)
        {
            var state = pot.GetCookingState();
            if (state != CookingPotItem.CookingState.Cooking)
            {
                pot.MaybeSpawnGearItemForInspectMode();
                source = pot.GetGearItemForInspectMode();
                if (source == null || source == vessel) return null;
            }
            else source = raw;
            CopyCookingHeat(pot, raw, source, state, state == CookingPotItem.CookingState.Ready && pot.IsItemBeingCookedHot());
        }

        string json = source.SerializeToString();
        var food = source.m_FoodItem;
        var placement = handoff ? new CookwareHandoff(Pose.From(point.transform), Pose.From(vessel.transform)) : null;
        var transfer = new GearTransfer(request.Portion, requester, source.GetGearName(), json,
            food != null && float.IsFinite(food.m_HeatPercent) ? food.m_HeatPercent : -1f, food?.m_NumTimesHeatedUp ?? 0,
            Mathf.Clamp01(source.GetNormalizedCondition()), request.Owner, key, placement, CaptureCookingFood(source));
        string data = JsonSerializer.Serialize(transfer);
        if (string.IsNullOrEmpty(json) || data.Length > SceneMessageLimits.PayloadLimit(9))
        {
            MelonLoader.MelonLogger.Warning("[Cooking] Could not capture output " + transfer.Gear);
            return null;
        }

        if (raw != null)
        {
            // Native PickUpCookedGearItem(false) can create a physical output. The
            // cooked preview supplies the native HP, weight and food properties.
            pot.m_GearItemBeingCooked = null;
            pot.m_GearItemSpawnedForInspectMode = null;
            pot.m_GearItemToEatDrinkFromInspectMode = null;
            pot.m_CookingElapsedHours = 0f;
            pot.m_PercentCooked = 0f;
            pot.m_MinutesUntilCooked = 0f;
            pot.ResetHeatLoss();
            if (point.GetPlacedGear() == raw || point.GetPlacedGear() == source) point.ResetPlacedGear();
            RetireCookingGear(raw);
            if (source != raw) RetireCookingGear(source);
        }
        if (raw == null || pot.IsDummyPot())
        {
            if (point.m_PlacedGear == vessel) point.ResetPlacedGear();
            if (pot != null)
            {
                LocalPots.Remove(pot.GetInstanceID());
                pot.DetachFromFire();
            }
            RetireCookingGear(vessel);
            LocalCooking.Remove(key);
            PublishedPots.Remove(key);
            PublishPot(key, null);
        }
        else pot.UpdateVisuals();
        LocalCookingPortions.Remove(key);
        return transfer;
    }

    private static void RetireCookingGear(GearItem gear)
    {
        if (gear == null) return;
        gear.gameObject.SetActive(false);
        UnityEngine.Object.Destroy(gear.gameObject);
    }

    private static void AcceptCookingGear(string area, int sender, GearTransfer transfer, string data)
    {
        if (transfer == null || transfer.Id == Guid.Empty) return;
        if (HostCookingGear.TryGetValue(transfer.Id, out var existing))
        {
            if (sender > 0 && existing.Scene == area && existing.Gear.SourceOwner == sender) AcknowledgeCookingGear(sender, area, transfer.Id);
            return;
        }
        if (!AuthorizedCookingTransfers.TryGetValue(transfer.Id, out var pending) ||
            pending.Scene != area || pending.Owner != sender || pending.Owner != transfer.SourceOwner ||
            pending.Key != transfer.SourceKey || pending.Requester != transfer.Requester ||
            !IsCookingGearName(transfer.Gear) || IsCookingDummyName(transfer.Gear) ||
            string.IsNullOrEmpty(transfer.Json) || !float.IsFinite(transfer.Condition) || !ValidCookingFood(transfer.FoodState) ||
            (pending.Action == UseEmptyCookware) != (transfer.Placement != null) ||
            (transfer.Placement != null && (transfer.Placement.Slot?.Valid != true || transfer.Placement.Pose?.Valid != true ||
                (transfer.Placement.Slot.Position - pending.Slot.Position).sqrMagnitude >= SlotMatchSquared))) return;

        AuthorizedCookingTransfers.Remove(transfer.Id);
        if (sender > 0 && Server.clients.ContainsKey(sender)) AcknowledgeCookingGear(sender, area, transfer.Id);
        var delivery = new CookingDelivery(area, transfer, data, transfer.Requester == 0 ? "" : Server.GetMACByID(transfer.Requester), Time.realtimeSinceStartup + 75f);
        RetryCookingGear(delivery, Time.realtimeSinceStartup);
    }

    private static void AcknowledgeCookingGear(int owner, string area, Guid id)
    {
        using var packet = MakeServerPacket(25, area, id.ToString("N"), 0, 0);
        ServerSend.SendUDPData(owner, packet);
    }

    private static void RetryCookingGear(CookingDelivery delivery, float now)
    {
        int recipient = delivery.Gear.Requester;
        if (recipient == 0)
        {
            if (InWorld && DeliverGear(delivery.Scene, delivery.Gear)) delivery = delivery with { Data = null };
        }
        else if (Server.clients.TryGetValue(recipient, out var client) && client.IsBusy() && !client.Departing &&
            !string.IsNullOrEmpty(delivery.Recipient) && Server.GetMACByID(recipient) == delivery.Recipient)
        {
            using var packet = MakeServerPacket(9, delivery.Scene, delivery.Data, 0, 0);
            ServerSend.SendUDPData(recipient, packet);
        }
        HostCookingGear[delivery.Gear.Id] = delivery with { NextSend = now + 1f };
    }

    private static void TickCookingGear()
    {
        if (!MyMod.InOnline())
        {
            OutgoingCookingGear.Clear();
            return;
        }
        float now = Time.realtimeSinceStartup;
        var queue = MyMod.iAmHost ? HostCookingGear : OutgoingCookingGear;
        foreach (var delivery in queue.Values.ToArray())
        {
            if (delivery.Expires <= now)
            {
                queue.Remove(delivery.Gear.Id);
                if (delivery.Data != null) MelonLoader.MelonLogger.Warning("[Cooking] Unconfirmed output " + delivery.Gear.Id);
            }
            else if (delivery.Data != null && delivery.NextSend <= now)
            {
                if (MyMod.iAmHost) RetryCookingGear(delivery, now);
                else
                {
                    SendToHost(9, delivery.Scene, delivery.Data);
                    queue[delivery.Gear.Id] = delivery with { NextSend = now + 1f };
                }
            }
        }
    }

    private static bool AcceptOutput(Guid id)
    {
        if (id == Guid.Empty) return false;
        float now = Time.realtimeSinceStartup;
        foreach (var entry in ReceivedOutputs.ToArray())
            if (entry.Value <= now) ReceivedOutputs.Remove(entry.Key);
        return ReceivedOutputs.TryAdd(id, now + 125f);
    }

    private static bool DeliverGear(string area, GearTransfer transfer)
    {
        if (transfer == null || transfer.Requester != (MyMod.iAmHost ? 0 : ClientUser.myId) ||
            !IsCookingGearName(transfer.Gear) || IsCookingDummyName(transfer.Gear) ||
            !float.IsFinite(transfer.Condition) || !ValidCookingFood(transfer.FoodState) || string.IsNullOrEmpty(transfer.Json)) return false;
        var player = GameManager.GetPlayerManagerComponent();
        var inventory = GameManager.GetInventoryComponent();
        var prefab = MyMod.GetGearItemObject(transfer.Gear);
        if (player == null || inventory == null || prefab == null || transfer.Id == Guid.Empty) return false;
        if (!AcceptOutput(transfer.Id)) return true;
        bool recentUse = cookwareUse is { } use && use.Id == transfer.Id && use.Scene == area &&
            use.Expires >= Time.realtimeSinceStartup;
        if (cookwareUse?.Id == transfer.Id) cookwareUse = null;

        var gear = UnityEngine.Object.Instantiate(prefab, player.transform.position, Quaternion.identity).GetComponent<GearItem>();
        gear.DeserializeFromJson(transfer.Json);
        // Native deserialization restores the sender's world position and may
        // already link a pot to a place point. Move loose output out of the scene
        // while we finish deciding whether it belongs in a slot or inventory.
        if (transfer.Placement == null) gear.transform.position = player.transform.position + Vector3.up * 100f;
        gear.m_BeenInPlayerInventory = true;
        gear.m_InPlayerInventory = false;
        gear.m_LastUpdatedTODHours = GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused();
        gear.SkipSpawnChanceRollInitialDecayAndAutoEvolve();
        gear.ManualStart();
        gear.SetNormalizedHP(Mathf.Clamp01(transfer.Condition), false);
        ApplyCookingFood(gear, transfer.FoodState);
        RestoreFoodHeat(gear, transfer);

        var placement = transfer.Placement;
        if (placement != null && area == MyMod.level_guid && placement.Slot?.Valid == true && placement.Pose?.Valid == true)
        {
            var slot = UnityEngine.Object.FindObjectsOfType<CookingSlot>()
                .Where(candidate => candidate.m_GearPlacePoint != null)
                .OrderBy(candidate => (candidate.m_GearPlacePoint.transform.position - placement.Slot.Position).sqrMagnitude)
                .FirstOrDefault();
            if (slot?.m_GearPlacePoint is { } point &&
                (point.transform.position - placement.Slot.Position).sqrMagnitude < SlotMatchSquared &&
                (point.m_PlacedGear == gear || point.GetPlacedGear() == gear || CookingSlotVacant(point)))
            {
                if (point.m_PlacedGear != null && point.m_PlacedGear != gear) point.ResetPlacedGear();
                ReceivePot(area, transfer.SourceOwner, transfer.SourceKey, null);
                gear.transform.SetPositionAndRotation(placement.Pose.Position, placement.Pose.Rotation);
                gear.gameObject.SetActive(true);
                var attached = gear.m_CookingPotItem;
                if (point.m_PlacedGear != gear || attached?.GetGearPlacePoint() != point)
                    point.PlaceGear(gear, reposition: true);
                if (point.m_PlacedGear == gear || attached?.GetGearPlacePoint() == point)
                {
                    if (recentUse && StillAimingAtHandoff(player, gear, point) &&
                        GameCompat.Panel<Panel_ActionPicker>() is { } picker)
                        picker.ShowActionPicker(gear.gameObject);
                    MelonLoader.MelonLogger.Msg("[Cooking] Placed transferred " + transfer.Gear + " on its slot");
                    return true;
                }
            }
        }
        if (placement != null)
        {
            gear.m_CookingPotItem?.DetachFromFire();
            MelonLoader.MelonLogger.Msg("[Cooking] Placed cookware moved to recipient inventory because its slot is unavailable");
        }
        inventory.AddGear(gear, enableNotificationFlag: true);
        if (gear != null && gear.m_InPlayerInventory)
        {
            RestoreFoodHeat(gear, transfer);
            gear.gameObject.SetActive(false);
        }
        if (gear != null && !gear.m_InPlayerInventory)
        {
            RetireCookingGear(gear);
            ReceivedOutputs.Remove(transfer.Id);
            return false;
        }
        MelonLoader.MelonLogger.Msg($"[Cooking] Delivered {transfer.Gear} id={transfer.Id} condition={transfer.Condition:F3}");
        return true;
    }

    private static bool StillAimingAtHandoff(PlayerManager player, GearItem gear, GearPlacePoint point)
    {
        if (!GearPlacementSync.CanInteract(player) || CoopReturnToMenu.InProgress ||
            GameManager.GetVpFPSCamera()?.GetCameraComponent()?.transform is not { } camera) return false;
        Vector3 slotPosition = point.transform.position;
        var target = camera.position + camera.forward * Vector3.Dot(slotPosition - camera.position, camera.forward);
        if ((target - slotPosition).sqrMagnitude > .25f) return false;
        var root = player.GetInteractiveObjectUnderCrosshairs(2.5f);
        return root != null && (root == gear.gameObject || root.transform.IsChildOf(gear.transform) ||
            root.GetComponentInParent<CookingSlot>()?.m_GearPlacePoint == point);
    }

    private static void RestoreFoodHeat(GearItem gear, GearTransfer transfer)
    {
        if (gear == null || gear.m_FoodItem == null || !float.IsFinite(transfer.Heat) || transfer.Heat < 0f) return;
        gear.m_FoodItem.m_HeatPercent = transfer.Heat;
        gear.m_FoodItem.m_NumTimesHeatedUp = transfer.TimesHeated;
    }

    private static void DeliverWater(string area, WaterTransfer transfer)
    {
        if (transfer == null || !float.IsFinite(transfer.Liters) || transfer.Liters <= 0f || transfer.Liters > 10f) return;
        if (transfer.Requester == (MyMod.iAmHost ? 0 : ClientUser.myId))
        {
            var inventory = GameManager.GetInventoryComponent();
            if (MyMod.level_guid != area || inventory == null || !AcceptOutput(transfer.Id)) return;
            inventory.AddToWaterSupply(GameCompat.Liters(transfer.Liters),
                transfer.Potable ? LiquidQuality.Potable : LiquidQuality.NonPotable);
            HUDMessage.AddMessage(transfer.Potable ? "Питьевая вода добавлена в инвентарь" : "Непитьевая вода добавлена в инвентарь");
        }
        else if (MyMod.iAmHost && transfer.Requester > 0 && transfer.Requester < MyMod.playersData.Count &&
            MyMod.playersData[transfer.Requester]?.m_LevelGuid == area)
        {
            using var packet = MakeServerPacket(8, area, JsonSerializer.Serialize(transfer), 0, 0);
            ServerSend.SendUDPData(transfer.Requester, packet);
        }
    }

    private static void ReceivePot(string area, int owner, int key, Pot pot)
    {
        if (area != MyMod.level_guid || owner == (MyMod.iAmHost ? 0 : ClientUser.myId)) return;
        var identity = (owner, key);
        RemotePots.TryGetValue(identity, out var previous);
        if (pot == null)
        {
            RetireCookwareVisual(previous?.Visual);
            RemotePots.Remove(identity);
            return;
        }
        if (!ValidPot(pot)) return;
        if (previous == null && RemotePots.Count >= 128) return;
        var visual = previous?.Visual;
        var hitboxes = previous?.Hitboxes;
        if (previous != null && (previous.State.Gear != pot.Gear || previous.State.Food != pot.Food || previous.State.Meshes != pot.Meshes || previous.State.Liquid != pot.Liquid || previous.State.Mode != pot.Mode) && visual != null)
        {
            RetireCookwareVisual(visual);
            visual = null;
        }
        if (visual == null)
        {
            visual = CreatePotVisual(pot);
            hitboxes = visual != null ? visual.GetComponentsInChildren<Collider>(true) : null;
        }
        if (visual != null) visual.transform.SetPositionAndRotation(pot.Pose.Position, pot.Pose.Rotation);
        RemotePots[identity] = new RemotePot(pot, visual, hitboxes, Time.realtimeSinceStartup + 7f);
    }

    private static void RetireCookwareVisual(GameObject visual)
    {
        if (visual == null) return;
        ClearSharedCookwareInteraction(GameManager.GetPlayerManagerComponent(), visual);
        visual.SetActive(false);
        UnityEngine.Object.Destroy(visual);
    }

    private static bool InRange(float value, float max) => float.IsFinite(value) && value >= 0f && value <= max;

    private static bool ValidCookingFood(CookingFood food) => food == null || InRange(food.Condition, 1f) && InRange(food.Weight, 100f) &&
        InRange(food.Calories, 100000f) && InRange(food.TotalCalories, 100000f) && InRange(food.Heat, 100f) && food.Reheats >= 0;

    private static bool ValidPot(Pot pot) => InRange(pot.Liters, 20f) && InRange(pot.WaterDuringMelt, 20f) &&
        InRange(pot.Cooked, 1f) && InRange(pot.Elapsed, 10000f) && ValidCookingFood(pot.FoodState) &&
        IsCookingGearName(pot.Gear) && pot.Pose?.Valid == true && pot.Slot?.Valid == true &&
        pot.Meshes != null && pot.Meshes.Length <= 256 && pot.Meshes.Length % 8 == 0 && pot.Liquid >= 0 && pot.Liquid <= 2 && pot.Food != null && pot.Food.Length <= 80 && pot.Mode != null && pot.Mode.Length <= 32 && pot.NativeHover != null && pot.NativeHover.Length <= 300;

    private static string PotMeshKey(MeshRenderer renderer, Transform root)
    {
        var filter = renderer.GetComponent<MeshFilter>();
        if (filter == null || filter.sharedMesh == null) return "";
        string path = "";
        for (var part = renderer.transform; part != null && part != root; part = part.parent)
            path = part.name + "/" + path;
        return Shared.StableHash(path + "|" + filter.sharedMesh.name).ToString("X8");
    }

    private static GameObject CreatePotVisual(Pot pot)
    {
        bool bareFood = IsCookingDummyName(pot.Gear) && IsCookingGearName(pot.Food);
        var prefab = MyMod.GetGearItemObject(bareFood ? pot.Food : pot.Gear);
        if (prefab == null) return null;
        var root = new GameObject("TLD Coop cooking slot") { layer = 2 };
        var cooking = prefab.GetComponent<CookingPotItem>();
        var visible = new HashSet<string>();
        for (int i = 0; i < pot.Meshes.Length; i += 8) visible.Add(pot.Meshes.Substring(i, 8));
        int body = 0;
        foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (cooking != null && renderer == cooking.m_GrubMeshRenderer) continue;
            bool active = bareFood || visible.Count == 0
                ? renderer.enabled && ActiveBelow(renderer.transform, prefab.transform)
                : visible.Contains(PotMeshKey(renderer, prefab.transform));
            if (active && CopyCookwareMesh(renderer, prefab.transform, root.transform, targetable: true)) body++;
        }
        if (body == 0)
            foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>())
                if (renderer.enabled && (cooking == null || renderer != cooking.m_GrubMeshRenderer))
                    CopyCookwareMesh(renderer, prefab.transform, root.transform, targetable: true);

        if (cooking != null && pot.Liquid != 0 && cooking.m_GrubMeshRenderer != null)
        {
            var source = pot.Liquid == 2 ? cooking.m_SnowMesh : cooking.m_WaterMesh;
            var list = pot.Liquid == 2 ? cooking.m_MeltSnowMaterialsList :
                pot.Mode == "Ready" ? cooking.m_BoilWaterReadyMaterialsList : cooking.m_BoilWaterPotMaterialsList;
            if (source != null && list != null && list.Length != 0)
                CopyCookwareMesh(cooking.m_GrubMeshRenderer, prefab.transform, root.transform, true, source, new[] { list[0] });
        }
        else if (cooking != null && IsCookingGearName(pot.Food))
            CopyCookwareFood(pot, prefab, cooking, root);
        root.AddComponent<Comps.SharedCookwareInteraction>();
        return root;
    }

    private static bool CopyCookwareMesh(MeshRenderer source, Transform sourceRoot, Transform replica,
        bool targetable, Mesh mesh = null, Material[] materials = null)
    {
        mesh ??= source.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null) return false;
        var visual = new GameObject(source.name) { layer = 2 };
        CopyCookwareTransform(source.transform, sourceRoot, visual.transform, replica);
        visual.AddComponent<MeshFilter>().sharedMesh = mesh;
        visual.AddComponent<MeshRenderer>().sharedMaterials = materials ?? source.sharedMaterials;
        if (targetable)
        {
            // The native slot hitbox often covers the grate or edge only. Match the
            // visible item's local footprint, including openings of pots and pans.
            var collider = visual.AddComponent<BoxCollider>();
            collider.center = mesh.bounds.center;
            collider.size = Vector3.Max(mesh.bounds.size, new Vector3(.02f, .04f, .02f));
            collider.isTrigger = true;
        }
        return true;
    }

    private static void CopyCookwareFood(Pot snapshot, GameObject prefab, CookingPotItem cooking, GameObject replica)
    {
        var foodPrefab = MyMod.GetGearItemObject(snapshot.Food);
        if (foodPrefab == null) return;
        var cookable = foodPrefab.GetComponent<GearItem>()?.m_Cookable;
        var source = cooking.m_GrubMeshRenderer;
        if (cookable != null && source != null)
        {
            bool raw = snapshot.Mode == nameof(CookingPotItem.CookingState.Cooking);
            var (uncooked, cooked) = cooking.m_GrubMeshType switch
            {
                CookingPotItem.GrubMeshType.Can => (cookable.m_MeshRawCanStyle, cookable.m_MeshCanStyle),
                CookingPotItem.GrubMeshType.FryingPan => (cookable.m_MeshRawFryingPanStyle, cookable.m_MeshFryingPanStyle),
                _ => (cookable.m_MeshRawPotStyle, cookable.m_MeshPotStyle)
            };
            var mesh = raw ? uncooked ?? cooked : cooked ?? uncooked;
            var materials = snapshot.Mode == nameof(CookingPotItem.CookingState.Ruined) ? cookable.m_RuinedMaterials :
                raw && cookable.m_CookingPotRawMaterialsList?.Length > 0 ? cookable.m_CookingPotRawMaterialsList : cookable.m_CookingPotMaterialsList;
            if (mesh != null && materials != null && materials.Length > 0 &&
                CopyCookwareMesh(source, prefab.transform, replica.transform, true, mesh, materials.ToArray())) return;
            if (!raw && cookable.m_CookedPrefab != null) foodPrefab = cookable.m_CookedPrefab.gameObject;
        }

        var marker = new GameObject("Cooking food");
        marker.transform.SetParent(replica.transform, false);
        marker.transform.localPosition = source != null
            ? prefab.transform.InverseTransformPoint(source.transform.position) : Vector3.up * .04f;
        foreach (var renderer in foodPrefab.GetComponentsInChildren<MeshRenderer>(true))
            if (renderer.enabled && ActiveBelow(renderer.transform, foodPrefab.transform))
                CopyCookwareMesh(renderer, foodPrefab.transform, marker.transform, targetable: true);
    }

    private static void CopyCookwareTransform(Transform source, Transform prefab, Transform target, Transform replica)
    {
        var inverseRotation = Quaternion.Inverse(prefab.rotation);
        target.SetParent(replica, false);
        target.localPosition = inverseRotation * (source.position - prefab.position);
        target.localRotation = inverseRotation * source.rotation;
        target.localScale = source.lossyScale;
    }

    private static bool ActiveBelow(Transform child, Transform root)
    {
        for (var part = child; part != root; part = part.parent)
            if (!part.gameObject.activeSelf) return false;
        return true;
    }

    internal static void Tick()
    {
        Pathes.RestoreSafehouseControls.Tick();
        CuringStorageSync.Tick();
        SceneWaterSync.Tick();
        if (MyMod.iAmHost)
        {
            int session = Server.Generation;
            if (session != cookingSession)
            {
                cookingSession = session;
                CookingClaims.Clear();
                AuthorizedCookingTransfers.Clear();
                HostCookingGear.Clear();
            }
            float current = Time.realtimeSinceStartup;
            foreach (var operation in AuthorizedCookingTransfers.ToArray())
                if (operation.Value.Expires <= current) AuthorizedCookingTransfers.Remove(operation.Key);
            foreach (var claim in CookingClaims.ToArray())
                if (claim.Value.Expires <= current) CookingClaims.Remove(claim.Key);
        }
        TickCookingGear();
        string area = InWorld ? MyMod.level_guid : null;
        if (area != null)
        {
            var world = (WorldSaveIdentity.Active, MPSaveManager.GetSeed());
            if (nativeFurnitureWorld != world)
            {
                NativeFurnitureIds.Clear();
                Pending.Clear();
                nativeFurnitureWorld = world;
                LoadFurnitureBindings();
            }
        }
        if (area != scene)
        {
            cookwareUse = null;
            ClearSharedCookwareInteraction(GameManager.GetPlayerManagerComponent());
            foreach (var pot in RemotePots.Values)
                RetireCookwareVisual(pot.Visual);
            RemotePots.Clear();
            LocalPots.Clear();
            LocalCooking.Clear();
            LocalCookingPortions.Clear();
            CookingPoints.Clear();
            RejectedCooking.Clear();
            nextCookingScan = 0f;
            nextCookingPublish = 0f;
            PublishedPots.Clear();
            JoiningFurniture.Reset();
            snapshotKnown = null;
            furnitureReady = MyMod.iAmHost;
            placementOriginObject = null;
            placementOrigin = null;
            Unapplied.Clear();
            LatestRevision.Clear();
            RemovedFurniture.Clear();
            FurnitureApplyWarnings.Clear();
            FurnitureCreateAttempts.Clear();
            FurnitureAliases.Clear();
            SettlingFurniture.Clear();
            nextFurnitureApply = 0f;
            scene = area;
            ScheduleFurnitureReplay();
            ContainerLockSync.Prepare(area);
            SceneRepairSync.Prepare(area);
            SceneWaterSync.Prepare(area);
            nextRequest = 0;
            if (area != null)
            {
                foreach (var pot in UnityEngine.Object.FindObjectsOfType<CookingPotItem>())
                    LocalPots[pot.GetInstanceID()] = pot;
                if (MyMod.iAmHost)
                    foreach (var move in GetScene(area).Values) ReceiveMove(area, move);
            }
        }
        if (area == null) return;
        foreach (var item in SettlingFurniture.Values.ToArray())
        {
            SettlingFurniture.Remove(item.Move.Id);
            if (FurnitureSettled(item.Object)) SendFurnitureMove(item.Move);
        }
        float now = Time.realtimeSinceStartup;
        bool reportReplay = false;
        if (MyMod.iAmHost && furnitureReplays > 0 && now >= replayFurnitureAt && !PlaceableManager.s_IsDeserializing)
        {
            furnitureReplays--;
            replayFurnitureAt = now + 4f;
            FurnitureCreateAttempts.Clear();
            var savedFurniture = GetScene(area);
            foreach (var move in savedFurniture.Values)
            {
                if (Unapplied.TryGetValue(move.Id, out var queued) && queued.Revision > move.Revision) continue;
                if (move.Removed) RemovedFurniture.Add(move.Id);
                Unapplied[move.Id] = move;
            }
            MelonLoader.MelonLogger.Msg($"[Furniture] Replay scene={area} saved={savedFurniture.Count} pending={Unapplied.Count}");
            nextFurnitureApply = 0f;
            reportReplay = true;
        }
        foreach (var pair in RemotePots.ToArray())
            if (pair.Value.Expires <= now) ReceivePot(area, pair.Key.Owner, pair.Key.Key, null);
        if (furnitureReady && Unapplied.Count > 0 && now >= nextFurnitureApply)
        {
            bool progressed = false;
            var lookup = new PlacementLookup();
            foreach (var pair in Unapplied.OrderBy(pair => !(pair.Value.Removed || pair.Value.Stored))
                .ThenBy(pair => QueuedParentDepth(pair.Value)).ToArray())
            {
                try
                {
                    if (Apply(pair.Value, lookup))
                    {
                        Unapplied.Remove(pair.Key);
                        progressed = true;
                    }
                }
                catch (Exception ex)
                {
                    if (FurnitureApplyWarnings.Add(pair.Key))
                        MelonLoader.MelonLogger.Warning($"[Furniture] Apply failed id={pair.Key} scene={area} {ex.Message}");
                }
            }
            furnitureRetryDelay = progressed ? 1f : Mathf.Min(furnitureRetryDelay * 2f, 30f);
            nextFurnitureApply = now + furnitureRetryDelay;
            if (furnitureSectorsDirty)
            {
                BoxSectorManager.s_Instance?.UpdateSectors();
                furnitureSectorsDirty = false;
            }
            if (bindingsChanged) SaveFurnitureBindings();
            if (reportReplay)
            {
                var pending = Unapplied.Values.Where(move => !move.Removed).ToArray();
                MelonLoader.MelonLogger.Msg($"[Furniture] Replay result scene={area} unresolved={pending.Length} waitingOnRemovals={Unapplied.Count - pending.Length}");
                foreach (var move in pending.Take(8))
                    MelonLoader.MelonLogger.Msg($"[Furniture] Waiting id={move.Id} asset={move.AssetName} parent={move.Parent}");
            }
        }
        TickPots();
        if (MyMod.iAmHost)
        {
            Pending.Clear();
            return;
        }
        if (now < nextRequest) return;
        nextRequest = now + (furnitureReady ? 8f : 3f);
        foreach (var pair in Pending)
            if (pair.Key.Area == area || now - pair.Value.SentAt <= 30f)
                SendToHost(1, pair.Key.Area, JsonSerializer.Serialize(pair.Value.Move));
        JoiningFurniture.Reset();
        snapshotKnown = new Dictionary<string, long>(LatestRevision, StringComparer.Ordinal);
        snapshotRequest = unchecked(snapshotRequest + 1);
        SendToHost(0, area, furnitureReady ? FurnitureReplicationPolicy.Stamp(snapshotKnown) : "", snapshotRequest);
        foreach (var state in ContainerLockSync.Requests()) PublishLock(state);
        foreach (var id in SceneRepairSync.Requests(area)) PublishRepair(area, id);
    }

    internal static void ScheduleFurnitureReplay()
    {
        furnitureReplays = 2;
        replayFurnitureAt = Time.realtimeSinceStartup + 1f;
        furnitureRetryDelay = 1f;
        nextFurnitureApply = 0f;
        nextRequest = 0;
    }

    private static void LoadFurnitureBindings()
    {
        bindingsChanged = false;
        if (string.IsNullOrEmpty(nativeFurnitureWorld.Id)) return;
        string data = MPSaveManager.LoadData("LocalFurnitureIds_" + nativeFurnitureWorld.Id, nativeFurnitureWorld.Seed);
        if (string.IsNullOrEmpty(data)) return;
        try
        {
            foreach (var binding in JsonSerializer.Deserialize<FurnitureBinding[]>(data) ?? Array.Empty<FurnitureBinding>())
                if (binding != null && !string.IsNullOrEmpty(binding.Area) && !string.IsNullOrEmpty(binding.Id) &&
                    !string.IsNullOrEmpty(binding.NativeId))
                    NativeFurnitureIds[(binding.Area, binding.Id)] = binding.NativeId;
        }
        catch (JsonException ex) { MelonLoader.MelonLogger.Warning("[Furniture] Could not read local IDs: " + ex.Message); }
    }

    private static void SaveFurnitureBindings()
    {
        if (string.IsNullOrEmpty(nativeFurnitureWorld.Id)) { bindingsChanged = false; return; }
        var saved = NativeFurnitureIds.Select(entry => new FurnitureBinding(entry.Key.Area, entry.Key.Id, entry.Value)).ToArray();
        MPSaveManager.ValidateRootExits();
        bindingsChanged = !MPSaveManager.SaveData("LocalFurnitureIds_" + nativeFurnitureWorld.Id,
            JsonSerializer.Serialize(saved), nativeFurnitureWorld.Seed);
    }

    private static void FinishFurnitureSnapshot(string area)
    {
        if (!JoiningFurniture.TryRead(out var records) || snapshotKnown == null) return;
        var included = records.Select(move => move.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in snapshotKnown)
            if (!included.Contains(entry.Key) && LatestRevision.TryGetValue(entry.Key, out long current) && current == entry.Value)
            {
                LatestRevision.Remove(entry.Key);
                RemovedFurniture.Remove(entry.Key);
                Unapplied.Remove(entry.Key);
            }
        foreach (var move in records) ReceiveMove(area, move);
        snapshotKnown = null;
        furnitureReady = true;
    }

    [HarmonyPatch(typeof(PlaceableManager), nameof(PlaceableManager.DeserializeScenePlacements))]
    private static class ReplayAfterNativeFurnitureLoad
    {
        private static void Postfix()
        {
            LatestRevision.Clear();
            RemovedFurniture.Clear();
            FurnitureAliases.Clear();
            ScheduleFurnitureReplay();
        }
    }

    internal static void ReceiveClient(int from, Packet packet)
    {
        byte kind = packet.ReadByte();
        string area = packet.ReadString();
        int key = packet.ReadInt();
        string data = packet.ReadString();
        if (from <= 0 || from >= MyMod.playersData.Count || MyMod.playersData[from] is not { } sender) return;
        bool recentFurnitureMove = kind == 1 && sender.m_PreviousScene == area &&
            Environment.TickCount64 - sender.m_SceneChangedAt <= 30_000;
        if ((kind is not (9 or 18 or 22 or 25) && sender.m_LevelGuid != area && !recentFurnitureMove) ||
            !WildlifeSpawnAuthorityPolicy.WorldScene(area) || area.Length > 160 ||
            area.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || data.Length > SceneMessageLimits.PayloadLimit(kind)) return;
        try
        {
            if (kind == 0)
            {
                var records = GetScene(area);
                string stamp = FurnitureReplicationPolicy.Stamp(records.Select(entry =>
                    new KeyValuePair<string, long>(entry.Key, entry.Value.Revision)));
                if (data != stamp)
                {
                    foreach (var move in records.Values)
                    {
                        using var reply = MakeServerPacket(19, area, JsonSerializer.Serialize(move), 0, key);
                        ServerSend.SendUDPData(from, reply);
                    }
                    using var complete = MakeServerPacket(20, area, stamp, 0, key);
                    ServerSend.SendUDPData(from, complete);
                }
                foreach (var id in SceneRepairSync.Snapshot(area))
                {
                    using var reply = MakeServerPacket(11, area, id, 0, 0);
                    ServerSend.SendUDPData(from, reply);
                }
                foreach (var water in SceneWaterSync.Snapshot(area))
                {
                    using var reply = MakeServerPacket(12, area, JsonSerializer.Serialize(water), 0, 0);
                    ServerSend.SendUDPData(from, reply);
                }
                using var waterReady = MakeServerPacket(13, area, "", 0, 0);
                ServerSend.SendUDPData(from, waterReady);
            }
            else if (kind is 17 or 18) CuringStorageSync.ReceiveClient(kind, from, area, data);
            else if (kind == 14) GearPlacementSync.Accept(area, key, data, from);
            else if (kind == 22) AcceptWater(area, JsonSerializer.Deserialize<SceneWaterSync.Withdrawal>(data),
                from, MyMod.playersData[from]?.m_LevelGuid == area);
            else if (kind == 11 && SceneRepairSync.Accept(area, data)) SendToPeers(11, area, data);
            else if (kind == 10) DeathRespawn.ReceiveClient(from, area, key, data);
            else if (kind == 1) AcceptMove(area, JsonSerializer.Deserialize<Move>(data), from);
            else if (kind == 2)
            {
                var pot = JsonSerializer.Deserialize<Pot>(data);
                AcceptCookingState(area, from, key, pot, data);
            }
            else if (kind == 4) RequestCookwareRelease(from, key, area, JsonSerializer.Deserialize<CookingRequest>(data));
            else if (kind == 8) DeliverWater(area, JsonSerializer.Deserialize<WaterTransfer>(data));
            else if (kind == 9) AcceptCookingGear(area, from, JsonSerializer.Deserialize<GearTransfer>(data), data);
            else if (kind == 25 && Guid.TryParseExact(data, "N", out var receipt) &&
                HostCookingGear.TryGetValue(receipt, out var delivery) && delivery.Scene == area &&
                delivery.Gear.Requester == from && delivery.Recipient == Server.GetMACByID(from))
                HostCookingGear[receipt] = delivery with { Data = null };
            else if (kind == 7)
            {
                var state = ContainerLockSync.Accept(area, JsonSerializer.Deserialize<ContainerLockSync.State>(data));
                if (state != null) SendToPeers(7, area, JsonSerializer.Serialize(state));
            }
            else if (kind == 5 && MyMod.playersData[from].m_LevelGuid == area)
            {
                if (area != MyMod.level_guid || ThrownGearSync.Receive(area, from, key, data))
                    SendToPeers(5, area, data, from, key);
            }
        }
        catch (JsonException) { }
    }

    internal static void Receive(Packet packet)
    {
        byte kind = packet.ReadByte();
        string area = packet.ReadString();
        int owner = packet.ReadInt();
        int key = packet.ReadInt();
        string data = packet.ReadString();
        if (data.Length > SceneMessageLimits.PayloadLimit(kind)) return;
        if (kind == 24)
        {
            CuringStorageSync.Acknowledge(data);
            return;
        }
        if (kind == 23)
        {
            try { SceneWaterSync.ReceiveGrant(JsonSerializer.Deserialize<SceneWaterSync.Grant>(data)); }
            catch (JsonException) { }
            return;
        }
        if (kind == 3)
        {
            foreach (var pair in Pending.ToArray())
                if (pair.Key.Area == area && pair.Value.Move.Operation == data) Pending.Remove(pair.Key);
            return;
        }
        if (kind == 25)
        {
            if (Guid.TryParseExact(data, "N", out var receipt)) OutgoingCookingGear.Remove(receipt);
            return;
        }
        if (!InWorld || (kind != 9 && area != MyMod.level_guid)) return;
        try
        {
            if (kind == 17) CuringStorageSync.Receive(data);
            else if (kind == 14 || kind == 15) GearPlacementSync.Receive(key, data, reply: kind == 15);
            else if (kind == 16) GearPlacementSync.MissingPreview(key);
            else if (kind == 10) DeathRespawn.Receive(area, key, data);
            else if (kind == 11) SceneRepairSync.Receive(area, data);
            else if (kind == 12) SceneWaterSync.Receive(area, JsonSerializer.Deserialize<SceneWaterSync.Level>(data));
            else if (kind == 13) SceneWaterSync.SnapshotComplete(area);
            else if (kind == 19 && key == snapshotRequest && snapshotKnown != null)
            {
                var move = JsonSerializer.Deserialize<Move>(data);
                if (HasFurniturePose(move) && !move.RegisterOnly && !string.IsNullOrEmpty(move.Id))
                    JoiningFurniture.Add(move.Id, move.Revision, move);
                FinishFurnitureSnapshot(area);
            }
            else if (kind == 20 && key == snapshotRequest && snapshotKnown != null)
            {
                JoiningFurniture.Complete(data);
                FinishFurnitureSnapshot(area);
            }
            else if (kind == 21)
            {
                var move = JsonSerializer.Deserialize<Move>(data);
                if (HasFurniturePose(move) && !string.IsNullOrEmpty(move.Id) && move.Revision >= LatestRevision.GetValueOrDefault(move.Id))
                {
                    LatestRevision[move.Id] = move.Revision;
                    Unapplied[move.Id] = move;
                    furnitureRetryDelay = 1f;
                    nextFurnitureApply = 0f;
                    HUDMessage.AddMessage("Мебель уже изменил другой игрок");
                }
            }
            else if (kind == 1) ReceiveMove(area, JsonSerializer.Deserialize<Move>(data));
            else if (kind == 2) ReceivePot(area, owner, key, JsonSerializer.Deserialize<Pot>(data));
            else if (kind == 4 && JsonSerializer.Deserialize<CookingRequest>(data) is { } release && release.Owner == ClientUser.myId)
                ReleaseCookware(key, release, owner);
            else if (kind == 8) DeliverWater(area, JsonSerializer.Deserialize<WaterTransfer>(data));
            else if (kind == 9 && JsonSerializer.Deserialize<GearTransfer>(data) is { } output && DeliverGear(area, output))
                SendToHost(25, area, output.Id.ToString("N"));
            else if (kind == 6) RejectCooking(key);
            else if (kind == 7) ContainerLockSync.Receive(JsonSerializer.Deserialize<ContainerLockSync.State>(data));
            else if (kind == 5 && owner != ClientUser.myId) ThrownGearSync.Receive(area, owner, key, data);
        }
        catch (JsonException) { }
    }

    internal static void SendRescueToHost(int subject, string data) => SendToHost(10, MyMod.level_guid, data, subject);


    internal static void SendCuring(byte kind, string area, string data) => SendToHost(kind, area, data);

    internal static void ReplyCuring(int guest, string area, string data)
    {
        using var packet = MakeServerPacket(17, area, data, 0, 0);
        ServerSend.SendUDPData(guest, packet);
    }

    internal static void AcknowledgeCuring(int guest, string area, Guid token)
    {
        using var packet = MakeServerPacket(24, area, token.ToString("N"), 0, 0);
        ServerSend.SendUDPData(guest, packet);
    }

    internal static void SendDroppedMove(string area, int key, string data) => SendToHost(14, area, data, key);

    internal static void BroadcastDroppedMove(string area, int key, string data) => SendToPeers(14, area, data, key: key);

    internal static void ReplyDroppedMove(int guest, string area, int key, string data)
    {
        using var packet = MakeServerPacket(15, area, data, guest, key);
        ServerSend.SendUDPData(guest, packet);
    }

    internal static void ReplyMissingPreview(int guest, string area, int key)
    {
        using var packet = MakeServerPacket(16, area, "", guest, key);
        ServerSend.SendUDPData(guest, packet);
    }

    internal static void RequestWater(string area, string data) => SendToHost(22, area, data);

    internal static SceneWaterSync.Grant AcceptWater(string area, SceneWaterSync.Withdrawal request, int sender, bool inArea)
    {
        var grant = SceneWaterSync.Accept(area, request, inArea, out var level);
        if (grant == null) return null;
        if (level != null) SendToPeers(12, area, JsonSerializer.Serialize(level));
        if (sender > 0)
        {
            using var reply = MakeServerPacket(23, area, JsonSerializer.Serialize(grant), 0, 0);
            ServerSend.SendUDPData(sender, reply);
        }
        return grant;
    }

    internal static void BroadcastDowned(string area, int subject, string data) => SendToPeers(10, area, data, key: subject);

    internal static void SendRescueToGuest(int guest, string area, int subject, string data)
    {
        using var packet = MakeServerPacket(10, area, data, 0, subject);
        ServerSend.SendUDPData(guest, packet);
    }

    private static void SendToHost(byte kind, string area, string data, int key = 0)
    {
        using var packet = new Packet(ClientPacket);
        packet.Write(kind);
        packet.Write(area);
        packet.Write(key);
        packet.Write(data);
        MyMod.SendUDPData(packet);
    }

    internal static void PublishLock(ContainerLockSync.State state)
    {
        string area = MyMod.level_guid;
        if (MyMod.iAmHost)
        {
            var accepted = ContainerLockSync.Accept(area, state, fromHost: true);
            if (accepted != null) SendToPeers(7, area, JsonSerializer.Serialize(accepted));
        }
        else SendToHost(7, area, JsonSerializer.Serialize(state));
    }

    internal static void PublishRepair(string area, string id)
    {
        if (!MyMod.iAmHost) SendToHost(11, area, id);
        else if (SceneRepairSync.Accept(area, id)) SendToPeers(11, area, id);
    }

    internal static void PublishThrownFlight(int key, string data)
    {
        if (MyMod.iAmHost) SendToPeers(5, MyMod.level_guid, data, 0, key);
        else if (MyMod.sendMyPosition) SendToHost(5, MyMod.level_guid, data, key);
    }

    private static Packet MakeServerPacket(byte kind, string area, string data, int owner, int key)
    {
        var packet = new Packet(ServerPacket);
        packet.Write(kind);
        packet.Write(area);
        packet.Write(owner);
        packet.Write(key);
        packet.Write(data);
        return packet;
    }

    private static void SendToPeers(byte kind, string area, string data, int owner = 0, int key = 0)
    {
        using var packet = MakeServerPacket(kind, area, data, owner, key);
        ServerSend.SendUDPDataToAll(packet, area);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using GameServer;
using HarmonyLib;
using Il2Cpp;
using Il2CppTLD.IntBackedUnit;
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
    private sealed record Move(string Id, string Parent, Pose Pose, string Operation, long Revision, int Version = 0, string Anchor = "", string ParentAnchor = "", string Asset = "", string AssetName = "", float Sx = 1f, float Sy = 1f, float Sz = 1f, bool RegisterOnly = false, bool Removed = false, string Visible = "", string PreviousId = "", Pose[] History = null, long BaseRevision = -1, Pose Origin = null);
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
    private sealed record Pot(string Gear, Pose Pose, Pose Slot, string Meshes, string Food, string Mode, int Minutes, bool CanTake, int Liquid, string NativeHover, float Liters = 0f);
    private sealed record GearTransfer(Guid Id, int Requester, string Gear, string Json, float Heat, int TimesHeated);
    private sealed record WaterTransfer(Guid Id, int Requester, float Liters, bool Potable);
    private static readonly Dictionary<Guid, float> ReceivedOutputs = new();
    private sealed record RemotePot(Pot State, GameObject Visual, Collider[] Hitboxes, float Expires);
    private static readonly Dictionary<string, Dictionary<string, Move>> Saved = new(StringComparer.Ordinal);
    // LoadData migrates old Unity vectors by lowercasing X, Y and Z before returning JSON.
    private static readonly JsonSerializerOptions FurnitureSaveOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<(string Area, string Id), Move> Pending = new();
    private static readonly FurnitureSnapshot<Move> JoiningFurniture = new();
    private static Dictionary<string, long> snapshotKnown;
    private static int snapshotRequest;
    private static bool furnitureReady;
    private static GameObject placementOriginObject;
    private static Pose placementOrigin;
    private static long placementRevision;
    private static readonly Dictionary<string, Move> Unapplied = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LatestRevision = new(StringComparer.Ordinal);
    private static readonly HashSet<string> AcceptedOperations = new(StringComparer.Ordinal);
    private static readonly Queue<string> OperationOrder = new();
    private static readonly Dictionary<int, CookingPotItem> LocalPots = new();
    private static readonly Dictionary<int, GearPlacePoint> CookingPoints = new();
    private static readonly Dictionary<int, (GearPlacePoint Point, GearItem Gear, CookingPotItem Pot)> LocalCooking = new();
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
        string anchor = Anchor(obj);
        return anchor.Length == 0 ? "" : "anchor-" + anchor;
    }

    internal static string FurnitureIdentity(GameObject obj)
    {
        var placeable = obj.GetComponentInParent<Placeable>();
        if (placeable != null) return Id(placeable.gameObject);
        var decoration = obj.GetComponentInParent<DecorationItem>();
        return Id(decoration != null ? decoration.gameObject : obj);
    }

    private static string Anchor(GameObject obj)
    {
        var decoration = obj != null ? obj.GetComponent<DecorationItem>() : null;
        return decoration != null && OriginalAnchors.TryGetValue(decoration.GetInstanceID(), out string key) ? key : "";
    }

    private static readonly Dictionary<int, string> OriginalAnchors = new();

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.Awake))]
    private static class RememberOriginalFurniture
    {
        private static void Prefix(DecorationItem __instance)
        {
            var position = __instance.transform.position;
            if (!__instance.gameObject.scene.IsValid() || position == Vector3.zero) return;
            string identity = FormattableString.Invariant(
                $"{GameCompat.StripCloneSuffix(__instance.name)}|{position.x:F2}|{position.y:F2}|{position.z:F2}");
            OriginalAnchors[__instance.GetInstanceID()] = Shared.StableHash(identity).ToString("X8");
        }
    }

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.OnDestroy))]
    private static class ForgetFurnitureAnchor
    {
        private static void Prefix(DecorationItem __instance) => OriginalAnchors.Remove(__instance.GetInstanceID());
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
        if (string.IsNullOrEmpty(id)) return null;
        if (FurnitureAliases.TryGetValue(id, out var alias) && alias != null) return alias;
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
        var obj = ObjectGuidManager.Lookup(id);
        if (DecorationItem.TryGetDecorationFromId(id, out var decoration) && decoration != null) return decoration.gameObject;
        lookup ??= new PlacementLookup();
        var placeable = lookup.Find(id);
        if (placeable != null) return placeable.gameObject;
        if (NativeFurnitureIds.TryGetValue((MyMod.level_guid, id), out string nativeId) &&
            !LatestRevision.ContainsKey(nativeId) && lookup.Find(nativeId) is { } known && known != null)
            return known.gameObject;
        if (obj != null && (obj.GetComponent<DecorationItem>() != null || obj.GetComponent<Placeable>() != null)) return obj;
        if (!string.IsNullOrEmpty(anchor))
        {
            GameObject match = null;
            foreach (var item in UnityEngine.Object.FindObjectsOfType<DecorationItem>(true))
            {
                if (item == null || Anchor(item.gameObject) != anchor) continue;
                if (match != null) return null;
                match = item.gameObject;
            }
            return match;
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
        if (placingFurniture != null) return;
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
        string parent = decoration != null && decoration.DecorationParent != null ? Id(decoration.DecorationParent.gameObject) : "";
        var move = new Move(id, parent, Pose.From(obj.transform), Guid.NewGuid().ToString("N"), 0, 2,
            Anchor(obj), Anchor(decoration?.DecorationParent?.gameObject), Visible: CaptureVisibleMeshes(obj));
        var placeable = obj.GetComponent<Placeable>();
        if (placeable == null || IsFixedBed(obj) || !Guid.TryParseExact(placeable.m_Addressable?.AssetGUID, "N", out _)) return move;
        var scale = obj.transform.localScale;
        return move with { Anchor = "", Asset = placeable.m_Addressable.AssetGUID, AssetName = obj.name, Sx = scale.x, Sy = scale.y, Sz = scale.z };
    }

    [HarmonyPatch(typeof(DecorationItem), nameof(DecorationItem.OnBreakdown))]
    private static class DecorationRemoved
    {
        private static void Prefix(DecorationItem __instance, out Move __state) => __state = CaptureFurniture(__instance.gameObject);

        private static void Postfix(Move __state, bool __runOriginal)
        {
            if (__runOriginal && __state != null)
                PublishMove(__state with { Removed = true, Parent = "", ParentAnchor = "", Visible = "" });
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
        return move == null ? null : () => PublishMove(move with { Removed = true, Parent = "", ParentAnchor = "", Visible = "" });
    }

    private static void PublishMove(Move move, GameObject source = null)
    {
        if (move == null) return;
        if (source != null && source == placementOriginObject && move.BaseRevision < 0)
            move = move with { Origin = placementOrigin, BaseRevision = placementRevision };
        if (move.BaseRevision < 0)
            move = move with { BaseRevision = CurrentFurnitureRevision(string.IsNullOrEmpty(move.PreviousId) ? move.Id : move.PreviousId) };
        if (source != null && !move.Removed && !string.IsNullOrEmpty(move.Asset))
        {
            SettlingFurniture[move.Id] = (move, source);
            return;
        }
        SendFurnitureMove(move);
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
            Pending[(MyMod.level_guid, move.Id)] = move;
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
            if (__0.activeInHierarchy && placeable != null && !placeable.m_Invalidated &&
                PlaceableManager.GetPlacementState(placeable) is not (PlacementState.OnPlayer or PlacementState.Removed))
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
            var moves = obj.GetComponentsInChildren<DecorationItem>(true)
                .Select(item => CaptureFurniture(item.gameObject)).Where(move => move != null).ToList();
            if (__instance.m_DecorationItemChain != null)
                foreach (var item in __instance.m_DecorationItemChain)
                {
                    var move = item.Item != null ? CaptureFurniture(item.Item.gameObject) : null;
                    if (move != null && !moves.Any(existing => existing.Id == move.Id)) moves.Add(move);
                }
            var rootMove = CaptureFurniture(obj);
            if (rootMove != null && !moves.Any(existing => existing.Id == rootMove.Id)) moves.Insert(0, rootMove);
            __state = new FurniturePlacement { Object = obj, Moves = moves.ToArray() };
            foreach (var move in moves)
            {
                var source = rootMove?.Id == move.Id ? obj : Find(move.Id, move.Anchor);
                if (source != null) __state.Objects[move.Id] = source;
            }
            placingFurniture = __state;
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
                bool root = obj == __state.Object && ReferenceEquals(obj, __state.Object);
                if (root && __state.Replacement != null)
                {
                    obj = __state.Replacement;
                    FurnitureAliases[captured.Id] = obj;
                }
                if (obj == null) obj = Find(captured.Id, captured.Anchor);
                var decoration = obj != null ? obj.GetComponent<DecorationItem>() : null;
                string parent = decoration?.DecorationParent != null ? Id(decoration.DecorationParent.gameObject) : "";
                var parentAnchor = Anchor(decoration?.DecorationParent?.gameObject);
                var pose = root || obj == null || obj.transform.position == Vector3.zero ? captured.Pose : Pose.From(obj.transform);
                var current = obj != null ? CaptureFurniture(obj) : null;
                PublishMove((current ?? captured) with { Parent = parent, ParentAnchor = parentAnchor, Pose = pose,
                    Origin = root && __state.Object == placementOriginObject ? placementOrigin : null,
                    BaseRevision = root && __state.Object == placementOriginObject ? placementRevision : -1,
                    PreviousId = current != null && current.Id != captured.Id ? captured.Id : "" }, obj);
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
            __state = __0 && placingFurniture == null ? CaptureFurniture(__instance.gameObject) : null;
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
        return moves;
    }

    private static void AcceptMove(string area, Move move, int sender = 0)
    {
        if (!HasFurniturePose(move) || string.IsNullOrEmpty(move.Id) || move.Id.Length > 100 ||
            (move.Anchor?.Length ?? 0) > 8 || (move.ParentAnchor?.Length ?? 0) > 8 ||
            move.Parent == null || move.Parent.Length > 100 || move.Id == move.Parent || move.Version != 2 ||
            (move.Visible?.Length ?? 0) > 192 || (move.PreviousId?.Length ?? 0) > 100 ||
            !Guid.TryParseExact(move.Operation, "N", out _) || !ValidFurnitureAsset(move)) return;
        var moves = GetScene(area);
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
            if (!FurnitureReplicationPolicy.CanCommit(move.BaseRevision, previous?.Revision ?? 0))
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
            (move.History?.Length ?? 0) > FurnitureHistoryLimit) return;
        if (LatestRevision.TryGetValue(move.Id, out long latest) && latest >= move.Revision) return;
        LatestRevision[move.Id] = move.Revision;
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

    private static bool SameFurniturePose(Pose left, Pose right) =>
        left?.Valid == true && right?.Valid == true &&
        (left.Position - right.Position).sqrMagnitude <= .0001f &&
        Mathf.Abs(Quaternion.Dot(left.Rotation, right.Rotation)) >= .9999f;

    private static Pose[] FurnitureHistory(Move move, Move previous)
    {
        if (move.Removed || string.IsNullOrEmpty(move.Asset)) return null;
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
        if (move.Removed)
        {
            if (obj == null) return false;
            var activePlayer = GameManager.GetPlayerManagerComponent();
            if (activePlayer != null && (activePlayer.m_ObjectToPlace == obj || activePlayer.m_ObjectToPlaceDecorationItem?.gameObject == obj)) return false;
            var removedDecoration = obj.GetComponent<DecorationItem>();
            if (removedDecoration != null && obj.activeSelf)
            {
                BoxSectorManager.s_Instance?.RemoveDecorationFromSystem(removedDecoration);
                removedDecoration.RemoveFromHierarchy();
                furnitureSectorsDirty = true;
            }
            obj.SetActive(false);
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
        if (player != null && player.m_ObjectToPlace == obj) return false;
        var decoration = obj.GetComponent<DecorationItem>();
        if (decoration == null && placeable == null) return true;
        GameObject parent = null;
        bool awaitingParent = false;
        if (decoration != null && !fixedBed)
        {
            parent = Find(move.Parent, move.ParentAnchor, lookup);
            awaitingParent = move.Parent.Length > 0 && (parent == null || !parent.activeInHierarchy);
        }
        var sectors = BoxSectorManager.s_Instance;
        bool changed = (obj.transform.position - move.Pose.Position).sqrMagnitude > .000001f ||
            Mathf.Abs(Quaternion.Dot(obj.transform.rotation, move.Pose.Rotation)) < .99999f;
        var parentDecoration = parent != null ? parent.GetComponent<DecorationItem>() : null;
        bool changeParent = decoration != null && !fixedBed &&
            (awaitingParent ? decoration.DecorationParent != null && !decoration.DecorationParent.gameObject.activeInHierarchy :
                parentDecoration != decoration.DecorationParent && (parent == null || !parent.transform.IsChildOf(obj.transform)));
        bool repairSectors = changed || changeParent || (!awaitingParent &&
            !obj.GetComponentsInChildren<Renderer>(true).Any(VisibleRenderer));
        if (repairSectors && sectors != null && decoration != null)
            sectors.RemoveDecorationFromSystem(decoration);
        if (changeParent) decoration.SetParent(awaitingParent ? null : parentDecoration);
        obj.transform.SetPositionAndRotation(move.Pose.Position, move.Pose.Rotation);
        if (!string.IsNullOrEmpty(move.Asset)) FurnitureAliases[move.Id] = obj;
        if (repairSectors && sectors != null && decoration != null)
        {
            sectors.MaybeAddDecorationIntoSystem(decoration);
            furnitureSectorsDirty = true;
        }
        int restored = RestoreFurnitureMeshes(obj, move.Visible);
        if (restored > 0) MelonLoader.MelonLogger.Msg($"[Furniture] Restored {restored} mesh renderers for {obj.name} id={move.Id}");
        return !awaitingParent;
    }

    private static bool IsFurnitureMatch(Move move, Pose current) =>
        SameFurniturePose(move.Pose, current) || move.History?.Any(prior => SameFurniturePose(prior, current)) == true;

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
            bool canTake = pot == null || CanTakeFromPot(pot);
            PublishedPots.Add(key);
            var slotPose = Pose.From(point.transform);
            var gearPose = (gear.transform.position - point.transform.position).sqrMagnitude < .5625f ? Pose.From(gear.transform) : slotPose;
            PublishPot(key, new Pot(name, gearPose, slotPose, visible.ToString(),
                food != null ? food.GetGearName() : "", pot?.GetCookingState().ToString() ?? "",
                pot != null && float.IsFinite(pot.m_MinutesUntilCooked) ? Mathf.CeilToInt(pot.m_MinutesUntilCooked) : 0, canTake, liquid, pot?.GetHoverText() ?? "",
                liquid == 2 ? pot.m_LitersSnowBeingMelted.ToLiters() : pot?.m_LitersWaterBeingBoiled.ToLiters() ?? 0f));
        }
        foreach (int key in PublishedPots.ToArray())
            if (!LocalCooking.ContainsKey(key) && PublishedPots.Remove(key)) PublishPot(key, null);
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
        return point != null && point.GetPlacedGear() == null ? FindRemotePot(point) : null;
    }

    internal static GameObject AimCookware(float range, RaycastHit worldHit = default)
    {
        if (RemotePots.Count == 0) return null;
        var camera = GameManager.GetVpFPSCamera()?.GetCameraComponent();
        if (camera == null) return null;
        var ray = new Ray(camera.transform.position, camera.transform.forward);
        if (worldHit.collider == null)
            Physics.Raycast(ray, out worldHit, range, Utils.m_PhysicalCollisionLayerMask, QueryTriggerInteraction.Ignore);
        float nearest = worldHit.collider != null
            ? Mathf.Min(range, Vector3.Dot(worldHit.point - ray.origin, ray.direction) + .025f) : range;
        return AimCookware(ray, nearest);
    }

    private static GameObject AimCookware(Ray ray, float nearest)
    {
        GameObject selected = null;
        foreach (var remote in RemotePots.Values)
        {
            if (remote.Hitboxes == null) continue;
            foreach (var collider in remote.Hitboxes)
            {
                if (collider == null || !collider.Raycast(ray, out var hit, nearest)) continue;
                selected = remote.Visual;
                nearest = hit.distance;
            }
        }
        return selected;
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
            if (__instance.m_GearPlacePoint?.GetPlacedGear() != null) return;
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
            if (__instance.m_GearPlacePoint?.GetPlacedGear() != null ||
                FindRemotePot(__instance.m_GearPlacePoint) is not { } entry) return true;
            __result = OpenCookingActions(__instance.gameObject);
            return false;
        }
    }

    private sealed record CookingInspection(string Scene, int Owner, int Key, int Action, GameObject Root, GearItem Food);
    private static CookingInspection inspection;

    internal static bool OpenCookingActions(GameObject target)
    {
        if (FindRemoteCookware(target) is not { } entry) return false;
        if (inspection != null) return true;
        var state = entry.Value.State;
        var player = GameManager.GetPlayerManagerComponent();
        var prefab = MyMod.GetGearItemObject(state.Gear);
        if (player == null || prefab == null || !state.CanTake) return true;
        var root = UnityEngine.Object.Instantiate(prefab);
        root.transform.position = state.Pose.Position;
        foreach (var collider in root.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
        var pot = root.GetComponent<CookingPotItem>();
        if (pot == null)
        {
            if (root.GetComponent<GearItem>() != null)
            {
                UnityEngine.Object.Destroy(root);
                RequestSharedCookware(entry.Key.Owner, entry.Key.Key, TakeAction(state));
                return true;
            }
            UnityEngine.Object.Destroy(root);
            return true;
        }
        pot.enabled = false;
        pot.m_MinutesUntilCooked = state.Minutes;
        if (Enum.TryParse<CookingPotItem.CookingState>(state.Mode, out var mode)) pot.m_CookingState = mode;
        pot.m_GearItem = root.GetComponent<GearItem>();
        pot.m_LitersSnowBeingMelted = GameCompat.Liters(state.Liquid == 2 ? state.Liters : 0f);
        pot.m_LitersWaterBeingBoiled = GameCompat.Liters(state.Liquid == 1 ? state.Liters : 0f);
        var foodPrefab = state.Food.Length == 0 ? null : MyMod.GetGearItemObject(state.Food);
        GearItem food = null;
        if (foodPrefab != null)
        {
            var foodObject = UnityEngine.Object.Instantiate(foodPrefab, root.transform);
            foreach (var collider in foodObject.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            food = foodObject.GetComponent<GearItem>();
            pot.m_GearItemBeingCooked = food;
        }
        inspection = new CookingInspection(MyMod.level_guid, entry.Key.Owner, entry.Key.Key, TakeAction(state), root, food);
        try
        {
            if (inspection.Action == 1) player.EnterInspectGearModeFromCookingPot(root.GetComponent<GearItem>(), pot);
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

    private static void RequestSharedCookware(int owner, int key, int action)
    {
        if (MyMod.iAmHost) RequestCookwareRelease(0, owner, key, MyMod.level_guid, action);
        else SendToHost(4, MyMod.level_guid, $"{owner}:{action}", key);
    }

    private static void EndCookingInspection()
    {
        var current = inspection;
        inspection = null;
        if (current?.Food != null) UnityEngine.Object.Destroy(current.Food.gameObject);
        if (current?.Root != null) UnityEngine.Object.Destroy(current.Root);
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
            var current = inspection;
            if (current == null) return true;
            __instance.ExitInspectGearMode(true);
            if (current.Scene == MyMod.level_guid)
            {
                RequestSharedCookware(current.Owner, current.Key, current.Action);
            }
            return false;
        }
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
        private static void Postfix() => EndCookingInspection();
    }

    private static int TakeAction(Pot state) => state.Liquid == 2 ? 4 : state.Liquid == 1 ? 3 : state.Food.Length > 0 ? 2 : 1;

    private static void RequestCookwareRelease(int requester, int owner, int key, string area, int action)
    {
        if (owner == requester || owner < 0 ||
            (owner == 0 ? MyMod.level_guid != area :
             owner >= MyMod.playersData.Count || MyMod.playersData[owner]?.m_LevelGuid != area)) return;
        if (!CookingClaims.TryGetValue((area, owner, key), out var claim) || !claim.State.CanTake || TakeAction(claim.State) != action) return;
        if (owner == 0) ReleaseCookware(key, action, requester);
        else
        {
            using var packet = MakeServerPacket(4, area, action.ToString(), requester, key);
            ServerSend.SendUDPData(owner, packet);
        }
    }

    private static void ReleaseCookware(int key, int expectedAction = 0, int requester = -1)
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
        if (expectedAction != 0 && currentAction != expectedAction) return;
        if (snow || water > 0f)
        {
            var state = pot.GetCookingState();
            pot.m_LitersSnowBeingMelted = GameCompat.Liters(0);
            pot.m_LitersWaterBeingBoiled = GameCompat.Liters(0);
            pot.UpdateVisuals();
            if (!snow && state != CookingPotItem.CookingState.Ruined)
            {
                int recipient = requester >= 0 ? requester : MyMod.iAmHost ? 0 : ClientUser.myId;
                var transfer = new WaterTransfer(Guid.NewGuid(), recipient, water, state == CookingPotItem.CookingState.Ready);
                if (MyMod.iAmHost) DeliverWater(MyMod.level_guid, transfer);
                else SendToHost(8, MyMod.level_guid, JsonSerializer.Serialize(transfer));
            }
        }
        else if (pot != null && pot.m_GearItemBeingCooked != null)
        {
            var raw = pot.m_GearItemBeingCooked;
            var cookingState = pot.GetCookingState();
            bool cancelRaw = cookingState == CookingPotItem.CookingState.Cooking;
            bool hot = cookingState == CookingPotItem.CookingState.Ready && pot.IsItemBeingCookedHot();
            if (!cancelRaw) pot.MaybeSpawnGearItemForInspectMode();
            var source = cancelRaw ? raw : pot.GetGearItemForInspectMode() ?? raw;
            if (source == null || source == pot.m_GearItem) return;
            var output = UnityEngine.Object.Instantiate(source.gameObject, point.transform.position, source.transform.rotation).GetComponent<GearItem>();
            if (cookingState == CookingPotItem.CookingState.Ready && output.m_FoodItem != null &&
                (hot || output.m_FoodItem.m_HeatedWhenCooked) && output.m_FoodItem.m_HeatPercent <= 0f)
                pot.SetHeatPercentWhenCooked(output.m_FoodItem);
            if (cancelRaw && !pot.IsDummyPot()) pot.DestroyGearItemBeingCooked();
            else pot.PickUpCookedGearItem(addToInventory: false);
            if ((pot != null && pot.m_GearItemBeingCooked == raw) || (source != null && source.m_InPlayerInventory))
            {
                UnityEngine.Object.Destroy(output.gameObject);
                return;
            }
            if (source != null)
            {
                if (point.GetPlacedGear() == source) point.ResetPlacedGear();
                UnityEngine.Object.Destroy(source.gameObject);
            }
            TransferCookingOutput(output, point, requester);
        }
        else
        {
            if (pot != null)
            {
                LocalPots.Remove(pot.GetInstanceID());
                pot.DetachFromFire();
            }
            if (point.GetPlacedGear() == gear) point.ResetPlacedGear();
            LocalCooking.Remove(key);
            PublishedPots.Remove(key);
            PublishPot(key, null);
            TransferCookingOutput(gear, point, requester);
        }
    }

    private static void TransferCookingOutput(GearItem gear, GearPlacePoint point, int requester)
    {
        if (requester >= 0)
        {
            string json = gear.SerializeToString();
            var food = gear.m_FoodItem;
            var transfer = new GearTransfer(Guid.NewGuid(), requester, gear.GetGearName(), json,
                food != null && float.IsFinite(food.m_HeatPercent) ? food.m_HeatPercent : -1f, food?.m_NumTimesHeatedUp ?? 0);
            string data = JsonSerializer.Serialize(transfer);
            if (!string.IsNullOrEmpty(json) && data.Length <= 10000)
            {
                if (MyMod.iAmHost) DeliverGear(MyMod.level_guid, transfer, data);
                else SendToHost(9, MyMod.level_guid, data);
                UnityEngine.Object.Destroy(gear.gameObject);
                return;
            }
        }
        DropCookingOutput(gear, point, requester);
    }

    private static bool AcceptOutput(Guid id)
    {
        if (id == Guid.Empty) return false;
        float now = Time.realtimeSinceStartup;
        foreach (var entry in ReceivedOutputs.ToArray())
            if (entry.Value <= now) ReceivedOutputs.Remove(entry.Key);
        return ReceivedOutputs.TryAdd(id, now + 30f);
    }

    private static void DeliverGear(string area, GearTransfer transfer, string data)
    {
        if (transfer == null || !IsCookingGearName(transfer.Gear) || IsCookingDummyName(transfer.Gear) || string.IsNullOrEmpty(transfer.Json)) return;
        if (transfer.Requester == (MyMod.iAmHost ? 0 : ClientUser.myId))
        {
            var player = GameManager.GetPlayerManagerComponent();
            var prefab = MyMod.GetGearItemObject(transfer.Gear);
            if (player == null || prefab == null || MyMod.level_guid != area || !AcceptOutput(transfer.Id)) return;
            var gear = UnityEngine.Object.Instantiate(prefab, player.transform.position, Quaternion.identity).GetComponent<GearItem>();
            gear.DeserializeFromJson(transfer.Json);
            gear.m_LastUpdatedTODHours = GameManager.GetTimeOfDayComponent().GetHoursPlayedNotPaused();
            gear.ManualStart();
            RestoreFoodHeat(gear, transfer);
            if (!player.ProcessPickupItemInteractionCompat(gear)) MyMod.SendDropItem(gear, cookingOutput: true);
            else RestoreFoodHeat(gear, transfer);
        }
        else if (MyMod.iAmHost && transfer.Requester > 0 && transfer.Requester < MyMod.playersData.Count &&
            MyMod.playersData[transfer.Requester]?.m_LevelGuid == area)
        {
            using var packet = MakeServerPacket(9, area, data, 0, 0);
            ServerSend.SendUDPData(transfer.Requester, packet);
        }
    }

    private static void RestoreFoodHeat(GearItem gear, GearTransfer transfer)
    {
        if (gear == null || gear.m_FoodItem == null || !float.IsFinite(transfer.Heat) || transfer.Heat < 0f) return;
        gear.m_FoodItem.m_HeatPercent = transfer.Heat;
        gear.m_FoodItem.m_NumTimesHeatedUp = transfer.TimesHeated;
    }

    private static void DropCookingOutput(GearItem gear, GearPlacePoint point, int requester)
    {
        gear.gameObject.SetActive(true);
        var slot = point.transform.position;
        var origin = slot + point.transform.forward * .85f;
        var recipient = requester >= 0 && requester < MyMod.playersData.Count ? MyMod.playersData[requester] : null;
        if (recipient != null && recipient.m_LevelGuid == MyMod.level_guid && (recipient.m_Position - slot).sqrMagnitude < 16f)
        {
            var offset = Vector3.ProjectOnPlane(slot - recipient.m_Position, Vector3.up);
            origin = recipient.m_Position + Vector3.ClampMagnitude(offset, .45f);
        }
        if (Physics.Raycast(origin + Vector3.up * .35f, Vector3.down, out var floor, 3f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            origin = floor.point + Vector3.up * .06f;
        gear.transform.position = origin;
        MyMod.SendDropItem(gear, samepose: true, cookingOutput: true);
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
            if (previous?.Visual != null) UnityEngine.Object.Destroy(previous.Visual);
            RemotePots.Remove(identity);
            return;
        }
        if (!ValidPot(pot)) return;
        if (previous == null && RemotePots.Count >= 128) return;
        var visual = previous?.Visual;
        var hitboxes = previous?.Hitboxes;
        if (previous != null && (previous.State.Gear != pot.Gear || previous.State.Food != pot.Food || previous.State.Meshes != pot.Meshes || previous.State.Liquid != pot.Liquid) && visual != null)
        {
            UnityEngine.Object.Destroy(visual);
            visual = null;
        }
        if (visual == null)
        {
            visual = CreatePotVisual(pot);
            hitboxes = visual != null ? visual.GetComponentsInChildren<Collider>() : null;
        }
        if (visual != null) visual.transform.SetPositionAndRotation(pot.Pose.Position, pot.Pose.Rotation);
        RemotePots[identity] = new RemotePot(pot, visual, hitboxes, Time.realtimeSinceStartup + 7f);
    }

    private static bool ValidPot(Pot pot) => float.IsFinite(pot.Liters) && pot.Liters >= 0f && pot.Liters <= 20f && IsCookingGearName(pot.Gear) && pot.Pose?.Valid == true && pot.Slot?.Valid == true &&
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
        bool useFood = IsCookingDummyName(pot.Gear) && IsCookingGearName(pot.Food);
        var prefab = MyMod.GetGearItemObject(useFood ? pot.Food : pot.Gear);
        if (prefab == null) return null;
        var root = new GameObject("TLD Coop cooking slot");
        root.layer = prefab.layer;
        Bounds hitBounds = default;
        bool hasHitBounds = false;
        var cooking = prefab.GetComponent<CookingPotItem>();
        var visible = new HashSet<string>();
        for (int i = 0; i < pot.Meshes.Length; i += 8) visible.Add(pot.Meshes.Substring(i, 8));
        foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>(true))
        {
            var filter = renderer.GetComponent<MeshFilter>();
            if (filter == null) continue;
            Mesh sourceMesh = filter.sharedMesh;
            var materials = renderer.sharedMaterials;
            if (pot.Liquid != 0 && cooking != null && renderer == cooking.m_GrubMeshRenderer)
            {
                sourceMesh = pot.Liquid == 2 ? cooking.m_SnowMesh : cooking.m_WaterMesh;
                var liquidMaterials = pot.Liquid == 2 ? cooking.m_MeltSnowMaterialsList : cooking.m_BoilWaterPotMaterialsList;
                if (liquidMaterials != null && liquidMaterials.Length != 0)
                    materials = new[] { liquidMaterials[0] };
                else continue;
            }
            else if (useFood || visible.Count == 0)
            {
                if (!renderer.enabled || !renderer.gameObject.activeSelf || (cooking != null && renderer == cooking.m_GrubMeshRenderer)) continue;
            }
            else if (!visible.Contains(PotMeshKey(renderer, prefab.transform))) continue;
            if (sourceMesh == null) continue;
            var mesh = new GameObject(renderer.name);
            mesh.transform.SetParent(root.transform, false);
            mesh.transform.localPosition = prefab.transform.InverseTransformPoint(renderer.transform.position);
            mesh.transform.localRotation = Quaternion.Inverse(prefab.transform.rotation) * renderer.transform.rotation;
            mesh.transform.localScale = renderer.transform.lossyScale;
            mesh.AddComponent<MeshFilter>().sharedMesh = sourceMesh;
            mesh.AddComponent<MeshRenderer>().sharedMaterials = materials;
            var bounds = sourceMesh.bounds;
            for (int i = 0; i < 8; i++)
            {
                var sign = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
                var point = mesh.transform.TransformPoint(bounds.center + Vector3.Scale(bounds.extents, sign));
                if (hasHitBounds) hitBounds.Encapsulate(point);
                else { hitBounds = new Bounds(point, Vector3.zero); hasHitBounds = true; }
            }
        }
        if (!CopyCookwareColliders(prefab, root) && hasHitBounds)
        {
            var hitbox = root.AddComponent<BoxCollider>();
            hitbox.isTrigger = true;
            hitbox.center = hitBounds.center;
            hitbox.size = Vector3.Max(hitBounds.size, Vector3.one * .01f);
        }
        return root;
    }

    private static bool CopyCookwareColliders(GameObject prefab, GameObject replica)
    {
        bool copied = false;
        foreach (var source in prefab.GetComponentsInChildren<Collider>(true))
        {
            if (!source.enabled || !ActiveBelow(source.transform, prefab.transform)) continue;
            var child = new GameObject(source.name) { layer = source.gameObject.layer };
            child.transform.SetParent(replica.transform, false);
            child.transform.localPosition = prefab.transform.InverseTransformPoint(source.transform.position);
            child.transform.localRotation = Quaternion.Inverse(prefab.transform.rotation) * source.transform.rotation;
            child.transform.localScale = source.transform.lossyScale;
            Collider hitbox;
            if (source.TryCast<BoxCollider>() is { } box)
            {
                var copy = child.AddComponent<BoxCollider>();
                copy.center = box.center;
                copy.size = box.size;
                hitbox = copy;
            }
            else if (source.TryCast<SphereCollider>() is { } sphere)
            {
                var copy = child.AddComponent<SphereCollider>();
                copy.center = sphere.center;
                copy.radius = sphere.radius;
                hitbox = copy;
            }
            else if (source.TryCast<CapsuleCollider>() is { } capsule)
            {
                var copy = child.AddComponent<CapsuleCollider>();
                copy.center = capsule.center;
                copy.radius = capsule.radius;
                copy.height = capsule.height;
                copy.direction = capsule.direction;
                hitbox = copy;
            }
            else if (source.TryCast<MeshCollider>() is { } mesh && mesh.sharedMesh != null)
            {
                var copy = child.AddComponent<MeshCollider>();
                copy.convex = mesh.convex;
                copy.sharedMesh = mesh.sharedMesh;
                hitbox = copy;
            }
            else
            {
                UnityEngine.Object.Destroy(child);
                continue;
            }
            if (hitbox.TryCast<MeshCollider>() is not { convex: false }) hitbox.isTrigger = true;
            copied = true;
        }
        return copied;
    }

    internal static GameObject CookwareReplicaRoot(GameObject target)
    {
        if (target == null) return null;
        foreach (var pot in RemotePots.Values)
            if (pot.Visual != null && (target == pot.Visual || target.transform.parent == pot.Visual.transform))
                return pot.Visual;
        return null;
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
            }
            float current = Time.realtimeSinceStartup;
            foreach (var claim in CookingClaims.ToArray())
                if (claim.Value.Expires <= current) CookingClaims.Remove(claim.Key);
        }
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
            foreach (var pot in RemotePots.Values)
                if (pot.Visual != null) UnityEngine.Object.Destroy(pot.Visual);
            RemotePots.Clear();
            LocalPots.Clear();
            LocalCooking.Clear();
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
            if (item.Object == null || !item.Object.activeInHierarchy) continue;
            var placeable = item.Object.GetComponent<Placeable>();
            if (placeable == null || placeable.m_Invalidated ||
                PlaceableManager.GetPlacementState(placeable) is PlacementState.OnPlayer or PlacementState.Removed ||
                item.Object.GetComponent<DecorationItem>()?.IsCraftedItemPlacementPending() == true) continue;
            SendFurnitureMove(item.Move);
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
            foreach (var pair in Unapplied.OrderBy(pair => !pair.Value.Removed)
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
            if (pair.Key.Area == area) SendToHost(1, area, JsonSerializer.Serialize(pair.Value));
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
        if (from <= 0 || from >= MyMod.playersData.Count || (kind is not (18 or 22) && MyMod.playersData[from]?.m_LevelGuid != area) ||
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
            else if (kind == 4)
            {
                var request = data.Split(':');
                if (request.Length == 2 && int.TryParse(request[0], out int owner) && int.TryParse(request[1], out int action))
                    RequestCookwareRelease(from, owner, key, area, action);
            }
            else if (kind == 8) DeliverWater(area, JsonSerializer.Deserialize<WaterTransfer>(data));
            else if (kind == 9) DeliverGear(area, JsonSerializer.Deserialize<GearTransfer>(data), data);
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
                if (pair.Key.Area == area && pair.Value.Operation == data) Pending.Remove(pair.Key);
            return;
        }
        if (!InWorld || area != MyMod.level_guid) return;
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
            else if (kind == 4 && int.TryParse(data, out int action) && action >= 1 && action <= 4) ReleaseCookware(key, action, owner);
            else if (kind == 8) DeliverWater(area, JsonSerializer.Deserialize<WaterTransfer>(data));
            else if (kind == 9) DeliverGear(area, JsonSerializer.Deserialize<GearTransfer>(data), data);
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

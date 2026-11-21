using System;
using System.Text;
using GameServer;
using Il2Cpp;
using MelonLoader;

namespace TldCoop;

public static class ContainerLootSync
{
	private static Container openBox;

	private static string openScene;

	private static string openGuid;

	private static bool closeClaimed;
	private static bool inventoryUncommitted;

	private static bool uploadPending;

	private static bool closeUpload;

	private static string uploadScene;

	private static string uploadGuid;

	private static string uploadChecksum;
	internal static string UploadLease { get; private set; }
	private static float retryUploadAt;

	public static bool HasPendingUpload => uploadPending;
	internal static bool InventoryUncommitted => inventoryUncommitted;

	internal static string EnsureId(Container box)
	{
		if (box == null) return "";
		var guid = box.GetComponent<ObjectGuid>();
		string id = guid?.Get();
		if (!string.IsNullOrEmpty(id)) return id;
		var data = GameManager.m_SceneTransitionData;
		if (data == null || string.IsNullOrEmpty(data.m_SceneSaveFilenameCurrent)) return "";
		if (guid == null) guid = box.gameObject.AddComponent<ObjectGuid>();
		int salt = Shared.StableHash(data.m_SceneSaveFilenameCurrent + "|" + GameCompat.StripCloneSuffix(box.name));
		id = MyMod.GenerateSeededGUID(data.m_GameRandomSeed ^ salt, box.transform.position);
		guid.Set(id);
		return id;
	}

	internal static void Reset()
	{
		openBox = null;
		openScene = openGuid = uploadScene = uploadGuid = uploadChecksum = null;
		UploadLease = null;
		closeClaimed = uploadPending = closeUpload = inventoryUncommitted = false;
		Shared.ContainerDecompressedDataBackup = "";
		Shared.ContainerGUIDDataBackup = "";
	}

	public static void AcceptedOpen(Container box)
	{
		openBox = box;
		openScene = MyMod.level_guid;
		openGuid = box.GetComponent<ObjectGuid>().Get();
		inventoryUncommitted = true;
		closeClaimed = false;
		uploadPending = false;
		closeUpload = false;
	}

	public static bool TryBeginClose(Container box)
	{
		if (box == null || openBox != box || openScene != MyMod.level_guid || openGuid != box.GetComponent<ObjectGuid>().Get() || closeClaimed)
		{
			MelonLogger.Msg("[Container loot] Ignored duplicate/stale close");
			return false;
		}
		closeClaimed = true;
		return true;
	}

	public static void AwaitUpload()
	{
		closeUpload = true;
	}

	internal static void RetryClose() => closeClaimed = false;

	public static void TrackUpload(string scene, string guid, string compressed)
	{
		closeUpload = closeUpload && closeClaimed && scene == openScene && guid == openGuid;
		if (!uploadPending || uploadScene != scene || uploadGuid != guid)
			UploadLease = closeUpload ? CoopContainerFlow.Lease : Guid.Parse(guid).ToString("N");
		uploadScene = scene;
		uploadGuid = guid;
		uploadChecksum = Shared.SHA256CheckSum(compressed);
		uploadPending = true;
		retryUploadAt = UnityEngine.Time.realtimeSinceStartup + 5f;
	}

	internal static void Tick()
	{
		if (!MyMod.sendMyPosition || !uploadPending || UnityEngine.Time.realtimeSinceStartup < retryUploadAt ||
			string.IsNullOrEmpty(Shared.ContainerDecompressedDataBackup)) return;
		Shared.SendContainerData(Shared.CompressString(Shared.ContainerDecompressedDataBackup), uploadScene, uploadGuid,
			Shared.ContainerDecompressedDataBackup);
	}

	public static bool UploadMatches(string scene, string guid, string checksum)
	{
		if (HasPendingUpload && uploadScene == scene && uploadGuid == guid)
		{
			return uploadChecksum == checksum;
		}
		return false;
	}

	public static void CompleteUpload()
	{
		if (!HasPendingUpload)
		{
			return;
		}
		uploadPending = false;
		Container container = openBox;
		if (closeUpload && container != null && openScene == MyMod.level_guid)
		{
			bool empty = container.IsEmpty();
			container.DestroyAllGear();
			var sync = container.GetComponent<Comps.ContainersSync>();
			if (sync != null) sync.m_Empty = empty;
			container.BeginContainerClose();
			if (container.m_CloseAudio.Length == 0)
			{
				GameAudioManager.PlayGUIButtonBack();
			}
			GameManager.GetPlayerManagerComponent().MaybeRevealPolaroidDiscoveryOnClose();
			Panel_Container panel_Container = GameCompat.Panel<Panel_Container>();
			if (panel_Container.m_Container == container)
			{
				panel_Container.Enable(enable: false);
			}
			if (empty && container.GetComponent<Comps.DeathDropContainer>() != null)
				MyMod.RemoveDeathContainer(openGuid, openScene);
		}
		closeUpload = false;
		inventoryUncommitted = false;
		Shared.ContainerDecompressedDataBackup = "";
		Shared.ContainerGUIDDataBackup = "";
		GameManager.ForceSaveGame();
	}

	internal static void HostCloseCommitted() => inventoryUncommitted = false;

	public static bool MatchesPending(string scene, string guid)
	{
		Container goingToOpenContinaer = MyMod.GoingToOpenContinaer;
		if (goingToOpenContinaer != null && scene == MyMod.level_guid)
		{
			return goingToOpenContinaer.GetComponent<ObjectGuid>()?.Get() == guid;
		}
		return false;
	}

	public static bool Reserve(int client, string scene, string guid, string lease)
	{
		var peer = Server.clients[client];
		if (!Guid.TryParseExact(lease, "N", out _) || scene != MyMod.playersData[client]?.m_LevelGuid ||
			string.IsNullOrEmpty(guid) || guid.Length > 200 ||
			guid.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || guid.Contains('|') ||
			!WildlifeSpawnAuthorityPolicy.WorldScene(scene) || scene.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
			(peer.ContainerGuid != null && (peer.ContainerScene != scene || peer.ContainerGuid != guid || peer.ContainerLease != lease))) return false;
		if (ContainerLockSync.IsLockedOnHost(scene, guid)) return false;
		if (MyMod.MyContainer != null && MyMod.MyContainer.m_LevelGUID == scene && MyMod.MyContainer.m_Guid == guid)
		{
			return false;
		}
		for (int i = 1; i < MyMod.playersData.Count; i++)
		{
			if (i != client)
			{
				if (Server.clients.TryGetValue(i, out var other) && other.IsBusy() &&
					other.ContainerScene == scene && other.ContainerGuid == guid) return false;
				DataStr.ContainerOpenSync containerOpenSync = MyMod.playersData[i]?.m_Container;
				if (containerOpenSync != null && containerOpenSync.m_LevelGUID == scene && containerOpenSync.m_Guid == guid)
				{
					return false;
				}
			}
		}
		peer.ContainerScene = scene;
		peer.ContainerGuid = guid;
		peer.ContainerLease = lease;
		MyMod.playersData[client].m_Container = new DataStr.ContainerOpenSync
		{
			m_Guid = guid,
			m_LevelGUID = scene,
			m_LevelID = MyMod.playersData[client].m_Levelid
		};
		return true;
	}

	internal static void Release(int from, string scene, string guid, string lease)
	{
		var peer = Server.clients[from];
		if (peer.ContainerScene != scene || peer.ContainerGuid != guid || peer.ContainerLease != lease) return;
		peer.ContainerGuid = peer.ContainerLease = null;
		var open = MyMod.playersData[from]?.m_Container;
		if (open?.m_Guid == guid && open.m_LevelGUID == scene) MyMod.playersData[from].m_Container = null;
	}

	internal static bool CanUpload(int from, string scene, string guid, string lease, string checksum, out bool alreadySaved)
	{
		alreadySaved = false;
		if (!Server.clients.TryGetValue(from, out var peer) || peer.ContainerScene != scene) return false;
		alreadySaved = peer.ContainerCommit == guid + "|" + lease + "|" + checksum;
		return alreadySaved || (peer.ContainerGuid == guid && peer.ContainerLease == lease);
	}

	internal static void CommitUpload(int from, string scene, string guid, string lease, string checksum, int state)
	{
		var peer = Server.clients[from];
		peer.ContainerCommit = guid + "|" + lease + "|" + checksum;
		Release(from, scene, guid, lease);
		Shared.AddLootedContainer(new DataStr.ContainerOpenSync
		{
			m_Guid = guid, m_LevelGUID = scene, m_Inspected = true
		}, needSync: true, from, state);
		if (scene == MyMod.level_guid) MyMod.RemoveLootFromContainer(guid, state);
		if (state == 0 && MyMod.DeathCreates.Exists(box => box.m_Guid == guid && box.m_LevelKey == scene))
		{
			MyMod.RemoveDeathContainer(guid, scene);
			ServerSend.DEATHCREATEEMPTYNOW(guid, scene, from);
		}
	}

	internal static bool ReservedByGuest(string scene, string guid)
	{
		foreach (var peer in Server.clients.Values)
			if (peer.IsBusy() && peer.ContainerScene == scene && peer.ContainerGuid == guid) return true;
		return false;
	}

	public static void RejectPending(string scene, string guid)
	{
		if (MatchesPending(scene, guid))
		{
			Container goingToOpenContinaer = MyMod.GoingToOpenContinaer;
			CoopContainerFlow.Rejected(goingToOpenContinaer);
			MyMod.GoingToOpenContinaer = null;
			if (MyMod.LastPacketForRepeat == MyMod.ResendPacketType.Container)
			{
				MyMod.DiscardRepeatPacket();
			}
			GameManager.GetPlayerManagerComponent().SetControlMode(PlayerControlMode.Normal);
			GameManager.GetPlayerManagerComponent().m_Container = null;
			goingToOpenContinaer.m_SearchInProgress = false;
			goingToOpenContinaer.m_OpenInProgress = false;
			goingToOpenContinaer.BeginContainerClose();
			HUDMessage.AddMessage("Контейнер уже осматривают");
		}
	}

	public static string NativeSnapshot(Container box)
	{
		if (box == null)
		{
			return "";
		}
		if (box.m_NotPopulated)
		{
			box.PopulateContents();
		}
		box.InstantiateContents();
		string text = box.Serialize();
		MelonLogger.Msg("[Container loot] Native snapshot guid=" + box.GetComponent<ObjectGuid>()?.Get() + " items=" + box.m_Items.Count);
		string text2 = Shared.CompressString(text);
		if (Encoding.UTF8.GetByteCount(text2) > 48000)
		{
			throw new InvalidOperationException("Initial container snapshot exceeds safe packet size");
		}
		return text2;
	}

	public static string GetOrInitialize(string scene, string guid, string initial)
	{
		int containerState = MPSaveManager.GetContainerState(scene, guid);
		string text = MPSaveManager.LoadContainer(scene, guid);
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		if (MPSaveManager.IsFileExist(MPSaveManager.GetKeyTemplate(MPSaveManager.SaveKeyTemplateType.Container, scene, guid),
			MPSaveManager.GetSeed())) return "";
		if (containerState != -1 && containerState != 2)
		{
			return "";
		}
		if (string.IsNullOrEmpty(initial))
		{
			return "";
		}
		if (Utils.DeserializeObject<ContainerSaveDataProxy>(Shared.DecompressString(initial)) == null)
		{
			throw new InvalidOperationException("Invalid initial native container snapshot");
		}
		if (!MPSaveManager.SaveContainer(scene, guid, initial))
			throw new InvalidOperationException("Could not persist initial container snapshot");
		MPSaveManager.SetConstainerState(scene, guid, 1, DontNotify: true);
		MelonLogger.Msg("[Container loot] Initialized first search scene=" + scene + " guid=" + guid);
		return initial;
	}
}

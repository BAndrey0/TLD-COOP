using GameServer;
using HarmonyLib;
using Il2Cpp;

namespace TldCoop;

// Doors and lids that belong to a container (cabinets, lockers, wardrobes) are OpenClose objects of their own. The
// "someone is using this" check covered only the container, so the door could still be opened while the other player
// had the cabinet open, and the two animations broke the door a little more every time. Such doors cannot be used
// while another player has the container open, and their remote open/close is not replayed (see MyMod.ApplyOpenables).
public static class ContainerDoorGuard
{
	[HarmonyPatch(typeof(OpenClose), "PerformInteraction")]
	private static class BlockBusyContainerDoor
	{
		private static bool Prefix(OpenClose __instance, ref bool __result)
		{
			try
			{
				if (!MyMod.InOnline() || __instance == null || __instance.TryCast<OpenClose>() == null)
				{
					return true;
				}
				if (!IsBusy(ContainerOf(__instance), notify: true))
				{
					return true;
				}
				__result = false;
				return false;
			}
			catch
			{
				return true;
			}
		}
	}

	public static Container ContainerOf(OpenClose door)
	{
		if (door == null)
		{
			return null;
		}
		Container container = door.GetComponent<Container>();
		if (container == null)
		{
			container = door.GetComponentInParent<Container>();
		}
		if (container == null)
		{
			container = door.GetComponentInChildren<Container>();
		}
		if (container == null) container = ContainerLockSync.ContainerForAnimation(door.m_ObjectAnim);
		if (container == null && door.m_ObjectAnim?.m_Target != null)
		{
			var target = door.m_ObjectAnim.m_Target;
			container = target.GetComponentInParent<Container>() ?? target.GetComponentInChildren<Container>();
		}
		return container;
	}

	internal static bool IsBusy(Container container, bool notify)
	{
		if (!MyMod.InOnline() || container == null) return false;
		string id = container.GetComponent<ObjectGuid>()?.Get();
		if (string.IsNullOrEmpty(id)) return false;
		for (int i = 0; i < MyMod.playersData.Count; i++)
		{
			if (i == ClientUser.myId)
			{
				continue;
			}
			DataStr.ContainerOpenSync open = MyMod.playersData[i]?.m_Container;
			if (open != null && open.m_Guid == id && open.m_LevelGUID == MyMod.level_guid)
			{
				if (notify) NotifyBusy();
				return true;
			}
		}
		if (!MyMod.iAmHost || !ContainerLootSync.ReservedByGuest(MyMod.level_guid, id)) return false;
		if (notify) NotifyBusy();
		return true;
	}

	private static void NotifyBusy()
	{
		HUDMessage.AddMessage("Контейнер уже осматривают");
		GameAudioManager.PlayGUIError();
	}
}

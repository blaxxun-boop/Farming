using HarmonyLib;
using UnityEngine;
using SkillManager;

namespace Farming;

public static class MassHarvest
{
	[HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
	private class HarvestNearbyPickables
	{
		private static bool isPicked = false;
		
		private static readonly int itemMask = LayerMask.NameToLayer("item");
		private static readonly int plantMask = LayerMask.GetMask("piece_nonsolid", "item");

		[HarmonyPriority(Priority.LowerThanNormal)]
		private static void Postfix(Pickable __instance)
		{
			bool isFarmingPickable(Pickable pickable) => pickable.gameObject.layer != itemMask || pickable.m_amount > 1;
			
			if (isPicked || Farming.increaseHarvestAmount.Value == 0 || !isFarmingPickable(__instance))
			{
				return;
			}
			
			isPicked = true;

			// ReSharper disable once Unity.PreferNonAllocApi
			foreach (Collider collider in Physics.OverlapSphere(__instance.transform.position, (int)(Player.m_localPlayer.GetSkillFactor("Farming") * 100 / Farming.increaseHarvestAmount.Value) * 1.5f, plantMask))
			{
				if ((collider.GetComponent<Pickable>() ?? collider.transform.parent?.GetComponent<Pickable>()) is { } pickable && pickable != __instance && isFarmingPickable(pickable))
				{
					pickable.Interact(Player.m_localPlayer, false, false);
				}
			}

			isPicked = false;
		}
	}
}

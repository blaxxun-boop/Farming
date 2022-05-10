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
		
		private static void Postfix(Pickable __instance)
		{
			if (isPicked || Farming.increaseHarvestAmount.Value == 0)
			{
				return;
			}
			
			isPicked = true;

			int plantMask = LayerMask.GetMask("piece_nonsolid");

			// ReSharper disable once Unity.PreferNonAllocApi
			foreach (Collider collider in Physics.OverlapSphere(__instance.transform.position, (int)(Player.m_localPlayer.GetSkillFactor("Farming") * 100 / Farming.increaseHarvestAmount.Value) * 1.5f, plantMask))
			{
				if (collider.GetComponent<Pickable>() is { } pickable && pickable != __instance)
				{
					pickable.Interact(Player.m_localPlayer, false, false);
				}
			}

			isPicked = false;
		}
	}
}

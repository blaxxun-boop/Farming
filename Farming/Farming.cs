using System.Reflection;
using BepInEx;
using HarmonyLib;
using SkillManager;
using UnityEngine;

namespace Farming;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class Farming : BaseUnityPlugin
{
	private const string ModName = "Farming";
	private const string ModVersion = "1.0.0";
	private const string ModGUID = "org.bepinex.plugins.farming";

	private static readonly Skill farming = new("Farming", "farming.png");

	public void Awake()
	{
		farming.Description.English("Reduces the time required for plants to grow and increases item yield for harvesting plants.");
		farming.Name.German("Landwirtschaft");
		farming.Description.German("Reduziert die Zeit, bis Pflanzen wachsen und erhöht die Ausbeute beim Ernten von Pflanzen.");
		farming.Configurable = true;

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	[HarmonyPatch(typeof(Plant), nameof(Plant.GetGrowTime))]
	public class MakePlantsGrowFaster
	{
		private static void Postfix(Plant __instance, ref float __result)
		{
			__result /= 1 + __instance.m_nview.GetZDO().GetFloat("Farming Skill Level") * 3;
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
	public class PlayerIsPlantingPlants
	{
		public static bool planting = false;
		private static void Prefix() => planting = true;
		private static void Finalizer() => planting = false;
	}

	[HarmonyPatch(typeof(Plant), nameof(Plant.Awake))]
	public class SaveSkillLevel
	{
		private static void Postfix(Plant __instance)
		{
			if (PlayerIsPlantingPlants.planting)
			{
				__instance.m_nview.GetZDO().Set("Farming Skill Level", Player.m_localPlayer.GetSkillFactor("Farming"));
				Player.m_localPlayer.RaiseSkill("Farming");
			}
			if (__instance.m_nview?.GetZDO()?.GetFloat("Farming Skill Level") > 0.5f)
			{
				__instance.m_biome = (Heightmap.Biome)(-1);
			}
		}
	}

	[HarmonyPatch(typeof(Plant), nameof(Plant.Grow))]
	public class SaveSkillFactor
	{
		public static float skillFactor = 0f;
		private static void Prefix(Plant __instance) => skillFactor = __instance.m_nview.GetZDO().GetFloat("Farming Skill Level");
		private static void Finalizer() => skillFactor = 0;
	}

	[HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
	public class IncreaseItemYield
	{
		private static void Postfix(Pickable __instance)
		{
			if (Random.Range(0f, 1f) < SaveSkillFactor.skillFactor)
			{
				__instance.m_amount *= 2;
			}
		}
	}
}

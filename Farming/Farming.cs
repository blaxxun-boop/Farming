using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using SkillManager;
using UnityEngine;

namespace Farming;

[BepInPlugin(ModGUID, ModName, ModVersion)]
public class Farming : BaseUnityPlugin
{
	private const string ModName = "Farming";
	private const string ModVersion = "1.1.0";
	private const string ModGUID = "org.bepinex.plugins.farming";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<float> growSpeedFactor = null!;
	private static ConfigEntry<float> cropYieldFactor = null!;
	private static ConfigEntry<int> ignoreBiomeLevel = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	private enum Toggle
	{
		On = 1,
		Off = 0
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	private static Skill farming = null!;

	public void Awake()
	{
		farming = new Skill("Farming", "farming.png");
		farming.Description.English("Reduces the time required for plants to grow and increases item yield for harvesting plants.");
		farming.Name.German("Landwirtschaft");
		farming.Description.German("Reduziert die Zeit, bis Pflanzen wachsen und erhöht die Ausbeute beim Ernten von Pflanzen.");
		farming.Configurable = false;

		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		growSpeedFactor = config("2 - Crops", "Grow Speed Factor", 3f, new ConfigDescription("Speed factor for crop growth at skill level 100.", new AcceptableValueRange<float>(1f, 10f)));
		cropYieldFactor = config("2 - Crops", "Crop Yield Factor", 2f, new ConfigDescription("Item yield factor for crops at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		ignoreBiomeLevel = config("2 - Crops", "Ignore Biome Level", 50, new ConfigDescription("Required skill level to ignore the required biome of planted crops. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		experienceGainedFactor = config("3 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the farming skill.", new AcceptableValueRange<float>(0.01f, 5f)));
		experienceGainedFactor.SettingChanged += (_, _) => farming.SkillGainFactor = experienceGainedFactor.Value;
		farming.SkillGainFactor = experienceGainedFactor.Value;
		
		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}

	[HarmonyPatch(typeof(Plant), nameof(Plant.GetGrowTime))]
	public class MakePlantsGrowFaster
	{
		private static void Postfix(Plant __instance, ref float __result)
		{
			__result /= 1 + __instance.m_nview.GetZDO().GetFloat("Farming Skill Level") * (growSpeedFactor.Value - 1);
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
			if (__instance.m_nview?.GetZDO()?.GetFloat("Farming Skill Level") >= ignoreBiomeLevel.Value / 100f)
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
				int baseYield = Mathf.FloorToInt(cropYieldFactor.Value);
				__instance.m_amount *= baseYield + (Random.Range(0f, 1f) < cropYieldFactor.Value - baseYield ? 0 : 1);
			}
		}
	}
}

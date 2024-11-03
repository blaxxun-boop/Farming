using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using JetBrains.Annotations;
using ServerSync;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Farming;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInIncompatibility("org.bepinex.plugins.valheim_plus")]
public class Farming : BaseUnityPlugin
{
	private const string ModName = "Farming";
	private const string ModVersion = "2.2.0";
	private const string ModGUID = "org.bepinex.plugins.farming";

	private static readonly ConfigSync configSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };

	private static ConfigEntry<Toggle> serverConfigLocked = null!;
	private static ConfigEntry<float> growSpeedFactor = null!;
	private static ConfigEntry<float> cropYieldFactor = null!;
	private static ConfigEntry<int> ignoreBiomeLevel = null!;
	private static ConfigEntry<float> experienceGainedFactor = null!;
	private static ConfigEntry<int> experienceLoss = null!;
	public static ConfigEntry<KeyboardShortcut> plantModeToggleHotkey = null!;
	public static ConfigEntry<int> increasePlantAmount = null!;
	public static ConfigEntry<int> increaseHarvestAmount = null!;
	public static ConfigEntry<Toggle> randomRotation = null!;
	public static ConfigEntry<KeyboardShortcut> snapModeToggleHotkey = null!;
	private static ConfigEntry<int> showPlantProgressLevel = null!;
	public static ConfigEntry<int> staminaReductionPerLevel = null!;

	private ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
	{
		ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

		SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
		syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

		return configEntry;
	}

	private ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);

	public enum Toggle
	{
		On = 1,
		Off = 0,
	}

	private class ConfigurationManagerAttributes
	{
		[UsedImplicitly] public bool? ShowRangeAsPercent;
	}

	private static Skills.SkillDef? farmingSkill;

	public void Awake()
	{
		serverConfigLocked = config("1 - General", "Lock Configuration", Toggle.On, "If on, the configuration is locked and can be changed by server admins only.");
		configSync.AddLockingConfigEntry(serverConfigLocked);
		growSpeedFactor = config("2 - Crops", "Grow Speed Factor", 3f, new ConfigDescription("Speed factor for crop growth at skill level 100.", new AcceptableValueRange<float>(1f, 10f)));
		cropYieldFactor = config("2 - Crops", "Crop Yield Factor", 2f, new ConfigDescription("Item yield factor for crops at skill level 100.", new AcceptableValueRange<float>(1f, 5f)));
		showPlantProgressLevel = config("2 - Crops", "Show Progress Level", 30, new ConfigDescription("Required skill level to see the progress of planted crops. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		ignoreBiomeLevel = config("2 - Crops", "Ignore Biome Level", 50, new ConfigDescription("Required skill level to ignore the required biome of planted crops. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		increasePlantAmount = config("2 - Crops", "Plant Increase Interval", 20, new ConfigDescription("Level interval to increase the number of crops planted at the same time. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		increaseHarvestAmount = config("2 - Crops", "Harvest Increase Interval", 20, new ConfigDescription("Level interval to increase the radius harvested at the same time. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		staminaReductionPerLevel = config("2 - Crops", "Stamina Reduction per Level", 1, new ConfigDescription("Reduces the stamina usage while planting and harvesting your crops. Percentage stamina reduction per level. 0 is disabled.", new AcceptableValueRange<int>(0, 100), new ConfigurationManagerAttributes { ShowRangeAsPercent = false }));
		randomRotation = config("2 - Crops", "Random Rotation", Toggle.Off, new ConfigDescription("Rotates each crop randomly. Some people say this looks more natural."), false);
		randomRotation.SettingChanged += (_, _) => MassPlant.determineGhostRotations();
		experienceGainedFactor = config("3 - Other", "Skill Experience Gain Factor", 1f, new ConfigDescription("Factor for experience gained for the farming skill.", new AcceptableValueRange<float>(0.01f, 5f)));
		experienceGainedFactor.SettingChanged += (_, _) =>
		{
			if (farmingSkill is not null)
			{
				farmingSkill.m_increseStep = experienceGainedFactor.Value;
			}
		};
		experienceLoss = config("3 - Other", "Skill Experience Loss", 0, new ConfigDescription("How much experience to lose in the farming skill on death.", new AcceptableValueRange<int>(0, 100)));
		plantModeToggleHotkey = config("3 - Other", "Toggle Mass Plant Hotkey", new KeyboardShortcut(KeyCode.LeftShift), new ConfigDescription("Shortcut to press to toggle between the single plant mode and the mass plant mode. Please note that you have to stand still, to toggle this."), false);
		snapModeToggleHotkey = config("3 - Other", "Toggle Snapping Hotkey", new KeyboardShortcut(KeyCode.LeftControl), new ConfigDescription("Shortcut to press to toggle between snapping mode and not snapping. Please note that you have to stand still, to toggle this."), false);

		Assembly assembly = Assembly.GetExecutingAssembly();
		Harmony harmony = new(ModGUID);
		harmony.PatchAll(assembly);
	}
	
	[HarmonyPatch(typeof(Skills), nameof(Skills.Awake))]
	private static class SetXPGain
	{
		private static void Postfix(Skills __instance)
		{
			foreach (Skills.SkillDef skill in __instance.m_skills)
			{
				if (skill.m_skill == Skills.SkillType.Farming)
				{
					farmingSkill = skill;
					skill.m_increseStep = experienceGainedFactor.Value;
				}
			}
		}
	}
	
	[HarmonyPatch(typeof(Skills), nameof(Skills.OnDeath))]
	public class ChangeSkillLoss
	{
		private static void Prefix(Skills __instance, ref Skills.Skill? __state)
		{
			if (__instance.m_skillData.TryGetValue(Skills.SkillType.Farming, out Skills.Skill skill))
			{
				__state = skill;
				if (experienceLoss.Value > 0)
				{
					skill.m_level -= skill.m_level * experienceLoss.Value / 100f;
					skill.m_accumulator = 0.0f;
				}
				__instance.m_skillData.Remove(Skills.SkillType.Farming);
			}
		}

		private static void Finalizer(Skills __instance, ref Skills.Skill? __state)
		{
			if (__state is not null)
			{
				__instance.m_skillData[Skills.SkillType.Farming] = __state;
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.GetBuildStamina))]
	private static class DisableIndividualCultivatingStaminaUsage
	{
		private static Skills.SkillType PreventFarmingIncrease(Skills.SkillType skill) => skill == Skills.SkillType.Farming ? Skills.SkillType.None : skill;

		private static readonly MethodInfo getSkillFactor = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.GetSkillFactor));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(getSkillFactor))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(DisableIndividualCultivatingStaminaUsage), nameof(PreventFarmingIncrease)));
				}
				yield return instruction;
			}
		}
	}

	[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
	private static class RemoveFarmingSkillGainFromCultivator
	{
		private static float PreventFarmingIncrease(float skill) => Player.m_localPlayer.m_buildPieces?.m_skill == Skills.SkillType.Farming ? 0 : skill;

		private static readonly MethodInfo raiseSkill = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.RaiseSkill));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(raiseSkill))
				{
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(RemoveFarmingSkillGainFromCultivator), nameof(PreventFarmingIncrease)));
				}
				yield return instruction;
			}
		}
	}

	[HarmonyPatch(typeof(Pickable), nameof(Pickable.Interact))]
	private static class RemoveFarmingSkillGainFromPickable
	{
		private static float PreventFarmingIncrease(float skill, Pickable pickable) => pickable.m_pickRaiseSkill == Skills.SkillType.Farming ? 0 : skill;

		private static readonly MethodInfo raiseSkill = AccessTools.DeclaredMethod(typeof(Character), nameof(Character.RaiseSkill));

		private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
		{
			foreach (CodeInstruction instruction in instructions)
			{
				if (instruction.Calls(raiseSkill))
				{
					yield return new CodeInstruction(OpCodes.Ldarg_0);
					yield return new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(RemoveFarmingSkillGainFromPickable), nameof(PreventFarmingIncrease)));
				}
				yield return instruction;
			}
		}
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
				__instance.m_nview.GetZDO().Set("Farming Skill Level", Player.m_localPlayer.GetSkillFactor(Skills.SkillType.Farming) + 1e-14f);
				Player.m_localPlayer.RaiseSkill(Skills.SkillType.Farming);
			}
			if (ignoreBiomeLevel.Value > 0 && __instance.m_nview?.GetZDO()?.GetFloat("Farming Skill Level") >= ignoreBiomeLevel.Value / 100f)
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
			if (__instance.m_nview?.GetZDO() is { } zdo)
			{
				int yieldMultiplier = zdo.GetInt("Farming Yield Multiplier", 1);
				if (SaveSkillFactor.skillFactor > 0)
				{
					if (Random.Range(0f, 1f) < SaveSkillFactor.skillFactor)
					{
						int baseYield = Mathf.FloorToInt(cropYieldFactor.Value);
						yieldMultiplier = baseYield + (Random.Range(0f, 1f) < cropYieldFactor.Value - baseYield ? 1 : 0);
					}
					zdo.Set("Farming Yield Multiplier", yieldMultiplier);
					if (yieldMultiplier > 0)
					{
						__instance.m_maxLevelBonusChance = 0;
					}
				}
				__instance.m_amount *= yieldMultiplier;
			}
		}
	}

	[HarmonyPatch(typeof(Plant), nameof(Plant.GetHoverText))]
	private static class DisplayProgress
	{
		private static void Postfix(Plant __instance, ref string __result)
		{
			if (showPlantProgressLevel.Value > 0 && Player.m_localPlayer.GetSkillFactor(Skills.SkillType.Farming) >= showPlantProgressLevel.Value / 100f)
			{
				__result += $"\n{Math.Min(100, Math.Round(__instance.TimeSincePlanted() / __instance.GetGrowTime() * 100))}% grown";
			}
		}
	}
}

/*
File largely copied from https://github.com/Xeio/MassFarming

Preserving copyright for this file:

MIT License

Copyright (c) 2021 Joshua Shaffer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;
// ReSharper disable Unity.PreferNonAllocApi

namespace Farming;

[HarmonyPatch]
public class MassPlant
{
	private static Vector3 ghostCenterPosition;
	private static Quaternion ghostRotation;
	private static float[] randomGhostRotations = new float[1];
	private static Piece placedPiece = null!;
	private static List<PlantingCell> placements = new();
	private static bool placeSuccessful = false;
	private static bool massPlanting = true;
	private static bool snapping = true;
	private const float plantSpacing = 1.8f;

	private static int GridWidth() => 1 + Mathf.FloorToInt(Player.m_localPlayer.GetSkillFactor(Skills.SkillType.Farming) * (100f / Farming.increasePlantAmount.Value)) / 2;
	private static int GridHeight() => 1 + Mathf.FloorToInt(Mathf.Min(Player.m_localPlayer.GetSkillFactor(Skills.SkillType.Farming), 0.999f) * (100f / Farming.increasePlantAmount.Value) + 1) / 2;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
	public static void PlacePiecePostfix(Player __instance, Piece piece)
	{
		placeSuccessful = true;
		placedPiece = piece;
	}

	[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
	private static class ChangeStaminaUsage
	{
		private static float stamina = 0f;
		private static ItemDrop.ItemData? item;

		private static void Prefix(Player __instance)
		{
			if (__instance.GetRightItem()?.m_shared.m_name == "$item_cultivator")
			{
				item = __instance.GetRightItem();
				stamina = item.m_shared.m_attack.m_attackStamina;
				item.m_shared.m_attack.m_attackStamina *= Mathf.Max(0, 1 - __instance.GetSkillFactor(Skills.SkillType.Farming) * Farming.staminaReductionPerLevel.Value);
			}

			//Clear any previous place result
			placeSuccessful = false;
		}

		private static void Finalizer()
		{
			if (item is not null)
			{
				item.m_shared.m_attack.m_attackStamina = stamina;
				item = null;
			}
		}
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
	public static void UpdatePlacementPostfix(Player __instance, bool takeInput, float dt)
	{
		if (Farming.increasePlantAmount.Value == 0)
		{
			return;
		}

		if (!placeSuccessful)
		{
			//Ignore when the place didn't happen
			return;
		}

		Plant? plant = placedPiece.gameObject.GetComponent<Plant>();
		if (!plant)
		{
			return;
		}

		if (!massPlanting)
		{
			//Hotkey required
			return;
		}

		Heightmap? heightmap = Heightmap.FindHeightmap(ghostCenterPosition);
		if (!heightmap)
		{
			return;
		}

		for (int i = 1; i < placements.Count; ++i)
		{
			Vector3 newPos = placements[i].pos;
			if (placedPiece.m_cultivatedGroundOnly && !heightmap.IsCultivated(newPos))
			{
				continue;
			}

			bool hasMats = __instance.m_noPlacementCost || __instance.HaveRequirements(placedPiece, Player.RequirementMode.CanBuild);
			if (!hasMats)
			{
				return;
			}

			if (placements[i].valid != Player.PlacementStatus.Valid)
			{
				continue;
			}

			try
			{
				Farming.PlayerIsPlantingPlants.planting = true;

				Quaternion rotation = Quaternion.Euler(new Vector3(0, randomGhostRotations[i], 0)) * ghostRotation;

				GameObject newPlaceObj = Object.Instantiate(placedPiece.gameObject, newPos, rotation);

				Piece component = newPlaceObj.GetComponent<Piece>();
				if (component)
				{
					component.SetCreator(__instance.GetPlayerID());
				}
				placedPiece.m_placeEffect.Create(newPos, rotation, newPlaceObj.transform);
			}
			finally
			{
				Farming.PlayerIsPlantingPlants.planting = false;
			}

			Game.instance.GetPlayerProfile().IncrementStat(PlayerStatType.Builds);
			__instance.ConsumeResources(placedPiece.m_resources, 0);
		}
	}

	private struct PlantingCell
	{
		public Vector3 pos;
		public Player.PlacementStatus valid;
	}

	private static List<PlantingCell> BuildPlantingGridPositions(Vector3 originPos, Plant placedPlant, Quaternion rotation)
	{
		float plantDistance = placedPlant.m_growRadius * 2;
		int height = massPlanting ? GridHeight() : 1;
		int width = massPlanting ? GridWidth() : 1;

		int originalHeight = height;
		int originalWidth = width;

		if (width == 1)
		{
			width = 3;
		}
		if (height == 1)
		{
			height = 3;
		}

		List<Vector3> gridPositions = new(width * height);
		Vector3 left = rotation * Vector3.left * plantDistance;
		Vector3 forward = rotation * Vector3.forward * plantDistance;
		Vector3 gridOrigin = originPos - forward * (width - 1) / 2f - left * (height - 1) / 2f;

		for (int x = 0; x < width; ++x)
		{
			Vector3 newPos = gridOrigin;
			for (int z = 0; z < height; ++z)
			{
				gridPositions.Add(newPos);
				newPos += left;
			}
			gridOrigin += forward;
		}

		if (snapping)
		{
			int plantMask = LayerMask.GetMask("piece_nonsolid");

			HashSet<Vector3> snappingPositions = new();
			float minTranslationAll = float.MaxValue;
			foreach (Vector3 gridPos in gridPositions)
			{
				foreach (Collider collider in Physics.OverlapSphere(gridPos, plantDistance * plantSpacing * 1.5f, plantMask))
				{
					if (collider.GetComponent<Plant>() || collider.GetComponent<Pickable>())
					{
						Vector3 position = collider.transform.position;
						snappingPositions.Add(position with { y = 0 });
						minTranslationAll = Mathf.Min(minTranslationAll, Utils.DistanceXZ(gridPos, position));
					}
				}
			}

			if (minTranslationAll < plantDistance * plantSpacing)
			{
				Quaternion snapRotation = Quaternion.identity;
				if (snappingPositions.Count > 1 && gridPositions.Count > 1)
				{
					Vector3 firstSnapPos = snappingPositions.OrderBy(p => snappingPositions.Where(s => s != p).Min(s => Utils.DistanceXZ(s, p)) + (p - originPos).magnitude / 1000f).First();
					Vector3 nextSnapPos = snappingPositions.Where(p => p != firstSnapPos).OrderBy(p => Utils.DistanceXZ(firstSnapPos, p) + (p - originPos).magnitude / 1000f).First();
					float angle = Vector3.SignedAngle(gridPositions[0] - gridPositions[1], firstSnapPos - nextSnapPos, Vector3.up);
					angle = (angle + 225) % 90 - 45;
					snapRotation = Quaternion.Euler(0, angle, 0);
					for (int i = 0; i < gridPositions.Count; ++i)
					{
						gridPositions[i] = snapRotation * (gridPositions[i] - originPos) + originPos;
					}
				}

				float minTranslation = float.MaxValue;
				Vector3 translationVector = Vector3.zero;
				for (int i = 0; translationVector == Vector3.zero && i < gridPositions.Count; ++i)
				{
					foreach (Collider collider in Physics.OverlapSphere(gridPositions[i], plantDistance * plantSpacing, plantMask))
					{
						if (collider.GetComponent<Plant>() || collider.GetComponent<Pickable>())
						{
							float distance = Utils.DistanceXZ(gridPositions[i], collider.transform.position);
							if (distance - 0.001 < minTranslation)
							{
								minTranslation = distance;
								translationVector = gridPositions[i] with { y = 0 } - collider.transform.position with { y = 0 };

								if ((gridPositions[i] with { y = 0 } + snapRotation * left - collider.transform.position with { y = 0 }).sqrMagnitude < translationVector.sqrMagnitude)
								{
									translationVector += snapRotation * left;
								}
								else if ((gridPositions[i] with { y = 0 } - snapRotation * left - collider.transform.position with { y = 0 }).sqrMagnitude < translationVector.sqrMagnitude)
								{
									translationVector -= snapRotation * left;
								}

								if ((gridPositions[i] with { y = 0 } + snapRotation * forward - collider.transform.position with { y = 0 }).sqrMagnitude < translationVector.sqrMagnitude)
								{
									translationVector += snapRotation * forward;
								}
								else if ((gridPositions[i] with { y = 0 } - snapRotation * forward - collider.transform.position with { y = 0 }).sqrMagnitude < translationVector.sqrMagnitude)
								{
									translationVector -= snapRotation * forward;
								}
							}
						}
					}
				}

				for (int i = 0; translationVector != Vector3.zero && i < gridPositions.Count; ++i)
				{
					gridPositions[i] -= translationVector;
				}
			}
		}

		List<PlantingCell> grid = new();
		Heightmap heightmap = Heightmap.FindHeightmap(originPos);

		foreach (Vector3 gridPos in gridPositions)
		{
			Vector3 pos = gridPos with { y = ZoneSystem.instance.GetGroundHeight(gridPos) };

			Player.PlacementStatus valid = Player.PlacementStatus.Valid;
			if (placedPlant.GetComponent<Piece>().m_cultivatedGroundOnly && !heightmap.IsCultivated(pos))
			{
				valid = Player.PlacementStatus.NeedCultivated;
			}
			else if (!HasGrowSpace(pos, placedPlant.gameObject))
			{
				valid = Player.PlacementStatus.Invalid;
			}

			grid.Add(new PlantingCell { pos = pos, valid = valid });
		}

		if (originalWidth == 1)
		{
			grid = originalHeight == 1 ? new List<PlantingCell> { grid[4] } : new List<PlantingCell> { grid[2], grid[3] };
		}

		// exchange positions to have primary ghost (i.e. Player.m_placementGhost) position next to center if possible, otherwise a build-able position
		int centerValidIndex = grid.Select((c, i) => new Tuple<PlantingCell, int>(c, i)).Where(g => g.Item1.valid == Player.PlacementStatus.Valid).OrderBy(g => (originPos - g.Item1.pos).magnitude).FirstOrDefault()?.Item2 ?? 0;
		// ReSharper disable once SwapViaDeconstruction
		PlantingCell centerPos = grid[centerValidIndex];
		grid[centerValidIndex] = grid[0];
		grid[0] = centerPos;

		return grid;
	}

	private static readonly int _plantSpaceMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "piece", "piece_nonsolid");

	private static bool HasGrowSpace(Vector3 newPos, GameObject go)
	{
		if (go.GetComponent<Plant>() is { } placingPlant)
		{
			Collider[] nearbyObjects = new Collider[1];
			return Physics.OverlapSphereNonAlloc(newPos, placingPlant.m_growRadius, nearbyObjects, _plantSpaceMask) == 0;
		}
		return true;
	}

	private static bool invalidatedGhosts = true;
	private static GameObject?[] _placementGhosts = new GameObject?[1];
	private static readonly Piece _fakeResourcePiece = new()
	{
		m_dlc = string.Empty,
		m_resources = new[]
		{
			new Piece.Requirement(),
		},
	};
	private static readonly int RippleDistance = Shader.PropertyToID("_RippleDistance");
	private static readonly int ValueNoise = Shader.PropertyToID("_ValueNoise");

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Player), nameof(Player.SetupPlacementGhost))]
	public static void SetupPlacementGhostPostfix(Player __instance)
	{
		DestroyGhosts();
	}

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacementGhost))]
	public static void UpdatePlacementGhostPostfix(Player __instance, bool flashGuardStone)
	{
		GameObject? ghost = __instance.m_placementGhost;
		if (!ghost || !ghost.activeSelf)
		{
			SetGhostsActive(false);
			return;
		}

		ghostCenterPosition = ghost.transform.position;
		ghostRotation = ghost.transform.rotation;

		if (Farming.increasePlantAmount.Value == 0)
		{
			SetGhostsActive(false);
			return;
		}

		if (Farming.snapModeToggleHotkey.Value.IsDown())
		{
			//Hotkey required
			snapping = !snapping;
		}

		if (Farming.plantModeToggleHotkey.Value.IsDown())
		{
			//Hotkey required
			massPlanting = !massPlanting;
		}

		Plant? plant = ghost.GetComponent<Plant>();
		if (!plant)
		{
			SetGhostsActive(false);
			return;
		}

		if (!EnsureGhostsBuilt(__instance))
		{
			SetGhostsActive(false);
			return;
		}

		//Find the required resource to plant the item
		//Assuming that for plants there is only a single resource requirement...
		Piece.Requirement requirement = ghost.GetComponent<Piece>().m_resources.First(r => r.m_resItem && r.m_amount > 0);
		_fakeResourcePiece.m_resources[0].m_resItem = requirement.m_resItem;
		_fakeResourcePiece.m_resources[0].m_amount = requirement.m_amount;

		placements = BuildPlantingGridPositions(ghost.transform.position, plant, ghost.transform.rotation);

		_placementGhosts[0] = ghost;

		for (int i = 0; i < _placementGhosts.Length; ++i)
		{
			if (i == 1 && !massPlanting)
			{
				SetGhostsActive(false);
				return;
			}

			Vector3 newPos = placements[i].pos;

			//Track total cost of each placement
			_fakeResourcePiece.m_resources[0].m_amount += requirement.m_amount;

			_placementGhosts[i]!.transform.position = newPos;
			_placementGhosts[i]!.transform.rotation = Quaternion.Euler(new Vector3(0, randomGhostRotations[i], 0)) * ghost.transform.rotation;
			_placementGhosts[i]!.SetActive(true);

			Player.PlacementStatus valid = placements[i].valid;
			if (i == 0)
			{
				__instance.m_placementStatus = valid;
			}

			if (valid == Player.PlacementStatus.Valid && !__instance.m_noPlacementCost && !__instance.HaveRequirements(_fakeResourcePiece, Player.RequirementMode.CanBuild))
			{
				valid = Player.PlacementStatus.Invalid;
			}

			_placementGhosts[i]!.GetComponent<Piece>().SetInvalidPlacementHeightlight(valid != Player.PlacementStatus.Valid);
		}
	}

	public static void determineGhostRotations()
	{
		for (int i = 1; i < randomGhostRotations.Length; ++i)
		{
			randomGhostRotations[i] = Farming.randomRotation.Value == Farming.Toggle.Off ? 0 : Random.Range(0f, 360f);
		}
	}

	private static bool EnsureGhostsBuilt(Player player)
	{
		int requiredSize = GridHeight() * GridWidth();
		bool needsRebuild = invalidatedGhosts || _placementGhosts.Length != requiredSize;
		if (needsRebuild)
		{
			DestroyGhosts();

			if (_placementGhosts.Length != requiredSize)
			{
				_placementGhosts = new GameObject[requiredSize];
				randomGhostRotations = new float[requiredSize];
			}

			determineGhostRotations();

			if (player.m_buildPieces is { } pieceTable && pieceTable.GetSelectedPrefab() is { } prefab)
			{
				if (prefab.GetComponent<Piece>().m_repairPiece)
				{
					//Repair piece doesn't have ghost
					return false;
				}

				for (int i = 1; i < _placementGhosts.Length; ++i)
				{
					_placementGhosts[i] = SetupMyGhost(prefab);
				}

				invalidatedGhosts = false;
			}
			else
			{
				//No prefab, so don't need ghost (this probably shouldn't ever happen)
				return false;
			}
		}

		return true;
	}

	private static void DestroyGhosts()
	{
		foreach (GameObject? ghost in _placementGhosts.Skip(1))
		{
			if (ghost)
			{
				Object.Destroy(ghost);
			}
		}
		invalidatedGhosts = true;
	}

	private static void SetGhostsActive(bool active)
	{
		foreach (GameObject? ghost in _placementGhosts.Skip(1))
		{
			if (ghost)
			{
				ghost!.SetActive(active);
			}
		}
	}

	private static GameObject SetupMyGhost(GameObject prefab)
	{
		//This takes some shortcuts because it's only ever used for Plant pieces

		ZNetView.m_forceDisableInit = true;
		GameObject newGhost = Object.Instantiate(prefab);
		ZNetView.m_forceDisableInit = false;
		newGhost.name = prefab.name;

		foreach (Joint joint in newGhost.GetComponentsInChildren<Joint>())
		{
			Object.Destroy(joint);
		}

		foreach (Rigidbody rigidBody in newGhost.GetComponentsInChildren<Rigidbody>())
		{
			Object.Destroy(rigidBody);
		}

		int layer = LayerMask.NameToLayer("ghost");
		foreach (Transform childTransform in newGhost.GetComponentsInChildren<Transform>())
		{
			childTransform.gameObject.layer = layer;
		}

		foreach (TerrainModifier terrainModifier in newGhost.GetComponentsInChildren<TerrainModifier>())
		{
			Object.Destroy(terrainModifier);
		}

		foreach (GuidePoint guidepoint in newGhost.GetComponentsInChildren<GuidePoint>())
		{
			Object.Destroy(guidepoint);
		}

		foreach (Light light in newGhost.GetComponentsInChildren<Light>())
		{
			Object.Destroy(light);
		}

		Transform ghostOnlyTransform = newGhost.transform.Find("_GhostOnly");
		if ((bool)ghostOnlyTransform)
		{
			ghostOnlyTransform.gameObject.SetActive(value: true);
		}

		foreach (MeshRenderer meshRenderer in newGhost.GetComponentsInChildren<MeshRenderer>())
		{
			if (!(meshRenderer.sharedMaterial == null))
			{
				Material[] sharedMaterials = meshRenderer.sharedMaterials;
				for (int j = 0; j < sharedMaterials.Length; ++j)
				{
					Material material = new(sharedMaterials[j]);
					material.SetFloat(RippleDistance, 0f);
					material.SetFloat(ValueNoise, 0f);
					sharedMaterials[j] = material;
				}
				meshRenderer.sharedMaterials = sharedMaterials;
				meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
			}
		}

		return newGhost;
	}
}

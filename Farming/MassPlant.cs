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

using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using SkillManager;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Farming;

[HarmonyPatch]
public class MassPlant
{
	private static Vector3 ghostCenterPosition;
	private static Quaternion ghostRotation;
	private static Piece placedPiece = null!;
	private static bool placeSuccessful = false;

	private static int GridWidth() => 1 + Mathf.FloorToInt(Player.m_localPlayer.GetSkillFactor("Farming") * (100f / Farming.increasePlantAmount.Value)) / 2;
	private static int GridHeight() => 1 + Mathf.FloorToInt(Mathf.Min(Player.m_localPlayer.GetSkillFactor("Farming"), 0.999f) * (100f / Farming.increasePlantAmount.Value) + 1) / 2;

	[HarmonyPostfix]
	[HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
	public static void PlacePiecePostfix(Player __instance, ref bool __result, Piece piece)
	{
		placeSuccessful = __result;
		if (__result)
		{
			placedPiece = piece;
		}
	}

	[HarmonyPrefix]
	[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
	public static void UpdatePlacementPrefix(bool takeInput, float dt)
	{
		//Clear any previous place result
		placeSuccessful = false;
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

		if (Farming.singlePlantHotkey.Value.IsPressed())
		{
			//Hotkey required
			return;
		}

		Heightmap? heightmap = Heightmap.FindHeightmap(ghostCenterPosition);
		if (!heightmap)
		{
			return;
		}

		foreach (Vector3 newPos in BuildPlantingGridPositions(ghostCenterPosition, plant, ghostRotation).Skip(1))
		{
			if (placedPiece.m_cultivatedGroundOnly && !heightmap.IsCultivated(newPos))
			{
				continue;
			}

			bool hasMats = __instance.m_noPlacementCost || __instance.HaveRequirements(placedPiece, Player.RequirementMode.CanBuild);
			if (!hasMats)
			{
				return;
			}

			if (!HasGrowSpace(newPos, placedPiece.gameObject))
			{
				continue;
			}

			try
			{
				Farming.PlayerIsPlantingPlants.planting = true;

				GameObject newPlaceObj = Object.Instantiate(placedPiece.gameObject, newPos, ghostRotation);

				Piece component = newPlaceObj.GetComponent<Piece>();
				if (component)
				{
					component.SetCreator(__instance.GetPlayerID());
				}
				placedPiece.m_placeEffect.Create(newPos, ghostRotation, newPlaceObj.transform);
				++Game.instance.GetPlayerProfile().m_playerStats.m_builds;

				__instance.ConsumeResources(placedPiece.m_resources, 0);
			}
			finally
			{
				Farming.PlayerIsPlantingPlants.planting = false;
			}
		}
	}

	private static List<Vector3> BuildPlantingGridPositions(Vector3 originPos, Plant placedPlant, Quaternion rotation)
	{
		float plantRadius = placedPlant.m_growRadius * 2;
		int height = GridHeight();
		int width = GridWidth();

		List<Vector3> gridPositions = new(width * height);
		Vector3 left = rotation * Vector3.left * plantRadius;
		Vector3 forward = rotation * Vector3.forward * plantRadius;
		Vector3 gridOrigin = originPos - (forward * (width - 1) / 2f) - (left * (height - 1) / 2f);

		for (int x = 0; x < width; ++x)
		{
			Vector3 newPos = gridOrigin;
			for (int z = 0; z < height; ++z)
			{
				newPos.y = ZoneSystem.instance.GetGroundHeight(newPos);
				gridPositions.Add(newPos);
				newPos += left;
			}
			gridOrigin += forward;
		}

		// exchange positions to have primary ghost (i.e. Player.m_placementGhost) position next to center
		// ReSharper disable once SwapViaDeconstruction
		Vector3 centerPos = gridPositions[gridPositions.Count / 2];
		gridPositions[gridPositions.Count / 2] = gridPositions[0];
		gridPositions[0] = centerPos;

		return gridPositions;
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
			new Piece.Requirement()
		}
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
		
		if (Farming.singlePlantHotkey.Value.IsPressed())
		{
			//Hotkey required
			SetGhostsActive(false);
			return;
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

		Vector3 position = ghost.transform.position;
		Heightmap heightmap = Heightmap.FindHeightmap(position);
		List<Vector3> positions = BuildPlantingGridPositions(position, plant, ghost.transform.rotation);

		_placementGhosts[0] = ghost;

		for (int i = 0; i < _placementGhosts.Length; ++i)
		{
			Vector3 newPos = positions[i];

			//Track total cost of each placement
			_fakeResourcePiece.m_resources[0].m_amount += requirement.m_amount;

			_placementGhosts[i]!.transform.position = newPos;
			_placementGhosts[i]!.transform.rotation = ghost.transform.rotation;
			_placementGhosts[i]!.SetActive(true);

			bool invalid = false;
			if (ghost.GetComponent<Piece>().m_cultivatedGroundOnly && !heightmap.IsCultivated(newPos))
			{
				invalid = true;
			}
			else if (!HasGrowSpace(newPos, ghost.gameObject))
			{
				invalid = true;
			}
			else if (!__instance.m_noPlacementCost && !__instance.HaveRequirements(_fakeResourcePiece, Player.RequirementMode.CanBuild))
			{
				invalid = true;
			}

			_placementGhosts[i]!.GetComponent<Piece>().SetInvalidPlacementHeightlight(invalid);
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
			}

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

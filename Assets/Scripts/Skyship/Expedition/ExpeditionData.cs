using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    // ============================================================================================
    // VERTICAL-SLICE DATA DEFINITIONS
    //
    // Plain [Serializable] data classes with a static, code-defined database (no .asset files, so
    // the whole slice works without any editor authoring — matching the project's procedural
    // style). If/when the campaign grows, these can be promoted to ScriptableObjects and the
    // static databases replaced by asset loading without touching any consumer code: everything
    // goes through ExpeditionDatabase/CargoDatabase lookups by string id.
    // ============================================================================================

    /// <summary>A named sky region: ambience + baseline danger for expeditions set there.</summary>
    [Serializable]
    public class SkyRegionDefinition
    {
        public string regionId;
        public string displayName;
        [TextArea] public string description;
        public int dangerBase = 1;

        [Tooltip("Fog tint applied on top of WorldGenerator's default ambience.")]
        public Color fogColor = new Color(0.65f, 0.72f, 0.82f);
        [Tooltip("Multiplier on the generator's fog start/end distances (lower = murkier region).")]
        public float fogDistanceScale = 1f;
    }

    /// <summary>What kind of goal an expedition asks the crew to complete.</summary>
    public enum ObjectiveKind
    {
        /// <summary>Recover ONE specific objective cargo item and get it onto the player ship.</summary>
        RecoverObjectiveItem,
        /// <summary>Load N cargo items of a given definition onto the player ship.</summary>
        CollectCargoCount
    }

    /// <summary>One expedition goal, checked host-authoritatively by ExpeditionManager.</summary>
    [Serializable]
    public class ObjectiveDefinition
    {
        public string objectiveId;
        public ObjectiveKind kind;
        [TextArea] public string description;
        [Tooltip("CargoDefinition id the objective counts (the objective item, or the collectible).")]
        public string targetCargoDefinitionId;
        [Tooltip("How many matching items must be on the ship (1 for RecoverObjectiveItem).")]
        public int requiredCount = 1;
    }

    /// <summary>What completing an expedition pays out (host applies to PlayerProgressState).</summary>
    [Serializable]
    public class RewardDefinition
    {
        public int money;
        public int scrap;
        public float fuel;
        public int chartFragments;
        [Tooltip("Expedition ids revealed on the Sky Chart when this reward is granted.")]
        public List<string> unlockExpeditionIds = new List<string>();
    }

    /// <summary>Which kind of mission site the generator must guarantee near the ship spawn.</summary>
    public enum MissionSiteType
    {
        None,
        Derelict
    }

    /// <summary>A selectable expedition on the Sky Chart. Data only — no scene logic.</summary>
    [Serializable]
    public class ExpeditionDefinition
    {
        public string expeditionId;
        public string title;
        [TextArea] public string description;
        public string regionId;
        [Range(1, 5)] public int dangerRating = 1;
        public float fuelCost = 1f;
        public MissionSiteType requiredSiteType = MissionSiteType.Derelict;
        public ObjectiveDefinition objective;
        public RewardDefinition reward = new RewardDefinition();
        [Tooltip("Generation/threat modifier tags (future: storms, patrols...). Informational for the slice.")]
        public List<string> modifiers = new List<string>();

        [Tooltip("Unlocked from a fresh save (no prior completion needed).")]
        public bool unlockedByDefault;
        [Tooltip("Visible when unlocked but not launchable yet (campaign scaffolding).")]
        public bool isPlaceholder;

        public SkyRegionDefinition Region => ExpeditionDatabase.GetRegion(regionId);
    }

    /// <summary>
    /// Everything the procedural generator needs to build a mission space. Built on EVERY peer
    /// from synced state (expedition id + world seed arrive in the StartGame packet), so
    /// generation stays fully deterministic with no host/client branching.
    /// </summary>
    [Serializable]
    public class ExpeditionGenerationRequest
    {
        public int seed;
        public ExpeditionDefinition expedition;
        public SkyRegionDefinition region;
        public int dangerRating;
        public MissionSiteType requiredSiteType;
        public List<string> modifiers = new List<string>();
    }

    /// <summary>
    /// Static, code-defined content database for the vertical slice: one region ("Trade Winds")
    /// and the three starter expeditions + the locked follow-up lead. Data-driven consumers only
    /// ever look things up by id, so swapping this for ScriptableObject assets later is painless.
    /// </summary>
    public static class ExpeditionDatabase
    {
        public const string RegionTradeWinds = "trade_winds";

        public const string ExpMerchantWreck = "merchant_wreck";
        public const string ExpFuelBarge = "fuel_barge";
        public const string ExpDeadRadioSignal = "dead_radio_signal";
        public const string ExpFloatingChapel = "floating_chapel_signal";

        private static readonly Dictionary<string, SkyRegionDefinition> regions = new Dictionary<string, SkyRegionDefinition>();
        private static readonly List<ExpeditionDefinition> expeditions = new List<ExpeditionDefinition>();
        private static bool built;

        public static IReadOnlyList<ExpeditionDefinition> AllExpeditions { get { Build(); return expeditions; } }

        public static SkyRegionDefinition GetRegion(string id)
        {
            Build();
            return id != null && regions.TryGetValue(id, out var r) ? r : null;
        }

        public static ExpeditionDefinition GetExpedition(string id)
        {
            Build();
            if (string.IsNullOrEmpty(id)) return null;
            for (int i = 0; i < expeditions.Count; i++)
                if (expeditions[i].expeditionId == id) return expeditions[i];
            return null;
        }

        private static void Build()
        {
            if (built) return;
            built = true;

            regions[RegionTradeWinds] = new SkyRegionDefinition
            {
                regionId = RegionTradeWinds,
                displayName = "Trade Winds",
                description = "Once-busy merchant lanes. Calm air, scattered wrecks, and radio ghosts.",
                dangerBase = 1,
                fogColor = new Color(0.65f, 0.72f, 0.82f),
                fogDistanceScale = 1f
            };

            expeditions.Add(new ExpeditionDefinition
            {
                expeditionId = ExpMerchantWreck,
                title = "Merchant Wreck",
                description = "A trade barque broke up along the lane. Strip her holds before the wind does.",
                regionId = RegionTradeWinds,
                dangerRating = 1,
                fuelCost = 2f,
                requiredSiteType = MissionSiteType.Derelict,
                unlockedByDefault = true,
                objective = new ObjectiveDefinition
                {
                    objectiveId = "obj_merchant_salvage",
                    kind = ObjectiveKind.CollectCargoCount,
                    description = "Load 3 salvage crates from the wreck onto your ship.",
                    targetCargoDefinitionId = CargoDatabase.SalvageCrate,
                    requiredCount = 3
                },
                reward = new RewardDefinition { money = 60, scrap = 20, chartFragments = 1 }
            });

            expeditions.Add(new ExpeditionDefinition
            {
                expeditionId = ExpFuelBarge,
                title = "Fuel Barge Debris",
                description = "A tanker barge went down with her cells intact. Free fuel, if you can lift it.",
                regionId = RegionTradeWinds,
                dangerRating = 1,
                fuelCost = 1f,
                requiredSiteType = MissionSiteType.Derelict,
                unlockedByDefault = true,
                objective = new ObjectiveDefinition
                {
                    objectiveId = "obj_fuel_cells",
                    kind = ObjectiveKind.CollectCargoCount,
                    description = "Load 2 fuel cells from the barge onto your ship.",
                    targetCargoDefinitionId = CargoDatabase.FuelCell,
                    requiredCount = 2
                },
                reward = new RewardDefinition { money = 30, fuel = 2f }
            });

            expeditions.Add(new ExpeditionDefinition
            {
                expeditionId = ExpDeadRadioSignal,
                title = "Dead Radio Signal",
                description = "A derelict is still broadcasting on a band that was retired thirty years ago. " +
                              "Find her, pull the navigation box, and bring it home. Don't listen too long.",
                regionId = RegionTradeWinds,
                dangerRating = 2,
                fuelCost = 2f,
                requiredSiteType = MissionSiteType.Derelict,
                unlockedByDefault = true,
                objective = new ObjectiveDefinition
                {
                    objectiveId = "obj_black_nav_box",
                    kind = ObjectiveKind.RecoverObjectiveItem,
                    description = "Recover the Black Navigation Box and load it onto your ship.",
                    targetCargoDefinitionId = CargoDatabase.BlackNavigationBox,
                    requiredCount = 1
                },
                reward = new RewardDefinition
                {
                    money = 120,
                    scrap = 30,
                    chartFragments = 2,
                    unlockExpeditionIds = new List<string> { ExpFloatingChapel }
                },
                modifiers = new List<string> { "alarm_on_pickup", "storm_on_recovery" }
            });

            // Campaign scaffolding: unlocked by Dead Radio Signal, visible but not launchable yet.
            expeditions.Add(new ExpeditionDefinition
            {
                expeditionId = ExpFloatingChapel,
                title = "Floating Chapel Signal",
                description = "The navigation box keeps repeating one set of coordinates: a chapel " +
                              "that shouldn't be able to float. (Coming soon.)",
                regionId = RegionTradeWinds,
                dangerRating = 3,
                fuelCost = 3f,
                isPlaceholder = true,
                objective = new ObjectiveDefinition
                {
                    objectiveId = "obj_chapel_placeholder",
                    kind = ObjectiveKind.RecoverObjectiveItem,
                    description = "???",
                    targetCargoDefinitionId = CargoDatabase.BlackNavigationBox,
                    requiredCount = 1
                }
            });
        }
    }
}

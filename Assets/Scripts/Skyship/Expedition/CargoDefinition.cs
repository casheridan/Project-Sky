using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Data definition for a gameplay cargo type. EXTENDS the existing weighted-cargo prototype:
    /// CargoItem keeps its weight/value/category behavior (ship balance reads it unchanged) and a
    /// definition simply stamps those fields plus the new gameplay flags via ApplyTo().
    /// </summary>
    [Serializable]
    public class CargoDefinition
    {
        public string cargoId;
        public string displayName;
        public float weight = 20f;
        public float value = 25f;
        [Tooltip("Maps onto the prototype CargoCategory so all existing systems keep working.")]
        public CargoCategory cargoType = CargoCategory.Generic;
        public bool isObjectiveItem;
        [Tooltip("Eldritch corruption carried aboard. Feeds the threat director.")]
        public float corruptionValue;
        [Tooltip("Extra strain this item puts on the ship beyond raw weight (future stability sim).")]
        public float stabilityImpact;
        public bool canBeSold = true;
        public bool canBeStudied;
        [Tooltip("Reserved: installable ship module (unused in the vertical slice).")]
        public bool canBeInstalled;

        [Tooltip("Cube tint for the placeholder visuals.")]
        public Color color = Color.gray;
        [Tooltip("Uniform cube scale for the placeholder visuals.")]
        public float visualScale = 0.8f;
        public bool emissive;

        /// <summary>Stamp this definition onto a prototype CargoItem (visuals are the caller's job).</summary>
        public void ApplyTo(CargoItem item)
        {
            if (item == null) return;
            item.itemName = displayName;
            item.category = cargoType;
            item.weight = weight;
            item.value = value;
            item.definitionId = cargoId;
            item.isObjectiveItem = isObjectiveItem;
            item.corruptionValue = corruptionValue;
            item.stabilityImpact = stabilityImpact;
        }
    }

    /// <summary>
    /// Static, code-defined cargo database for the vertical slice (see ExpeditionDatabase for the
    /// promotion-to-ScriptableObject plan). Also maps the prototype's CargoCategory cubes onto
    /// definitions so legacy world loot participates in the reward economy.
    /// </summary>
    public static class CargoDatabase
    {
        public const string SmallScrapCrate = "scrap_small";
        public const string MediumScrapCrate = "scrap_medium";
        public const string SalvageCrate = "salvage_crate";
        public const string FuelCell = "fuel_cell";
        public const string BlackNavigationBox = "black_nav_box";
        public const string HeavyCursedStatue = "cursed_statue";

        private static readonly Dictionary<string, CargoDefinition> defs = new Dictionary<string, CargoDefinition>();
        private static bool built;

        public static CargoDefinition Get(string id)
        {
            Build();
            return id != null && defs.TryGetValue(id, out var d) ? d : null;
        }

        /// <summary>
        /// Best-effort definition for a prototype cube that was spawned by category only
        /// (legacy world loot / harvest crates). Null = keep the cube's own fields.
        /// </summary>
        public static CargoDefinition ForLegacyCategory(CargoCategory cat)
        {
            Build();
            switch (cat)
            {
                case CargoCategory.Fuel: return defs[FuelCell];
                case CargoCategory.RepairCargo: return defs[MediumScrapCrate];
                case CargoCategory.RawResource: return defs[SmallScrapCrate];
                default: return null;
            }
        }

        private static void Build()
        {
            if (built) return;
            built = true;

            Add(new CargoDefinition
            {
                cargoId = SmallScrapCrate,
                displayName = "Small Scrap Crate",
                weight = 12f,
                value = 15f,
                cargoType = CargoCategory.RawResource,
                color = new Color(0.55f, 0.48f, 0.40f)
            });

            Add(new CargoDefinition
            {
                cargoId = MediumScrapCrate,
                displayName = "Medium Scrap Crate",
                weight = 25f,
                value = 35f,
                cargoType = CargoCategory.RepairCargo,
                color = new Color(0.45f, 0.40f, 0.34f),
                visualScale = 1.0f
            });

            Add(new CargoDefinition
            {
                cargoId = SalvageCrate,
                displayName = "Salvage Crate",
                weight = 30f,
                value = 45f,
                cargoType = CargoCategory.SpecialCargo,
                color = new Color(0.75f, 0.58f, 0.22f),
                visualScale = 1.0f
            });

            Add(new CargoDefinition
            {
                cargoId = FuelCell,
                displayName = "Fuel Cell",
                weight = 18f,
                value = 30f,
                cargoType = CargoCategory.Fuel,
                color = new Color(0.20f, 0.75f, 0.35f),
                emissive = true,
                visualScale = 0.7f
            });

            Add(new CargoDefinition
            {
                cargoId = BlackNavigationBox,
                displayName = "Black Navigation Box",
                weight = 35f,
                value = 0f,
                cargoType = CargoCategory.SpecialCargo,
                isObjectiveItem = true,
                corruptionValue = 3f,
                stabilityImpact = 1f,
                canBeSold = false,
                canBeStudied = true,
                canBeInstalled = true, // future: slot it into the helm to read its coordinates
                color = new Color(0.06f, 0.06f, 0.08f),
                emissive = true,
                visualScale = 0.9f
            });

            Add(new CargoDefinition
            {
                cargoId = HeavyCursedStatue,
                displayName = "Heavy Cursed Statue",
                weight = 90f,
                value = 400f,
                cargoType = CargoCategory.Treasure,
                corruptionValue = 5f,
                stabilityImpact = 2f,
                canBeStudied = true,
                color = new Color(0.30f, 0.16f, 0.38f),
                emissive = true,
                visualScale = 1.1f
            });
        }

        private static void Add(CargoDefinition d) => defs[d.cargoId] = d;
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>Which kind of structure a map-registry entry describes (consumed by the ship's map table).</summary>
    public enum MapStructureKind
    {
        IslandVeryLarge,
        IslandLarge,
        IslandMedium,
        IslandSmall,
        Derelict
    }

    /// <summary>One placed structure, recorded for the ship's map-table diorama.</summary>
    [Serializable]
    public struct MapStructureInfo
    {
        public string name;
        public Vector3 position;
        public float radius;
        public MapStructureKind kind;
    }

    /// <summary>
    /// Procedurally fills the airspace with floating islands in four size tiers (1 very large,
    /// 2-3 large, 6-7 medium, 9-12 small per map) and 2-4 derelict ships, spaced kilometres apart.
    /// Island bodies are vertex-displaced meshes (IslandMeshBuilder): gently bumpy walkable tops,
    /// craggy undersides tapering to a point, decorated with hanging crystals, roots, and rock
    /// chunks. Tops carry steep non-climbable rocks; raw resources come from harvestable nodes
    /// (see ResourceNode), with only occasional loose fuel/treasure crates. Derelicts keep their
    /// procedurally-laid-out interiors (grid of rooms joined by doorways, debris obstacles).
    ///
    /// NETWORKING: generation is fully deterministic from a single integer seed. The host picks a
    /// world seed and broadcasts it in the StartGame packet (see NetworkManagerP2P); every peer then
    /// generates the IDENTICAL layout locally, so the static geometry needs no per-object sync. Only
    /// the cargo (which can be picked up and moved) is synchronized, via the existing host-authoritative
    /// cargo sync — which works because deterministic generation gives every crate the same NAME on
    /// every machine. Determinism relies on a per-instance System.Random (never UnityEngine.Random,
    /// which is shared global state), an identical call order on all peers, and NO branching on isHost
    /// inside generation (the only host/client difference is whether a finished crate is left kinematic).
    /// </summary>
    public class WorldGenerator : MonoBehaviour
    {
        [Header("Seed")]
        [Tooltip("0 = use the networked world seed (host-assigned); falls back to a random seed in solo play. " +
                 "Set non-zero to force a fixed layout for testing.")]
        public int seedOverride = 0;

        [Header("World Volume (centered on this GameObject)")]
        [Tooltip("Structures are scattered inside this box. Sea-of-Thieves-ish scale: kilometres across, " +
                 "with the Y range spreading islands over the altitudes you climb/descend to reach.")]
        public Vector3 worldExtent = new Vector3(8000f, 700f, 8000f);
        [Tooltip("Minimum EDGE-to-edge gap between structures (spacing check is radiusA + radiusB + this).")]
        public float baseIslandGap = 600f;
        [Tooltip("Keep this horizontal radius around the world origin (the ship spawn) empty.")]
        public float spawnClearRadius = 400f;
        [Tooltip("How many placement attempts per structure before giving up on that one.")]
        public int placementAttempts = 60;

        [Header("Island Tiers (per map)")]
        public Vector2Int veryLargeCount = new Vector2Int(1, 1);
        public Vector2 veryLargeRadius = new Vector2(220f, 280f);
        public Vector2Int largeCount = new Vector2Int(2, 3);
        public Vector2 largeRadius = new Vector2(140f, 180f);
        public Vector2Int mediumCount = new Vector2Int(6, 7);
        public Vector2 mediumRadius = new Vector2(70f, 110f);
        public Vector2Int smallCount = new Vector2Int(9, 12);
        public Vector2 smallRadius = new Vector2(30f, 55f);

        [Header("Island Loose Loot (fuel/treasure only — raw resources come from nodes)")]
        [Tooltip("Min/max loose crates per island. Raw resources are NOT in this pool.")]
        public Vector2Int looseLootPerIsland = new Vector2Int(0, 2);
        [Range(0f, 1f)]
        [Tooltip("Chance a loose crate is Treasure; otherwise it's Fuel.")]
        public float looseTreasureChance = 0.35f;

        [Header("Resource Nodes (harvestable rocks, per island by tier)")]
        public Vector2Int nodesSmallIsland = new Vector2Int(1, 2);
        public Vector2Int nodesMediumIsland = new Vector2Int(2, 4);
        public Vector2Int nodesLargeIsland = new Vector2Int(4, 6);
        public Vector2Int nodesVeryLargeIsland = new Vector2Int(6, 9);
        [Tooltip("Min/max total crates a single node yields before it's spent.")]
        public Vector2Int nodeYieldRange = new Vector2Int(3, 8);

        [Header("Derelicts (sparse, room-filled)")]
        public Vector2Int derelictCountRange = new Vector2Int(2, 4);
        [Tooltip("Min/max hull length (along Z) of a derelict ship.")]
        public Vector2 derelictLength = new Vector2(48f, 78f);
        [Tooltip("Min/max hull width (along X) of a derelict ship.")]
        public Vector2 derelictWidth = new Vector2(16f, 26f);
        [Tooltip("Interior floor-to-rail height of a derelict deck.")]
        public float derelictDeckHeight = 5f;
        [Tooltip("Min/max crates per derelict (repair/special/fuel/treasure), scattered through the rooms.")]
        public Vector2Int cargoPerDerelict = new Vector2Int(5, 11);

        [Header("Ambience (applied at runtime on every peer)")]
        [Tooltip("Camera far clip needed to see distant islands across the world volume.")]
        public float cameraFarClip = 6500f;
        public float fogStartDistance = 2500f;
        public float fogEndDistance = 6000f;
        public Color fogColor = new Color(0.65f, 0.72f, 0.82f);

        [Header("Runtime (read-only)")]
        [SerializeField] private int usedSeed;
        [SerializeField] private int spawnedIslands;
        [SerializeField] private int spawnedDerelicts;
        [SerializeField] private int spawnedNodes;
        [SerializeField] private int spawnedCargo;
        [SerializeField] private int spawnedPrimitives;

        private System.Random rng;
        private int cargoCounter;
        private int nodeCounter;
        private int terrainLayer = -1; // structures go on this layer so the ship's hull collides with them

        private struct PlacedEntry { public Vector3 pos; public float radius; }
        private readonly List<PlacedEntry> placed = new List<PlacedEntry>();

        private readonly List<MapStructureInfo> structures = new List<MapStructureInfo>();
        /// <summary>Every placed structure (position/radius/kind), for the ship's map table.</summary>
        public IReadOnlyList<MapStructureInfo> Structures => structures;

        private Material rockMat, rockDarkMat, hullMat, wallMat, debrisMat;
        private Material islandTopMat, islandUnderMat, crystalMat, rootMat, stoneMat, oreMat;
        private readonly Dictionary<CargoCategory, Material> cargoMats = new Dictionary<CargoCategory, Material>();

        private void Start()
        {
            Generate();
        }

        /// <summary>Build the whole world. Deterministic for a given seed.</summary>
        public void Generate()
        {
            var nm = NetworkManagerP2P.Instance;
            usedSeed = seedOverride != 0 ? seedOverride
                     : (nm != null && nm.worldSeed != 0) ? nm.worldSeed
                     : Environment.TickCount;

            rng = new System.Random(usedSeed);
            cargoCounter = 0;
            nodeCounter = 0;
            spawnedIslands = spawnedDerelicts = spawnedNodes = spawnedCargo = spawnedPrimitives = 0;
            placed.Clear();
            structures.Clear();
            BuildMaterials();
            // Islands/derelicts go on the "Terrain" layer so the ship's hull collides with them
            // (loose cargo stays on the default layer, so it doesn't block the ship).
            terrainLayer = LayerMask.NameToLayer("Terrain");

            // Fixed order (identical on every peer): biggest islands first for packing, then derelicts.
            SpawnIslandTier(MapStructureKind.IslandVeryLarge, veryLargeCount, veryLargeRadius);
            SpawnIslandTier(MapStructureKind.IslandLarge, largeCount, largeRadius);
            SpawnIslandTier(MapStructureKind.IslandMedium, mediumCount, mediumRadius);
            SpawnIslandTier(MapStructureKind.IslandSmall, smallCount, smallRadius);

            int derelicts = RandInt(derelictCountRange.x, derelictCountRange.y);
            for (int i = 0; i < derelicts; i++) SpawnDerelict(i);

            ApplyAmbience();

            // Build the lower-cabin map diorama from everything we just placed.
            ShipMapTable.CreateOnShip(structures, worldExtent, transform.position);

            // Cargo was created after the network manager cached this scene's objects; refresh so the
            // host syncs every crate (and clients can resolve them by name).
            if (nm != null) nm.RefreshCargoRegistry();

            Debug.Log($"[WorldGenerator] seed={usedSeed}: {spawnedIslands} islands, {spawnedDerelicts} derelicts, " +
                      $"{spawnedNodes} nodes, {spawnedCargo} cargo, {spawnedPrimitives} primitives.");
        }

        /// <summary>Distant islands need a long draw distance; linear fog sells the scale and hides the clip.</summary>
        private void ApplyAmbience()
        {
            var cam = Camera.main;
            if (cam != null) cam.farClipPlane = cameraFarClip;

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = fogStartDistance;
            RenderSettings.fogEndDistance = fogEndDistance;
            RenderSettings.fogColor = fogColor;
        }

        // ========================================================================================
        // ISLANDS — mesh landmasses with bumpy walkable tops and craggy, decorated undersides
        // ========================================================================================
        private void SpawnIslandTier(MapStructureKind kind, Vector2Int countRange, Vector2 radiusRange)
        {
            int count = RandInt(countRange.x, countRange.y);
            for (int i = 0; i < count; i++)
                SpawnIsland(kind, radiusRange);
        }

        private void SpawnIsland(MapStructureKind kind, Vector2 radiusRange)
        {
            float radius = RandRange(radiusRange.x, radiusRange.y);
            if (!TryPickPoint(radius, out Vector3 center)) return;

            var island = new GameObject($"Island_{spawnedIslands:00}");
            island.transform.position = center;
            island.transform.SetParent(transform, true);

            IslandMeshBuilder.Surface surface;
            float underDepth = BuildIslandBody(island.transform, radius, out surface);
            BuildIslandRocks(island.transform, radius, surface);
            BuildIslandUnderside(island.transform, radius, underDepth, surface);
            SetLayerRecursive(island, terrainLayer); // solid terrain (cargo, added next, stays default)
            ScatterResourceNodes(island.transform, radius, surface, kind);
            ScatterLooseLoot(island.transform, radius, surface);

            structures.Add(new MapStructureInfo
            {
                name = island.name,
                position = center,
                radius = radius,
                kind = kind
            });
            spawnedIslands++;
        }

        /// <summary>The landmass mesh: bumpy walkable top (surface ~localY 0) + tapering underside.</summary>
        private float BuildIslandBody(Transform island, float radius, out IslandMeshBuilder.Surface surface)
        {
            float sizeT = Mathf.InverseLerp(smallRadius.x, veryLargeRadius.y, radius);
            var p = new IslandMeshBuilder.Params
            {
                radius = radius,
                topRings = Mathf.Clamp(8 + Mathf.RoundToInt(radius * 0.06f), 10, 24),
                segments = Mathf.Clamp(16 + Mathf.RoundToInt(radius * 0.12f), 20, 48),
                underRings = 8,
                heightAmp = Mathf.Lerp(1.5f, 4f, sizeT),
                rimDip = Mathf.Lerp(0.8f, 2.5f, sizeT),
                underDepth = radius * RandRange(0.9f, 1.3f),
                outlineWobble = 0.22f,
                underJag = 0.18f
            };

            Mesh mesh = IslandMeshBuilder.Build(rng, p, out surface);

            var body = new GameObject("IslandBody");
            body.transform.SetParent(island, false);
            body.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = body.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { islandTopMat, islandUnderMat }; // submesh 0 = top, 1 = underside
            body.AddComponent<MeshCollider>().sharedMesh = mesh;         // static, non-convex
            spawnedPrimitives++;

            return p.underDepth;
        }

        /// <summary>
        /// Steep, non-climbable rocks on the top surface: near-vertical spires and tall upright
        /// boulders. Kept upright (yaw-only for boulders) so no face forms a walkable ramp —
        /// the CharacterController's 45° slope limit rejects them.
        /// </summary>
        private void BuildIslandRocks(Transform island, float radius, IslandMeshBuilder.Surface surface)
        {
            int spires = RandInt(3, 6) + Mathf.RoundToInt(radius / 60f);
            for (int s = 0; s < spires; s++)
            {
                Vector2 p = RandInsideDisc(radius * 0.8f);
                float groundY = surface.HeightAt(p.x, p.y);
                float h = RandRange(6f, 10f + radius * 0.06f);
                float r = RandRange(1.2f, 2.5f + radius * 0.01f);
                Cyl(island, new Vector3(p.x, groundY + h * 0.5f - 0.5f, p.y), h, r,
                    new Vector3(RandRange(-5f, 5f), RandRange(0f, 360f), RandRange(-5f, 5f)),
                    rockMat, "Spire");
            }

            int boulders = RandInt(4, 8) + Mathf.RoundToInt(radius / 50f);
            for (int b = 0; b < boulders; b++)
            {
                Vector2 p = RandInsideDisc(radius * 0.85f);
                float groundY = surface.HeightAt(p.x, p.y);
                float w = RandRange(2f, 4f + radius * 0.02f);
                float h = RandRange(3f, 6f);
                Box(island, new Vector3(p.x, groundY + h * 0.5f - 0.4f, p.y),
                    new Vector3(w * RandRange(0.8f, 1.3f), h, w * RandRange(0.8f, 1.3f)),
                    new Vector3(0f, RandRange(0f, 360f), 0f), // yaw only: sides stay vertical
                    rockDarkMat, "Boulder");
            }
        }

        /// <summary>Hanging decoration under the island: crystals, root strands, embedded rock chunks.</summary>
        private void BuildIslandUnderside(Transform island, float radius, float underDepth,
                                          IslandMeshBuilder.Surface surface)
        {
            // Approximates the underside cone's radius at a depth fraction (mirrors the mesh's shrink curve).
            float ConeRadiusAt(float angle, float u) => surface.OutlineRadius(angle) * Mathf.Pow(1f - u, 1.35f);
            float ConeYAt(float u) => -underDepth * Mathf.Pow(u, 1.15f);

            // Hanging crystals — bright shards poking out of the cone, pointing down and outward.
            int crystals = RandInt(3, 5) + Mathf.RoundToInt(radius / 70f);
            for (int c = 0; c < crystals; c++)
            {
                float ang = RandRange(0f, Mathf.PI * 2f);
                float u = RandRange(0.15f, 0.55f);
                float rc = ConeRadiusAt(ang, u) * 0.85f;
                var pos = new Vector3(Mathf.Cos(ang) * rc, ConeYAt(u), Mathf.Sin(ang) * rc);

                float len = Mathf.Clamp(radius * RandRange(0.12f, 0.28f), 4f, 30f);
                Mesh crystal = IslandMeshBuilder.BuildCrystal(rng, len, len * RandRange(0.16f, 0.24f));

                var go = new GameObject("Crystal");
                go.transform.SetParent(island, false);
                go.transform.localPosition = pos;
                // +Y-built shard flipped to hang: point down with an outward/random lean.
                go.transform.localRotation = Quaternion.Euler(
                    180f + RandRange(-22f, 22f), RandRange(0f, 360f), RandRange(-22f, 22f));
                go.AddComponent<MeshFilter>().sharedMesh = crystal;
                go.AddComponent<MeshRenderer>().sharedMaterial = crystalMat;
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = crystal;
                mc.convex = true; // 14 verts — cheap
                spawnedPrimitives++;
            }

            // Root strands — chains of shrinking cylinders drooping from near the rim.
            int roots = RandInt(2, 4) + Mathf.RoundToInt(radius / 90f);
            for (int r = 0; r < roots; r++)
            {
                float ang = RandRange(0f, Mathf.PI * 2f);
                float rr = surface.OutlineRadius(ang) * RandRange(0.55f, 0.85f);
                Vector3 tip = new Vector3(Mathf.Cos(ang) * rr, -radius * 0.04f, Mathf.Sin(ang) * rr);
                Vector3 dir = Vector3.down;
                int segs = RandInt(3, 5);
                float segLen = radius * RandRange(0.07f, 0.12f);
                float thick = Mathf.Clamp(radius * 0.015f, 0.4f, 3f);
                for (int i = 0; i < segs; i++)
                {
                    float t = i / (float)segs;
                    Vector3 mid = tip + dir * (segLen * 0.5f);
                    var seg = Cyl(island, mid, segLen, thick * (1f - t * 0.7f), Vector3.zero, rootMat, "Root");
                    seg.transform.localRotation = Quaternion.FromToRotation(Vector3.up, -dir);
                    tip += dir * segLen;
                    dir = (dir + new Vector3(RandRange(-0.3f, 0.3f), 0f, RandRange(-0.3f, 0.3f))).normalized;
                }
            }

            // Embedded rock chunks — dark boxes jutting from the cone surface.
            int chunks = RandInt(2, 5) + Mathf.RoundToInt(radius / 80f);
            for (int k = 0; k < chunks; k++)
            {
                float ang = RandRange(0f, Mathf.PI * 2f);
                float u = RandRange(0.1f, 0.5f);
                float rc = ConeRadiusAt(ang, u) * 0.95f;
                float size = radius * RandRange(0.08f, 0.16f);
                Box(island, new Vector3(Mathf.Cos(ang) * rc, ConeYAt(u), Mathf.Sin(ang) * rc),
                    new Vector3(size, size * RandRange(0.6f, 1f), size),
                    new Vector3(RandRange(0f, 360f), RandRange(0f, 360f), RandRange(0f, 360f)),
                    rockDarkMat, "Chunk");
            }
        }

        /// <summary>
        /// Harvestable resource nodes: each island rolls a dominant resource flavor, then scatters
        /// tier-scaled node counts across its top. All rolls come from the shared rng, so node
        /// placement, type, and total yield are identical on every peer.
        /// </summary>
        private void ScatterResourceNodes(Transform island, float radius,
                                          IslandMeshBuilder.Surface surface, MapStructureKind kind)
        {
            Vector2Int range;
            switch (kind)
            {
                case MapStructureKind.IslandVeryLarge: range = nodesVeryLargeIsland; break;
                case MapStructureKind.IslandLarge: range = nodesLargeIsland; break;
                case MapStructureKind.IslandMedium: range = nodesMediumIsland; break;
                default: range = nodesSmallIsland; break;
            }

            int count = RandInt(range.x, range.y);
            var dominant = (ResourceNodeType)rng.Next(0, 3); // the island's resource theme
            for (int n = 0; n < count; n++)
            {
                var type = rng.NextDouble() < 0.6 ? dominant : (ResourceNodeType)rng.Next(0, 3);
                Vector2 p = RandInsideDisc(radius * 0.75f);
                float y = surface.HeightAt(p.x, p.y);
                BuildResourceNode(island, new Vector3(p.x, y, p.y), type);
            }
        }

        /// <summary>A node's body: a low cluster of dark rocks with type-colored crystal veins on top.</summary>
        private void BuildResourceNode(Transform island, Vector3 localPos, ResourceNodeType type)
        {
            nodeCounter++;
            var go = new GameObject($"WNode_{nodeCounter:0000}"); // deterministic -> harvest sync keys on this
            go.transform.SetParent(island, false);
            go.transform.localPosition = localPos;
            go.transform.localRotation = Quaternion.Euler(0f, RandRange(0f, 360f), 0f);

            int rocks = RandInt(3, 5);
            for (int r = 0; r < rocks; r++)
            {
                float s = RandRange(0.7f, 1.4f);
                Box(go.transform,
                    new Vector3(RandRange(-0.7f, 0.7f), s * 0.3f, RandRange(-0.7f, 0.7f)),
                    new Vector3(s, s * RandRange(0.6f, 0.9f), s),
                    new Vector3(RandRange(0f, 360f), RandRange(0f, 360f), RandRange(0f, 360f)),
                    r % 2 == 0 ? rockDarkMat : rockMat, "NodeRock");
            }

            Material veinMat = type == ResourceNodeType.Stone ? stoneMat
                             : type == ResourceNodeType.Ore ? oreMat : crystalMat;
            var veins = new List<Transform>();
            int veinCount = RandInt(2, 4);
            for (int v = 0; v < veinCount; v++)
            {
                Mesh shard = IslandMeshBuilder.BuildCrystal(rng, RandRange(0.6f, 1.1f), RandRange(0.14f, 0.22f));
                var vein = new GameObject("Vein");
                vein.transform.SetParent(go.transform, false);
                vein.transform.localPosition = new Vector3(RandRange(-0.6f, 0.6f), RandRange(0.5f, 0.9f), RandRange(-0.6f, 0.6f));
                vein.transform.localRotation = Quaternion.Euler(RandRange(-35f, 35f), RandRange(0f, 360f), RandRange(-35f, 35f));
                vein.AddComponent<MeshFilter>().sharedMesh = shard;
                vein.AddComponent<MeshRenderer>().sharedMaterial = veinMat; // visual only, no collider
                veins.Add(vein.transform);
                spawnedPrimitives++;
            }

            var node = go.AddComponent<ResourceNode>();
            node.Initialize(go.name, type, RandInt(nodeYieldRange.x, nodeYieldRange.y), veins);
            SetLayerRecursive(go, terrainLayer);
            spawnedNodes++;
        }

        private void ScatterLooseLoot(Transform island, float radius, IslandMeshBuilder.Surface surface)
        {
            int count = RandInt(looseLootPerIsland.x, looseLootPerIsland.y);
            for (int k = 0; k < count; k++)
            {
                CargoCategory cat = rng.NextDouble() < looseTreasureChance
                    ? CargoCategory.Treasure : CargoCategory.Fuel;
                Vector2 p = RandInsideDisc(radius * 0.7f);
                float y = surface.HeightAt(p.x, p.y) + 1.5f;
                SpawnCargo(island.TransformPoint(new Vector3(p.x, y, p.y)), cat);
            }
        }

        // ========================================================================================
        // DERELICTS — massive hulks with procedurally-laid-out room/corridor interiors
        // ========================================================================================
        private void SpawnDerelict(int index)
        {
            float length = RandRange(derelictLength.x, derelictLength.y);
            float width = RandRange(derelictWidth.x, derelictWidth.y);
            if (!TryPickPoint(length * 0.5f, out Vector3 center)) return;

            float h = derelictDeckHeight;

            var derelict = new GameObject($"Derelict_{index:00}");
            derelict.transform.position = center;
            // A gentle list/tilt — it's a wreck, but kept shallow so the interior stays walkable.
            derelict.transform.rotation = Quaternion.Euler(RandRange(-8f, 8f), RandRange(0f, 360f), RandRange(-6f, 6f));
            derelict.transform.SetParent(transform, true);

            BuildDerelictHull(derelict.transform, length, width, h);
            BuildDerelictInterior(derelict.transform, length, width, h);
            BuildDerelictSuperstructure(derelict.transform, length, width, h);
            SetLayerRecursive(derelict, terrainLayer); // solid terrain (cargo, added next, stays default)
            ScatterDerelictCargo(derelict.transform, length, width);

            structures.Add(new MapStructureInfo
            {
                name = derelict.name,
                position = center,
                radius = length * 0.5f,
                kind = MapStructureKind.Derelict
            });
            spawnedDerelicts++;
        }

        /// <summary>Floor slab, tapered bow, and an open-topped outer hull with a couple of breaches.</summary>
        private void BuildDerelictHull(Transform ship, float L, float W, float h)
        {
            float t = 0.7f; // wall / slab thickness

            // Main deck floor (you walk on its top surface at localY 0).
            Box(ship, new Vector3(0f, -t * 0.5f, 0f), new Vector3(W, t, L), Vector3.zero, hullMat, "Floor");

            // Tapered bow cap at +Z so it reads as a ship, not a box.
            Box(ship, new Vector3(0f, h * 0.35f, L * 0.5f + W * 0.18f),
                new Vector3(W * 0.6f, h * 0.7f, W * 0.36f),
                new Vector3(28f, 0f, 0f), hullMat, "Bow");

            // Outer hull walls (open-topped so the ship is reachable from above with vertical flight).
            // Two of the four sides get a breach gap so you can also walk in from the side.
            int breachSide = rng.Next(0, 4);
            int breachSide2 = (breachSide + 1 + rng.Next(0, 3)) % 4;
            float breach = Mathf.Min(W, L) * 0.35f;

            WallAlongX(ship, -L * 0.5f, 0f, W, h, t, (breachSide == 0 || breachSide2 == 0) ? breach : 0f, hullMat, "HullStern");
            WallAlongX(ship, L * 0.5f, 0f, W, h, t, (breachSide == 1 || breachSide2 == 1) ? breach : 0f, hullMat, "HullBow");
            WallAlongZ(ship, -W * 0.5f, 0f, L, h, t, (breachSide == 2 || breachSide2 == 2) ? breach : 0f, hullMat, "HullPort");
            WallAlongZ(ship, W * 0.5f, 0f, L, h, t, (breachSide == 3 || breachSide2 == 3) ? breach : 0f, hullMat, "HullStar");
        }

        /// <summary>
        /// Procedural interior: divide the deck into a grid of rooms, carve a connected set of doorways
        /// with a randomized spanning tree (guarantees every room reachable), add a few extra openings
        /// for loops, then build interior walls everywhere a doorway WASN'T carved. Scatter debris.
        /// </summary>
        private void BuildDerelictInterior(Transform ship, float L, float W, float h)
        {
            float t = 0.5f;
            int cols = RandInt(2, 3);                                   // across the width (X)
            int rows = RandInt(4, 6);                                   // along the length (Z)
            float cellW = W / cols;
            float cellL = L / rows;
            float doorGap = Mathf.Min(3.2f, cellW * 0.5f, cellL * 0.5f);

            // Doorway sets. openV[r,c] = opening between cell (r,c) and (r,c+1); openH[r,c] between (r,c) and (r+1,c).
            bool[,] openV = new bool[rows, Mathf.Max(1, cols - 1)];
            bool[,] openH = new bool[Mathf.Max(1, rows - 1), cols];
            CarveRoomConnections(rows, cols, openV, openH);

            float halfW = W * 0.5f, halfL = L * 0.5f;

            // Internal walls between columns (constant X lines), per row cell.
            for (int c = 1; c < cols; c++)
            {
                float x = -halfW + c * cellW;
                for (int r = 0; r < rows; r++)
                {
                    float zCenter = -halfL + (r + 0.5f) * cellL;
                    float gap = openV[r, c - 1] ? doorGap : 0f;
                    WallAlongZ(ship, x, zCenter, cellL, h, t, gap, wallMat, "WallV");
                }
            }

            // Internal walls between rows (constant Z lines), per column cell.
            for (int r = 1; r < rows; r++)
            {
                float z = -halfL + r * cellL;
                for (int c = 0; c < cols; c++)
                {
                    float xCenter = -halfW + (c + 0.5f) * cellW;
                    float gap = openH[r - 1, c] ? doorGap : 0f;
                    WallAlongX(ship, z, xCenter, cellW, h, t, gap, wallMat, "WallH");
                }
            }

            // Debris obstacles inside random rooms (toppled beams / rubble piles).
            int debris = RandInt(rows, rows * cols);
            for (int d = 0; d < debris; d++)
            {
                int r = rng.Next(0, rows);
                int c = rng.Next(0, cols);
                float cx = -halfW + (c + 0.5f) * cellW + RandRange(-cellW * 0.25f, cellW * 0.25f);
                float cz = -halfL + (r + 0.5f) * cellL + RandRange(-cellL * 0.25f, cellL * 0.25f);
                if (rng.NextDouble() < 0.5)
                {
                    // Toppled beam.
                    Box(ship, new Vector3(cx, 0.4f, cz),
                        new Vector3(RandRange(0.3f, 0.6f), RandRange(0.3f, 0.6f), RandRange(2f, cellL * 0.7f)),
                        new Vector3(0f, RandRange(0f, 360f), RandRange(-20f, 20f)), debrisMat, "Beam");
                }
                else
                {
                    // Rubble pile.
                    float s = RandRange(0.8f, 1.8f);
                    Box(ship, new Vector3(cx, s * 0.4f, cz), new Vector3(s, s * RandRange(0.5f, 0.9f), s),
                        new Vector3(RandRange(0f, 360f), RandRange(0f, 360f), RandRange(0f, 360f)), debrisMat, "Rubble");
                }
            }
        }

        /// <summary>A raised bridge/tower at the stern for silhouette and an upper landmark.</summary>
        private void BuildDerelictSuperstructure(Transform ship, float L, float W, float h)
        {
            float bw = W * 0.6f, bl = L * 0.16f, bh = h * 1.1f;
            float z = -L * 0.5f + bl * 0.7f;
            var bridge = new GameObject("Bridge");
            bridge.transform.SetParent(ship, false);
            bridge.transform.localPosition = new Vector3(0f, h, z);

            float t = 0.5f;
            Box(bridge.transform, new Vector3(0f, 0f, 0f), new Vector3(bw, t, bl), Vector3.zero, hullMat, "BridgeFloor");
            // Three walls + an open front, so it's a little room up top.
            WallAlongX(bridge.transform, -bl * 0.5f, 0f, bw, bh, t, 0f, wallMat, "BridgeBack");
            WallAlongZ(bridge.transform, -bw * 0.5f, 0f, bl, bh, t, bl * 0.4f, wallMat, "BridgeL");
            WallAlongZ(bridge.transform, bw * 0.5f, 0f, bl, bh, t, bl * 0.4f, wallMat, "BridgeR");
        }

        private void ScatterDerelictCargo(Transform ship, float L, float W)
        {
            CargoCategory[] pool =
            {
                CargoCategory.RepairCargo, CargoCategory.RepairCargo,
                CargoCategory.SpecialCargo, CargoCategory.Fuel, CargoCategory.Treasure
            };
            int count = RandInt(cargoPerDerelict.x, cargoPerDerelict.y);
            for (int k = 0; k < count; k++)
            {
                CargoCategory cat = pool[rng.Next(0, pool.Length)];
                Vector3 local = new Vector3(
                    RandRange(-W * 0.4f, W * 0.4f), 1.2f, RandRange(-L * 0.45f, L * 0.45f));
                SpawnCargo(ship.TransformPoint(local), cat);
            }
        }

        /// <summary>Randomized spanning tree (iterative DFS) over the room grid → guaranteed-connected
        /// doorways, plus a few extra openings for loops/corridors. Deterministic from rng.</summary>
        private void CarveRoomConnections(int rows, int cols, bool[,] openV, bool[,] openH)
        {
            if (rows * cols <= 1) return;

            bool[,] visited = new bool[rows, cols];
            var stack = new Stack<Vector2Int>();
            var start = new Vector2Int(rng.Next(0, rows), rng.Next(0, cols));
            visited[start.x, start.y] = true;
            stack.Push(start);

            var neighbors = new List<Vector2Int>(4);
            while (stack.Count > 0)
            {
                Vector2Int cur = stack.Peek();
                neighbors.Clear();
                if (cur.x > 0 && !visited[cur.x - 1, cur.y]) neighbors.Add(new Vector2Int(cur.x - 1, cur.y));
                if (cur.x < rows - 1 && !visited[cur.x + 1, cur.y]) neighbors.Add(new Vector2Int(cur.x + 1, cur.y));
                if (cur.y > 0 && !visited[cur.x, cur.y - 1]) neighbors.Add(new Vector2Int(cur.x, cur.y - 1));
                if (cur.y < cols - 1 && !visited[cur.x, cur.y + 1]) neighbors.Add(new Vector2Int(cur.x, cur.y + 1));

                if (neighbors.Count == 0) { stack.Pop(); continue; }

                Vector2Int next = neighbors[rng.Next(0, neighbors.Count)];
                OpenBetween(cur, next, openV, openH);
                visited[next.x, next.y] = true;
                stack.Push(next);
            }

            // Extra doorways for loops/corridors (~20% of remaining shared walls).
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols - 1; c++)
                    if (!openV[r, c] && rng.NextDouble() < 0.2) openV[r, c] = true;
            for (int r = 0; r < rows - 1; r++)
                for (int c = 0; c < cols; c++)
                    if (!openH[r, c] && rng.NextDouble() < 0.2) openH[r, c] = true;
        }

        private static void OpenBetween(Vector2Int a, Vector2Int b, bool[,] openV, bool[,] openH)
        {
            if (a.x == b.x)
            {
                int c = Mathf.Min(a.y, b.y);
                openV[a.x, c] = true;                 // vertical-wall opening between columns
            }
            else
            {
                int r = Mathf.Min(a.x, b.x);
                openH[r, a.y] = true;                 // horizontal-wall opening between rows
            }
        }

        // ========================================================================================
        // CARGO
        // ========================================================================================
        private void SpawnCargo(Vector3 worldPos, CargoCategory cat)
        {
            cargoCounter++;
            SpawnCargoNamed($"WCargo_{cargoCounter:0000}", cat, worldPos); // deterministic, unique name
        }

        /// <summary>
        /// Spawn a crate with an explicit (deterministic) name — used both during generation and at
        /// runtime by the resource-harvest flow, where every peer spawns the same crate from a packet.
        /// </summary>
        public CargoItem SpawnCargoNamed(string cargoName, CargoCategory cat, Vector3 worldPos)
        {
            if (cargoMats.Count == 0) BuildMaterials();

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = cargoName; // network cargo sync keys on this
            go.transform.position = worldPos;
            go.transform.localScale = Vector3.one * 0.8f;
            go.GetComponent<Renderer>().sharedMaterial = CargoMaterial(cat);
            spawnedPrimitives++;

            var item = go.AddComponent<CargoItem>(); // RequireComponent adds the Rigidbody
            item.category = cat;
            item.itemName = Readable(cat);
            item.weight = WeightFor(cat);
            item.value = ValueFor(cat);

            // On non-authoritative clients keep crates kinematic; the host drives their positions.
            var nm = NetworkManagerP2P.Instance;
            if (nm != null && !nm.IsWorldAuthority)
            {
                var rb = go.GetComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            spawnedCargo++;
            return item;
        }

        // ========================================================================================
        // PRIMITIVE / PLACEMENT HELPERS
        // ========================================================================================

        /// <summary>Spawn a cube under <paramref name="parent"/> with the given local transform + material.</summary>
        private GameObject Box(Transform parent, Vector3 localPos, Vector3 localScale, Vector3 localEuler, Material mat, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localEulerAngles = localEuler;
            go.transform.localScale = localScale;
            go.GetComponent<Renderer>().sharedMaterial = mat;
            spawnedPrimitives++;
            return go;
        }

        /// <summary>Spawn a cylinder (pillar/spire) of the given height + radius under <paramref name="parent"/>.</summary>
        private GameObject Cyl(Transform parent, Vector3 localPos, float height, float radius, Vector3 localEuler, Material mat, string name)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localEulerAngles = localEuler;
            go.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f); // primitive cylinder is 2 units tall
            go.GetComponent<Renderer>().sharedMaterial = mat;
            spawnedPrimitives++;
            return go;
        }

        /// <summary>Wall running along X at constant Z. Optional centered gap (doorway/breach); 0 = solid.</summary>
        private void WallAlongX(Transform parent, float z, float xCenter, float xLen, float height, float thickness, float gap, Material mat, string name)
        {
            if (gap > 0.1f && xLen > gap + 0.6f)
            {
                float seg = (xLen - gap) * 0.5f;
                float off = (xLen - seg) * 0.5f;
                Box(parent, new Vector3(xCenter - off, height * 0.5f, z), new Vector3(seg, height, thickness), Vector3.zero, mat, name);
                Box(parent, new Vector3(xCenter + off, height * 0.5f, z), new Vector3(seg, height, thickness), Vector3.zero, mat, name);
            }
            else if (gap <= 0.1f)
            {
                Box(parent, new Vector3(xCenter, height * 0.5f, z), new Vector3(xLen, height, thickness), Vector3.zero, mat, name);
            }
            // else: gap >= wall length -> fully open, build nothing
        }

        /// <summary>Wall running along Z at constant X. Optional centered gap (doorway/breach); 0 = solid.</summary>
        private void WallAlongZ(Transform parent, float x, float zCenter, float zLen, float height, float thickness, float gap, Material mat, string name)
        {
            if (gap > 0.1f && zLen > gap + 0.6f)
            {
                float seg = (zLen - gap) * 0.5f;
                float off = (zLen - seg) * 0.5f;
                Box(parent, new Vector3(x, height * 0.5f, zCenter - off), new Vector3(thickness, height, seg), Vector3.zero, mat, name);
                Box(parent, new Vector3(x, height * 0.5f, zCenter + off), new Vector3(thickness, height, seg), Vector3.zero, mat, name);
            }
            else if (gap <= 0.1f)
            {
                Box(parent, new Vector3(x, height * 0.5f, zCenter), new Vector3(thickness, height, zLen), Vector3.zero, mat, name);
            }
        }

        /// <summary>Put a structure and all its parts on the given layer (skips if the layer is missing).</summary>
        private void SetLayerRecursive(GameObject go, int layer)
        {
            if (layer < 0) return;
            go.layer = layer;
            foreach (Transform child in go.transform) SetLayerRecursive(child.gameObject, layer);
        }

        /// <summary>
        /// Pick a spot for a structure of the given radius: inside the world volume, outside the
        /// spawn clearing, and edge-to-edge clear of everything already placed (radiusA + radiusB
        /// + baseIslandGap). Radius-aware so huge islands claim proportionally more room.
        /// </summary>
        private bool TryPickPoint(float structRadius, out Vector3 point)
        {
            for (int attempt = 0; attempt < placementAttempts; attempt++)
            {
                point = transform.position + new Vector3(
                    RandRange(-worldExtent.x * 0.5f, worldExtent.x * 0.5f),
                    RandRange(-worldExtent.y * 0.5f, worldExtent.y * 0.5f),
                    RandRange(-worldExtent.z * 0.5f, worldExtent.z * 0.5f));

                // Keep the ship's spawn clearing empty (horizontal distance from world origin).
                if (new Vector2(point.x, point.z).magnitude < spawnClearRadius + structRadius) continue;

                bool ok = true;
                for (int i = 0; i < placed.Count; i++)
                {
                    float needed = placed[i].radius + structRadius + baseIslandGap;
                    if ((placed[i].pos - point).sqrMagnitude < needed * needed) { ok = false; break; }
                }

                if (ok)
                {
                    placed.Add(new PlacedEntry { pos = point, radius = structRadius });
                    return true;
                }
            }
            point = Vector3.zero;
            return false;
        }

        private float RandRange(float min, float max) => min + (float)rng.NextDouble() * (max - min);
        private int RandInt(int minInclusive, int maxInclusive) => rng.Next(minInclusive, maxInclusive + 1);

        /// <summary>A deterministic random point within a disc of the given radius (XZ plane).</summary>
        private Vector2 RandInsideDisc(float radius)
        {
            float ang = RandRange(0f, Mathf.PI * 2f);
            float dist = radius * Mathf.Sqrt((float)rng.NextDouble());
            return new Vector2(Mathf.Cos(ang) * dist, Mathf.Sin(ang) * dist);
        }

        private static float WeightFor(CargoCategory cat)
        {
            switch (cat)
            {
                case CargoCategory.RawResource: return 30f;
                case CargoCategory.Fuel: return 20f;
                case CargoCategory.RepairCargo: return 40f;
                case CargoCategory.SpecialCargo: return 25f;
                case CargoCategory.Treasure: return 15f;
                case CargoCategory.Stone: return 35f;
                case CargoCategory.Ore: return 30f;
                case CargoCategory.Crystal: return 18f;
                default: return 20f;
            }
        }

        private static float ValueFor(CargoCategory cat)
        {
            switch (cat)
            {
                case CargoCategory.RawResource: return 20f;
                case CargoCategory.Fuel: return 35f;
                case CargoCategory.RepairCargo: return 50f;
                case CargoCategory.SpecialCargo: return 120f;
                case CargoCategory.Treasure: return 250f;
                case CargoCategory.Stone: return 10f;
                case CargoCategory.Ore: return 45f;
                case CargoCategory.Crystal: return 90f;
                default: return 25f;
            }
        }

        private static string Readable(CargoCategory cat)
        {
            switch (cat)
            {
                case CargoCategory.RawResource: return "Raw Resource";
                case CargoCategory.Fuel: return "Fuel Cell";
                case CargoCategory.RepairCargo: return "Repair Parts";
                case CargoCategory.SpecialCargo: return "Special Cargo";
                case CargoCategory.Treasure: return "Treasure";
                case CargoCategory.Stone: return "Stone";
                case CargoCategory.Ore: return "Ore Chunk";
                case CargoCategory.Crystal: return "Crystal Shard";
                default: return "Cargo";
            }
        }

        private void BuildMaterials()
        {
            rockMat = MakeMat(new Color(0.45f, 0.5f, 0.42f));
            rockDarkMat = MakeMat(new Color(0.30f, 0.33f, 0.28f));
            hullMat = MakeMat(new Color(0.32f, 0.26f, 0.20f));
            wallMat = MakeMat(new Color(0.40f, 0.34f, 0.28f));
            debrisMat = MakeMat(new Color(0.22f, 0.20f, 0.18f));
            islandTopMat = MakeMat(new Color(0.42f, 0.52f, 0.36f));
            islandUnderMat = MakeMat(new Color(0.28f, 0.24f, 0.21f));
            rootMat = MakeMat(new Color(0.35f, 0.27f, 0.20f));
            stoneMat = MakeMat(new Color(0.68f, 0.68f, 0.66f));
            oreMat = MakeMat(new Color(0.78f, 0.42f, 0.16f));
            crystalMat = MakeMat(new Color(0.45f, 0.82f, 0.95f));
            crystalMat.EnableKeyword("_EMISSION");
            if (crystalMat.HasProperty("_EmissionColor"))
                crystalMat.SetColor("_EmissionColor", new Color(0.18f, 0.45f, 0.60f));

            cargoMats.Clear();
            cargoMats[CargoCategory.RawResource] = MakeMat(new Color(0.80f, 0.62f, 0.36f));
            cargoMats[CargoCategory.Fuel] = MakeMat(new Color(0.20f, 0.75f, 0.35f));
            cargoMats[CargoCategory.Treasure] = MakeMat(new Color(0.95f, 0.80f, 0.18f));
            cargoMats[CargoCategory.RepairCargo] = MakeMat(new Color(0.30f, 0.55f, 0.90f));
            cargoMats[CargoCategory.SpecialCargo] = MakeMat(new Color(0.65f, 0.30f, 0.85f));
            cargoMats[CargoCategory.Stone] = MakeMat(new Color(0.62f, 0.62f, 0.60f));
            cargoMats[CargoCategory.Ore] = MakeMat(new Color(0.85f, 0.45f, 0.20f));
            cargoMats[CargoCategory.Crystal] = MakeMat(new Color(0.50f, 0.85f, 0.95f));
            cargoMats[CargoCategory.Generic] = MakeMat(new Color(0.7f, 0.7f, 0.7f));
        }

        private Material CargoMaterial(CargoCategory cat)
            => cargoMats.TryGetValue(cat, out var m) ? m : cargoMats[CargoCategory.Generic];

        private static Material MakeMat(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.25f);
            Gizmos.DrawWireCube(transform.position, worldExtent);
        }
#endif
    }
}

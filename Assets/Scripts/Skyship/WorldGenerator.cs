using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Procedurally fills the airspace with MASSIVE floating islands (frequent, carrying raw resources +
    /// occasional fuel/treasure) and MASSIVE derelict ships (sparse, carrying repair/special cargo, fuel,
    /// and treasure). Islands get procedurally-laid-out natural terrain (plateaus, spires, boulders,
    /// arches); derelicts get procedurally-laid-out interiors (a grid of rooms joined by doorways into
    /// corridors, with debris obstacles). Graybox primitives, matching the prototype art.
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

        [Header("Spawn Volume (centered on this GameObject)")]
        [Tooltip("Islands/derelicts are scattered inside this box. With vertical ship movement the Y range " +
                 "is now meaningful — structures spread across altitudes you climb/descend to reach.")]
        public Vector3 volumeSize = new Vector3(1600f, 200f, 1600f);
        [Tooltip("Minimum distance between island/derelict centers. Large, because the structures are huge.")]
        public float minSpacing = 320f;
        [Tooltip("Keep this horizontal radius around the world origin (the ship spawn) empty.")]
        public float keepClearRadius = 170f;
        [Tooltip("How many placement attempts per structure before giving up on that one.")]
        public int placementAttempts = 60;

        [Header("Islands (frequent, massive)")]
        public int islandCount = 14;
        [Tooltip("Half-extent of an island's top plateau. These are huge floating landmasses.")]
        public Vector2 islandRadius = new Vector2(34f, 72f);
        [Tooltip("Min/max raw-resource crates per island.")]
        public Vector2Int cargoPerIsland = new Vector2Int(3, 7);
        [Range(0f, 1f)] public float islandFuelChance = 0.22f;
        [Range(0f, 1f)] public float islandTreasureChance = 0.10f;

        [Header("Derelicts (sparse, massive, room-filled)")]
        public int derelictCount = 4;
        [Tooltip("Min/max hull length (along Z) of a derelict ship.")]
        public Vector2 derelictLength = new Vector2(48f, 78f);
        [Tooltip("Min/max hull width (along X) of a derelict ship.")]
        public Vector2 derelictWidth = new Vector2(16f, 26f);
        [Tooltip("Interior floor-to-rail height of a derelict deck.")]
        public float derelictDeckHeight = 5f;
        [Tooltip("Min/max crates per derelict (repair/special/fuel/treasure), scattered through the rooms.")]
        public Vector2Int cargoPerDerelict = new Vector2Int(5, 11);

        [Header("Runtime (read-only)")]
        [SerializeField] private int usedSeed;
        [SerializeField] private int spawnedIslands;
        [SerializeField] private int spawnedDerelicts;
        [SerializeField] private int spawnedCargo;
        [SerializeField] private int spawnedPrimitives;

        private System.Random rng;
        private int cargoCounter;
        private readonly List<Vector3> placed = new List<Vector3>();

        private Material rockMat, rockDarkMat, hullMat, wallMat, debrisMat;
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
            spawnedIslands = spawnedDerelicts = spawnedCargo = spawnedPrimitives = 0;
            placed.Clear();
            BuildMaterials();

            // Fixed order: all islands, then all derelicts (identical on every peer).
            for (int i = 0; i < islandCount; i++) SpawnIsland(i);
            for (int i = 0; i < derelictCount; i++) SpawnDerelict(i);

            // Cargo was created after the network manager cached this scene's objects; refresh so the
            // host syncs every crate (and clients can resolve them by name).
            if (nm != null) nm.RefreshCargoRegistry();

            Debug.Log($"[WorldGenerator] seed={usedSeed}: {spawnedIslands} islands, {spawnedDerelicts} derelicts, " +
                      $"{spawnedCargo} cargo, {spawnedPrimitives} primitives.");
        }

        // ========================================================================================
        // ISLANDS — massive floating landmasses with procedural natural terrain
        // ========================================================================================
        private void SpawnIsland(int index)
        {
            if (!TryPickPoint(out Vector3 center)) return;

            float radius = RandRange(islandRadius.x, islandRadius.y);
            var island = new GameObject($"Island_{index:00}");
            island.transform.position = center;
            island.transform.rotation = Quaternion.Euler(0f, RandRange(0f, 360f), 0f);
            island.transform.SetParent(transform, true);

            BuildIslandBody(island.transform, radius);
            BuildIslandObstacles(island.transform, radius);
            ScatterIslandCargo(island.transform, radius);

            spawnedIslands++;
        }

        /// <summary>The landmass itself: a broad flattened top plateau plus a tapering, craggy underside.</summary>
        private void BuildIslandBody(Transform island, float radius)
        {
            // Top plateau: several large overlapping flattened chunks. The surface sits at localY ~ 0
            // so things on top (cargo, the player) stand at the island's picked altitude.
            int chunks = RandInt(4, 7);
            for (int c = 0; c < chunks; c++)
            {
                float spread = radius * 0.55f;
                Box(island,
                    new Vector3(RandRange(-spread, spread), RandRange(-radius * 0.18f, 0f), RandRange(-spread, spread)),
                    new Vector3(radius * RandRange(0.8f, 1.4f), radius * RandRange(0.25f, 0.5f), radius * RandRange(0.8f, 1.4f)),
                    new Vector3(0f, RandRange(0f, 360f), 0f),
                    rockMat, "Plateau");
            }

            // Underside: progressively smaller, inward chunks tapering to a point — the floating-rock look.
            int tiers = RandInt(3, 5);
            for (int t = 0; t < tiers; t++)
            {
                float f = 1f - (t + 1) / (float)(tiers + 1);          // shrinks toward the tip
                float y = -radius * (0.25f + t * 0.45f);
                Box(island,
                    new Vector3(RandRange(-radius * 0.12f, radius * 0.12f), y, RandRange(-radius * 0.12f, radius * 0.12f)),
                    new Vector3(radius * f * RandRange(0.7f, 1.1f), radius * RandRange(0.4f, 0.7f), radius * f * RandRange(0.7f, 1.1f)),
                    new Vector3(RandRange(-12f, 12f), RandRange(0f, 360f), RandRange(-12f, 12f)),
                    rockDarkMat, "Underside");
            }
        }

        /// <summary>Natural obstacles on the surface: rock spires, scattered boulders, and the odd arch.</summary>
        private void BuildIslandObstacles(Transform island, float radius)
        {
            // Spires — tall rock pillars you have to navigate around.
            int spires = RandInt(4, 9);
            for (int s = 0; s < spires; s++)
            {
                Vector2 p = RandInsideDisc(radius * 0.8f);
                float h = RandRange(radius * 0.3f, radius * 0.9f);
                float r = RandRange(radius * 0.04f, radius * 0.1f);
                Cyl(island, new Vector3(p.x, h * 0.5f, p.y), h, r,
                    new Vector3(RandRange(-6f, 6f), RandRange(0f, 360f), RandRange(-6f, 6f)),
                    rockMat, "Spire");
            }

            // Boulders — chunky obstacles scattered across the deck.
            int boulders = RandInt(6, 14);
            for (int b = 0; b < boulders; b++)
            {
                Vector2 p = RandInsideDisc(radius * 0.9f);
                float size = RandRange(radius * 0.08f, radius * 0.22f);
                Box(island, new Vector3(p.x, size * 0.4f, p.y),
                    new Vector3(size * RandRange(0.8f, 1.3f), size * RandRange(0.6f, 1.1f), size * RandRange(0.8f, 1.3f)),
                    new Vector3(RandRange(0f, 360f), RandRange(0f, 360f), RandRange(0f, 360f)),
                    rockDarkMat, "Boulder");
            }

            // Arches — two pillars spanned by a lintel; a natural gateway.
            int arches = RandInt(0, 3);
            for (int a = 0; a < arches; a++)
            {
                Vector2 p = RandInsideDisc(radius * 0.6f);
                float span = RandRange(radius * 0.25f, radius * 0.45f);
                float h = RandRange(radius * 0.35f, radius * 0.6f);
                float yaw = RandRange(0f, 360f);
                var arch = new GameObject("Arch");
                arch.transform.SetParent(island, false);
                arch.transform.localPosition = new Vector3(p.x, 0f, p.y);
                arch.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
                float legR = radius * 0.05f;
                Cyl(arch.transform, new Vector3(-span * 0.5f, h * 0.5f, 0f), h, legR, Vector3.zero, rockMat, "Leg");
                Cyl(arch.transform, new Vector3(span * 0.5f, h * 0.5f, 0f), h, legR, Vector3.zero, rockMat, "Leg");
                Box(arch.transform, new Vector3(0f, h, 0f),
                    new Vector3(span + legR * 2f, legR * 2f, legR * 2f), Vector3.zero, rockMat, "Span");
            }
        }

        private void ScatterIslandCargo(Transform island, float radius)
        {
            int count = RandInt(cargoPerIsland.x, cargoPerIsland.y);
            for (int k = 0; k < count; k++)
            {
                CargoCategory cat = CargoCategory.RawResource;
                double roll = rng.NextDouble();
                if (roll < islandTreasureChance) cat = CargoCategory.Treasure;
                else if (roll < islandTreasureChance + islandFuelChance) cat = CargoCategory.Fuel;

                Vector2 p = RandInsideDisc(radius * 0.75f);
                SpawnCargo(island.TransformPoint(new Vector3(p.x, 1.5f, p.y)), cat);
            }
        }

        // ========================================================================================
        // DERELICTS — massive hulks with procedurally-laid-out room/corridor interiors
        // ========================================================================================
        private void SpawnDerelict(int index)
        {
            if (!TryPickPoint(out Vector3 center)) return;

            float length = RandRange(derelictLength.x, derelictLength.y);
            float width = RandRange(derelictWidth.x, derelictWidth.y);
            float h = derelictDeckHeight;

            var derelict = new GameObject($"Derelict_{index:00}");
            derelict.transform.position = center;
            // A gentle list/tilt — it's a wreck, but kept shallow so the interior stays walkable.
            derelict.transform.rotation = Quaternion.Euler(RandRange(-8f, 8f), RandRange(0f, 360f), RandRange(-6f, 6f));
            derelict.transform.SetParent(transform, true);

            BuildDerelictHull(derelict.transform, length, width, h);
            BuildDerelictInterior(derelict.transform, length, width, h);
            BuildDerelictSuperstructure(derelict.transform, length, width, h);
            ScatterDerelictCargo(derelict.transform, length, width);

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
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"WCargo_{cargoCounter:0000}"; // deterministic, unique -> network cargo sync keys on this
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

        private bool TryPickPoint(out Vector3 point)
        {
            for (int attempt = 0; attempt < placementAttempts; attempt++)
            {
                point = transform.position + new Vector3(
                    RandRange(-volumeSize.x * 0.5f, volumeSize.x * 0.5f),
                    RandRange(-volumeSize.y * 0.5f, volumeSize.y * 0.5f),
                    RandRange(-volumeSize.z * 0.5f, volumeSize.z * 0.5f));

                // Keep the ship's spawn clearing empty (horizontal distance from world origin).
                if (new Vector2(point.x, point.z).magnitude < keepClearRadius) continue;

                bool ok = true;
                for (int i = 0; i < placed.Count; i++)
                    if ((placed[i] - point).sqrMagnitude < minSpacing * minSpacing) { ok = false; break; }

                if (ok) { placed.Add(point); return true; }
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
            cargoMats.Clear();
            cargoMats[CargoCategory.RawResource] = MakeMat(new Color(0.80f, 0.62f, 0.36f));
            cargoMats[CargoCategory.Fuel] = MakeMat(new Color(0.20f, 0.75f, 0.35f));
            cargoMats[CargoCategory.Treasure] = MakeMat(new Color(0.95f, 0.80f, 0.18f));
            cargoMats[CargoCategory.RepairCargo] = MakeMat(new Color(0.30f, 0.55f, 0.90f));
            cargoMats[CargoCategory.SpecialCargo] = MakeMat(new Color(0.65f, 0.30f, 0.85f));
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
            Gizmos.DrawWireCube(transform.position, volumeSize);
        }
#endif
    }
}

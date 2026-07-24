using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// A PHYSICAL storm cell: a visible tower of dark cloud that marches across the map in a
    /// straight line, like real weather. You can SEE it coming (dark mass + internal lightning
    /// on the horizon, marker on the ship's chart) and you can AVOID it by flying around its
    /// path — the direction is rolled once per expedition from the world seed and NEVER changes.
    /// When the cell walks off the map border it despawns, waits a beat, and respawns at the
    /// entry border on the same heading (fresh lateral offset), so the weather keeps coming.
    ///
    /// AUTHORITY: the HOST simulates the cell and writes center/radius/active into the runtime
    /// state; they ride the existing ExpeditionNetState blob, so clients place the same visual
    /// and feel the same proximity FX with zero new packets. All gameplay forces are host-only.
    ///
    /// INSIDE the cell (scaled by how deep you are):
    ///  - every peer: sky dims, fog slams shut (via ExpeditionThreatDirector.LocalStormProximity),
    ///    lightning flashes overhead;
    ///  - host physics: wind shoves the ship ALONG the storm's travel direction, the deck rocks
    ///    (reaches clients through the synced visualTilt), gust peaks scatter loose cargo;
    ///  - the CORE grinds the hull: staying deep inside racks up hull damage.
    ///
    /// THREAT LINK: at threat 3+ ("STORM RISING", objective aboard) the cell swells and speeds
    /// up — still avoidable, but the sky is actively hunting your route home.
    /// </summary>
    public class StormSystem : MonoBehaviour
    {
        /// <summary>0..1 — how deep the LOCAL player is inside the storm. Read by the threat
        /// director's fog control so the two systems never fight over RenderSettings.</summary>
        public static float LocalStormProximity { get; private set; }

        [Header("Cell")]
        [Tooltip("Storm radius (meters) at threat < 3. ~1/3 of the map width: hard to ignore, " +
                 "but a plotted course can still slip around its lane.")]
        public float cellRadius = 1400f;
        [Tooltip("Vertical extent of the cloud tower.")]
        public float cellHeight = 700f;
        [Tooltip("Travel speed (m/s) at threat < 3. Direction is fixed per expedition.")]
        public float moveSpeed = 25f;
        [Tooltip("Seconds off-map before the cell respawns at the entry border.")]
        public float respawnDelay = 30f;
        [Tooltip("Radius/speed multipliers once threat reaches 3 (escape pressure).")]
        public float threatRadiusScale = 1.4f;
        public float threatSpeedScale = 1.6f;
        [Tooltip("How far off-center a cell's lane may run (fraction of the map half-width). " +
                 "Low values keep storms sweeping the middle of the map where the players are.")]
        [Range(0f, 1f)] public float lateralSpawnSpread = 0.45f;

        [Header("Wind (host, scaled by ship depth in the cell)")]
        [Tooltip("Peak drift the wind imparts along the storm's travel direction (m/s).")]
        public float windStrengthMax = 5f;
        [Tooltip("Peak yaw shove (deg/s).")]
        public float yawDriftMax = 5f;
        [Tooltip("Peak additive deck rocking (degrees).")]
        public float rockAngleMax = 5f;
        [Tooltip("How fast gusts evolve (Perlin time scale).")]
        public float gustSpeed = 0.15f;
        [Tooltip("Velocity change given to loose deck cargo on a gust peak (m/s at full depth).")]
        public float cargoGustShove = 1.6f;

        [Header("Core damage (host)")]
        [Tooltip("Fraction of the radius that counts as the grinding core.")]
        public float coreFraction = 0.35f;
        [Tooltip("Hull damage per second while the ship sits in the core.")]
        public float hullDamagePerSecond = 0.4f;

        [Header("Ambience (every peer)")]
        [Tooltip("Skybox exposure multiplier at full depth (darkens the sky).")]
        public float stormExposureScale = 0.35f;
        public Vector2 lightningInterval = new Vector2(3f, 10f);
        public float lightningIntensity = 2.4f;

        [Header("Runtime (read-only)")]
        [Range(0f, 1f)] public float localDepth;  // local player, drives ambience
        [Range(0f, 1f)] public float shipDepth;   // ship, drives physics (host)

        private Vector3 travelDir;   // fixed for the whole expedition
        private Vector3 origin;
        private Vector3 halfExtent;

        private Transform cellVisual;
        private Light cellFlash;      // point light at the cell heart — visible from far away
        private Light overheadFlash;  // directional flash when YOU are inside
        private float flash;
        private float nextLightning;
        private float nextCargoShove;
        private float nextCoreWarn;
        private float respawnAt = -1f;
        private float baseExposure = -1f;
        private bool firstSpawn = true;

        private ShipMovementController movement;
        private ShipBalanceController balance;
        private ShipPlatformArea platform;
        private Transform localPlayer;
        private System.Random rng;

        private void Start()
        {
            var gen = FindAnyObjectByType<WorldGenerator>();
            origin = gen != null ? gen.transform.position : Vector3.zero;
            halfExtent = (gen != null ? gen.worldExtent : new Vector3(8000f, 700f, 8000f)) * 0.5f;

            var ship = GameObject.Find("ShipRoot");
            if (ship != null)
            {
                movement = ship.GetComponent<ShipMovementController>();
                balance = ship.GetComponent<ShipBalanceController>();
                platform = ship.GetComponent<ShipPlatformArea>();
            }
            var player = GameObject.Find("Player");
            localPlayer = player != null ? player.transform : null;

            // One fixed heading per expedition, rolled from the world seed (same on every peer;
            // the host's synced center is authoritative anyway — this just keeps respawns and
            // the pre-sync first frame consistent).
            var nm = NetworkManagerP2P.Instance;
            rng = new System.Random((nm != null ? nm.worldSeed : Environment_TickCount()) * 31 + 7);
            float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0);
            travelDir = new Vector3(Mathf.Cos(ang), 0f, Mathf.Sin(ang));

            BuildCellVisual();

            // The authority seeds the first cell at an entry border so the crew can watch it come.
            var manager = ExpeditionManager.Instance;
            if (manager != null && manager.IsAuthority)
            {
                manager.runtime.stormRadius = cellRadius;
                SpawnAtEntryBorder(manager.runtime);
            }
        }

        private static int Environment_TickCount() => System.Environment.TickCount;

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            bool inMission = manager != null &&
                             (manager.runtime.phase == ExpeditionPhase.Active ||
                              manager.runtime.phase == ExpeditionPhase.ReturnReady);
            if (!inMission)
            {
                ClearEffects();
                if (cellVisual != null) cellVisual.gameObject.SetActive(false);
                return;
            }

            var rt = manager.runtime;
            if (manager.IsAuthority)
                SimulateCell(rt);

            if (!rt.stormActive)
            {
                ClearEffects();
                if (cellVisual != null) cellVisual.gameObject.SetActive(false);
                return;
            }

            // ---- every peer: place the visual and measure depths ----
            if (cellVisual != null)
            {
                if (!cellVisual.gameObject.activeSelf) cellVisual.gameObject.SetActive(true);
                // Host is exact; clients ease toward the 20 Hz synced center.
                cellVisual.position = manager.IsAuthority
                    ? rt.stormCenter
                    : Vector3.Lerp(cellVisual.position, rt.stormCenter, 0.15f);
                float visualScale = Mathf.Max(0.1f, rt.stormRadius / Mathf.Max(1f, cellRadius));
                cellVisual.localScale = new Vector3(visualScale, Mathf.Lerp(1f, visualScale, 0.5f), visualScale);
            }

            localDepth = DepthAt(localPlayer != null ? localPlayer.position : rt.stormCenter + Vector3.one * 99999f, rt);
            shipDepth = movement != null ? DepthAt(movement.transform.position, rt) : 0f;
            LocalStormProximity = localDepth;

            ApplyAmbience(rt);

            if (manager.IsAuthority)
                ApplyShipPhysics(manager, rt);
        }

        /// <summary>0 outside the cell → 1 at its center (horizontal distance only).</summary>
        private float DepthAt(Vector3 pos, ExpeditionRuntimeState rt)
        {
            Vector2 d = new Vector2(pos.x - rt.stormCenter.x, pos.z - rt.stormCenter.z);
            return Mathf.Clamp01(1f - d.magnitude / Mathf.Max(1f, rt.stormRadius));
        }

        // ---------------- HOST: CELL SIMULATION ----------------

        private void SimulateCell(ExpeditionRuntimeState rt)
        {
            bool surge = rt.threatLevel >= 3;
            float targetRadius = cellRadius * (surge ? threatRadiusScale : 1f);
            rt.stormRadius = Mathf.MoveTowards(rt.stormRadius, targetRadius, 25f * Time.deltaTime);

            if (!rt.stormActive)
            {
                if (respawnAt >= 0f && Time.time >= respawnAt)
                    SpawnAtEntryBorder(rt);
                return;
            }

            float speed = moveSpeed * (surge ? threatSpeedScale : 1f);
            rt.stormCenter += travelDir * (speed * Time.deltaTime);
            rt.stormCenter = new Vector3(rt.stormCenter.x, origin.y, rt.stormCenter.z);

            // Walked off the far border (fully clear of the map): despawn, come back later on
            // the SAME heading.
            if (Mathf.Abs(rt.stormCenter.x - origin.x) > halfExtent.x + rt.stormRadius ||
                Mathf.Abs(rt.stormCenter.z - origin.z) > halfExtent.z + rt.stormRadius)
            {
                rt.stormActive = false;
                respawnAt = Time.time + respawnDelay;
            }
        }

        /// <summary>Place the cell at the border it will enter from, on the fixed heading, with a
        /// fresh lateral offset so successive passes sweep different (but central-ish) lanes.</summary>
        private void SpawnAtEntryBorder(ExpeditionRuntimeState rt)
        {
            // Distance from origin to the border along the (fixed) travel direction.
            float alongSpan = Mathf.Abs(travelDir.x) * halfExtent.x + Mathf.Abs(travelDir.z) * halfExtent.z;
            Vector3 perp = new Vector3(-travelDir.z, 0f, travelDir.x);
            float lateralSpan = Mathf.Min(halfExtent.x, halfExtent.z) * lateralSpawnSpread;
            float lateral = ((float)rng.NextDouble() * 2f - 1f) * lateralSpan;

            // The FIRST cell spawns already straddling the border — inside fog draw range sooner,
            // so the crew actually sees the weather arrive. Respawns start fully outside.
            float along = firstSpawn ? alongSpan * 0.9f : alongSpan + rt.stormRadius;
            firstSpawn = false;

            rt.stormCenter = origin - travelDir * along + perp * lateral;
            rt.stormCenter = new Vector3(rt.stormCenter.x, origin.y, rt.stormCenter.z);
            rt.stormActive = true;
            respawnAt = -1f;

            var manager = ExpeditionManager.Instance;
            if (manager != null)
                manager.BroadcastEvent("Dark weather on the horizon — a storm cell is crossing the shelf.");
        }

        // ---------------- EVERY PEER: AMBIENCE ----------------

        private void ApplyAmbience(ExpeditionRuntimeState rt)
        {
            // Sky darkens as YOU go deeper (fog closing is handled by the threat director, which
            // reads LocalStormProximity — single owner for RenderSettings.fog).
            var sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty("_Exposure"))
            {
                if (baseExposure < 0f) baseExposure = sky.GetFloat("_Exposure");
                sky.SetFloat("_Exposure", Mathf.Lerp(baseExposure, baseExposure * stormExposureScale, localDepth));
            }

            // Lightning inside the cell — the heart light makes distant cells flicker on the
            // horizon; the directional flash only matters once you're under the cloud.
            if (Time.time >= nextLightning)
            {
                flash = 1f;
                nextLightning = Time.time + Random.Range(lightningInterval.x, lightningInterval.y);
            }
            flash = Mathf.MoveTowards(flash, 0f, Time.deltaTime * 3.5f);

            if (cellFlash != null)
            {
                cellFlash.range = rt.stormRadius * 1.6f;
                cellFlash.intensity = flash * flash * 6f;
            }
            if (overheadFlash != null)
                overheadFlash.intensity = flash * flash * lightningIntensity * localDepth;
        }

        // ---------------- HOST: PHYSICAL WIND + CORE DAMAGE ----------------

        private void ApplyShipPhysics(ExpeditionManager manager, ExpeditionRuntimeState rt)
        {
            float t = Time.time * gustSpeed;
            float gust = Mathf.PerlinNoise(t, 0.37f);
            gust *= gust;

            if (movement != null)
            {
                // The wind blows the way the storm travels — riding it out pushes you along its path.
                movement.externalWindDrift = travelDir * (gust * windStrengthMax * shipDepth);
                movement.externalYawDrift = (Mathf.PerlinNoise(t, 7.77f) - 0.5f) * 2f * yawDriftMax * shipDepth;
            }
            if (balance != null)
            {
                balance.externalRollAdd = (Mathf.PerlinNoise(t * 1.3f, 3.13f) - 0.5f) * 2f * rockAngleMax * shipDepth;
                balance.externalPitchAdd = (Mathf.PerlinNoise(t * 1.1f, 5.91f) - 0.5f) * 2f * rockAngleMax * 0.6f * shipDepth;
            }

            // Gust peaks knock loose cargo around the deck.
            if (gust > 0.7f && shipDepth > 0.05f && platform != null && Time.time >= nextCargoShove)
            {
                nextCargoShove = Time.time + 2.5f;
                var items = platform.itemsInPlatform;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null || item.isHeld || item.Body == null || item.Body.isKinematic) continue;
                    item.Body.AddForce(travelDir * (cargoGustShove * shipDepth), ForceMode.VelocityChange);
                }
            }

            // The core grinds the hull — the real reason to fly AROUND, not through.
            if (shipDepth > 1f - coreFraction)
            {
                manager.progress.hullDamage += hullDamagePerSecond * Time.deltaTime;
                if (Time.time >= nextCoreWarn)
                {
                    nextCoreWarn = Time.time + 6f;
                    manager.BroadcastEvent("!! The storm's heart is tearing at the hull — get OUT !!");
                }
            }
        }

        // ---------------- VISUAL ----------------

        /// <summary>The cell itself: a tower of dark puffs with a lightning heart. Built once at
        /// unit radius; scaled when threat swells the storm.</summary>
        private void BuildCellVisual()
        {
            cellVisual = new GameObject("StormCell").transform;

            Material dark = SkyDressing.MakeMat(new Color(0.16f, 0.15f, 0.21f));
            Material mid = SkyDressing.MakeMat(new Color(0.26f, 0.24f, 0.33f));

            for (int i = 0; i < 52; i++)
            {
                float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0);
                float dist = cellRadius * Mathf.Sqrt((float)rng.NextDouble()) * 0.95f;
                float w = Mathf.Lerp(300f, 850f, (float)rng.NextDouble());

                var puff = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                puff.name = "StormPuff";
                Destroy(puff.GetComponent<Collider>()); // the storm is weather, not a wall
                puff.transform.SetParent(cellVisual, false);
                puff.transform.localPosition = new Vector3(
                    Mathf.Cos(ang) * dist,
                    Mathf.Lerp(-cellHeight * 0.5f, cellHeight * 0.5f, (float)rng.NextDouble()),
                    Mathf.Sin(ang) * dist);
                puff.transform.localScale = new Vector3(w, w * Mathf.Lerp(0.45f, 0.8f, (float)rng.NextDouble()), w);
                puff.GetComponent<Renderer>().sharedMaterial = (i % 3 == 0) ? mid : dark;
            }

            // Lightning heart: flickers the whole cell from within, visible across the map.
            var heart = new GameObject("StormHeart");
            heart.transform.SetParent(cellVisual, false);
            cellFlash = heart.AddComponent<Light>();
            cellFlash.type = LightType.Point;
            cellFlash.color = new Color(0.80f, 0.82f, 1f);
            cellFlash.intensity = 0f;
            cellFlash.shadows = LightShadows.None;

            // Overhead flash for anyone standing inside the cell.
            var over = new GameObject("StormOverheadFlash");
            over.transform.SetParent(cellVisual, false);
            over.transform.rotation = Quaternion.Euler(55f, 30f, 0f);
            overheadFlash = over.AddComponent<Light>();
            overheadFlash.type = LightType.Directional;
            overheadFlash.color = new Color(0.85f, 0.88f, 1f);
            overheadFlash.intensity = 0f;
            overheadFlash.shadows = LightShadows.None;

            cellVisual.gameObject.SetActive(false);
        }

        /// <summary>Zero every hook so a distant/absent storm leaves no residual FX.</summary>
        private void ClearEffects()
        {
            localDepth = 0f;
            shipDepth = 0f;
            LocalStormProximity = 0f;

            if (movement != null)
            {
                movement.externalWindDrift = Vector3.zero;
                movement.externalYawDrift = 0f;
            }
            if (balance != null)
            {
                balance.externalRollAdd = 0f;
                balance.externalPitchAdd = 0f;
            }
            if (cellFlash != null) cellFlash.intensity = 0f;
            if (overheadFlash != null) overheadFlash.intensity = 0f;

            var sky = RenderSettings.skybox;
            if (sky != null && baseExposure > 0f && sky.HasProperty("_Exposure"))
                sky.SetFloat("_Exposure", baseExposure);
        }

        private void OnDisable()
        {
            ClearEffects();
        }
    }
}

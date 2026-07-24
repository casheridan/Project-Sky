using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// THE LEVIATHAN — a vast dark shape swimming the cloud sea below the islands. It is FED by
    /// what falls into the clouds: cargo dropped overboard, capsized loads, anything that sinks
    /// past the deck (ShipStressSystem reports capsize dumps; this system watches for loose
    /// crates that fall below the cloud line). The more it's fed, the bolder it gets:
    ///
    ///   0 DORMANT   — nothing but rumors.
    ///   1 RESTLESS  — a silhouette circles the middle distance, cresting the cloud deck.
    ///   2 SHADOWING — it circles directly beneath YOUR ship. Stop feeding it.
    ///   3 BREACHING — it comes UP: a deck-slamming near-miss that damages the hull, then dives.
    ///
    /// Makes cargo-dumping (the easy fix for overload/tilt) a real decision instead of a free
    /// action. HOST owns the state machine (runtime.leviathanState rides the sync blob); every
    /// peer animates the shape locally from the synced state + mission clock, so the silhouette
    /// matches across the crew without extra packets.
    /// </summary>
    public class LeviathanSystem : MonoBehaviour
    {
        public static LeviathanSystem Instance { get; private set; }

        [Header("Appetite")]
        [Tooltip("Seconds into the expedition before it stirs on its own (state 1).")]
        public float restlessTime = 150f;
        [Tooltip("Feedings that make it shadow the ship (state 2).")]
        public int shadowThreshold = 2;
        [Tooltip("Feedings that trigger a breach (state 3).")]
        public int breachThreshold = 4;
        [Tooltip("Feedings forgiven after each breach (it's sated... for a while).")]
        public int breachSatiation = 3;

        [Header("Breach")]
        public float breachDuration = 9f;
        public float breachHullDamage = 8f;
        [Tooltip("Deck jolt (degrees of roll) when it slams past the hull.")]
        public float breachJolt = 9f;

        [Header("Swimming")]
        public float restlessOrbitRadius = 900f;
        public float shadowOrbitRadius = 240f;
        public float orbitSpeedRestless = 0.05f; // radians/sec
        public float orbitSpeedShadow = 0.22f;

        [Header("Runtime (read-only)")]
        public int fedCount;
        public int state;

        private Vector3 origin;
        private float cloudY;          // top of the cloud sea — where the shape swims
        private Transform body;
        private Transform shipRoot;
        private ShipBalanceController balance;
        private float clock;
        private float breachStartedLocal = -1f; // per-peer animation clock for state 3
        private float breachEndsAt;             // host: when to leave state 3
        private float nextFeedScan;
        private bool joltDone;
        private readonly HashSet<string> fedItems = new HashSet<string>();

        private void Awake() => Instance = this;
        private void OnDestroy() { if (Instance == this) Instance = null; }

        private void Start()
        {
            var gen = FindAnyObjectByType<WorldGenerator>();
            origin = gen != null ? gen.transform.position : Vector3.zero;
            float extentY = gen != null ? gen.worldExtent.y : 700f;
            float depthBelow = gen != null ? gen.cloudSeaDepthBelow : 200f;
            cloudY = origin.y - extentY * 0.5f - depthBelow + 30f; // just cresting the puff tops

            var ship = GameObject.Find("ShipRoot");
            shipRoot = ship != null ? ship.transform : null;
            balance = ship != null ? ship.GetComponent<ShipBalanceController>() : null;

            BuildBody();
        }

        /// <summary>Something edible went into the clouds. Called by ShipStressSystem on capsize
        /// dumps; the feed-scan below catches individually dropped crates.</summary>
        public static void NotifyFed(int amount)
        {
            if (Instance == null) return;
            var manager = ExpeditionManager.Instance;
            if (manager == null || !manager.IsAuthority) return;
            Instance.fedCount += amount;
        }

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            bool inMission = manager != null &&
                             (manager.runtime.phase == ExpeditionPhase.Active ||
                              manager.runtime.phase == ExpeditionPhase.ReturnReady);
            if (!inMission)
            {
                if (body != null) body.gameObject.SetActive(false);
                return;
            }

            clock += Time.deltaTime;
            clock = Mathf.Lerp(clock, manager.runtime.elapsedSeconds, 0.05f);

            if (manager.IsAuthority)
                HostDrive(manager);

            state = manager.runtime.leviathanState;
            AnimateBody(manager);
        }

        // ---------------- HOST ----------------

        private void HostDrive(ExpeditionManager manager)
        {
            var rt = manager.runtime;

            // Watch for loose cargo sinking into the cloud sea (each crate feeds it once).
            if (Time.time >= nextFeedScan)
            {
                nextFeedScan = Time.time + 2f;
                var items = FindObjectsByType<CargoItem>(FindObjectsInactive.Exclude);
                for (int i = 0; i < items.Length; i++)
                {
                    var item = items[i];
                    if (item == null || item.isHeld || fedItems.Contains(item.name)) continue;
                    if (item.transform.position.y < cloudY + 40f)
                    {
                        fedItems.Add(item.name);
                        fedCount++;
                        manager.BroadcastEvent("The clouds take it. Something noticed.");
                    }
                }
            }

            int newState = rt.leviathanState;
            switch (rt.leviathanState)
            {
                case 0: // dormant
                    if (rt.elapsedSeconds >= restlessTime || fedCount >= 1 || rt.threatLevel >= 2)
                        newState = 1;
                    break;

                case 1: // restless
                    if (fedCount >= shadowThreshold) newState = 2;
                    break;

                case 2: // shadowing
                    if (fedCount >= breachThreshold)
                    {
                        newState = 3;
                        breachEndsAt = Time.time + breachDuration;
                        joltDone = false;
                    }
                    else if (fedCount < shadowThreshold)
                    {
                        newState = 1; // starved back down after a breach's satiation
                    }
                    break;

                case 3: // breaching
                    // The slam lands mid-breach: hull damage + a deck jolt everyone feels.
                    if (!joltDone && Time.time >= breachEndsAt - breachDuration * 0.5f)
                    {
                        joltDone = true;
                        manager.progress.hullDamage += breachHullDamage;
                        if (balance != null)
                            balance.AddTiltImpulse(Random.value < 0.5f ? breachJolt : -breachJolt, breachJolt * 0.5f);
                        manager.BroadcastEvent("!! IT SURFACES — the hull screams against its hide !!");
                    }
                    if (Time.time >= breachEndsAt)
                    {
                        fedCount = Mathf.Max(0, fedCount - breachSatiation);
                        newState = fedCount >= shadowThreshold ? 2 : 1;
                    }
                    break;
            }

            if (newState != rt.leviathanState)
            {
                rt.leviathanState = newState;
                switch (newState)
                {
                    case 1: manager.BroadcastEvent("Something vast turns beneath the clouds."); break;
                    case 2: manager.BroadcastEvent("It is following the ship's shadow. STOP FEEDING IT."); break;
                    case 3: manager.BroadcastEvent("THE CLOUDS BULGE — BRACE!"); break;
                }
            }
        }

        // ---------------- EVERY PEER: THE SHAPE ----------------

        private void AnimateBody(ExpeditionManager manager)
        {
            if (body == null) return;

            if (state <= 0)
            {
                if (body.gameObject.activeSelf) body.gameObject.SetActive(false);
                breachStartedLocal = -1f;
                return;
            }
            if (!body.gameObject.activeSelf) body.gameObject.SetActive(true);

            Vector3 center;
            float radius, speed, y = cloudY;

            if (state == 1)
            {
                center = new Vector3(origin.x, 0f, origin.z);
                radius = restlessOrbitRadius;
                speed = orbitSpeedRestless;
            }
            else // shadowing or breaching: under the ship
            {
                Vector3 s = shipRoot != null ? shipRoot.position : origin;
                center = new Vector3(s.x, 0f, s.z);
                radius = shadowOrbitRadius;
                speed = orbitSpeedShadow;
            }

            if (state == 3)
            {
                // Local breach animation: rise steeply toward the hull, hang, dive. Timed off the
                // synced state flip, so peers are within a packet of each other.
                if (breachStartedLocal < 0f) breachStartedLocal = Time.time;
                float t = Mathf.Clamp01((Time.time - breachStartedLocal) / breachDuration);
                float rise = Mathf.Sin(t * Mathf.PI); // up and back down
                float shipY = shipRoot != null ? shipRoot.position.y : origin.y;
                y = Mathf.Lerp(cloudY, shipY - 35f, rise * rise);
                radius *= Mathf.Lerp(1f, 0.4f, rise);
            }
            else
            {
                breachStartedLocal = -1f;
            }

            float ang = clock * speed;
            Vector3 pos = new Vector3(center.x + Mathf.Cos(ang) * radius, y, center.z + Mathf.Sin(ang) * radius);
            Vector3 tangent = new Vector3(-Mathf.Sin(ang), 0f, Mathf.Cos(ang));

            body.position = Vector3.Lerp(body.position, pos, 0.5f * Time.deltaTime * 10f);
            if (tangent.sqrMagnitude > 0.001f)
                body.rotation = Quaternion.Slerp(body.rotation, Quaternion.LookRotation(tangent), 2f * Time.deltaTime);
        }

        /// <summary>A whale-of-wrongness silhouette: stretched dark body, tail, dorsal ridge.
        /// No colliders — it's a horror, not an obstacle (for now).</summary>
        private void BuildBody()
        {
            body = new GameObject("Leviathan").transform;
            Material hide = SkyDressing.MakeMat(new Color(0.045f, 0.055f, 0.085f));

            var trunk = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            trunk.name = "Trunk";
            Destroy(trunk.GetComponent<Collider>());
            trunk.transform.SetParent(body, false);
            trunk.transform.localScale = new Vector3(28f, 22f, 110f);
            trunk.GetComponent<Renderer>().sharedMaterial = hide;

            var tail = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            tail.name = "Tail";
            Destroy(tail.GetComponent<Collider>());
            tail.transform.SetParent(body, false);
            tail.transform.localPosition = new Vector3(0f, 2f, -75f);
            tail.transform.localScale = new Vector3(14f, 26f, 45f);
            tail.GetComponent<Renderer>().sharedMaterial = hide;

            var ridge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ridge.name = "DorsalRidge";
            Destroy(ridge.GetComponent<Collider>());
            ridge.transform.SetParent(body, false);
            ridge.transform.localPosition = new Vector3(0f, 13f, 10f);
            ridge.transform.localScale = new Vector3(2.5f, 12f, 40f);
            ridge.GetComponent<Renderer>().sharedMaterial = hide;

            body.gameObject.SetActive(false);
        }
    }
}

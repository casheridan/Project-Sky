using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace Skyship
{
    /// <summary>Tag component on a hull growth so PlayerInteraction's E-ray can find it.</summary>
    public class Barnacle : MonoBehaviour
    {
        public string id;
    }

    /// <summary>
    /// BARNACLE SWARMS — glowing sky-fauna that latch onto the hull while you fly through
    /// hazards (the storm cell, whisper fog) and slowly even in calm-but-threatened air. Each
    /// one adds PHANTOM WEIGHT at its hull position — feeding straight into the existing
    /// balance/tilt sim (ShipBalanceController.externalWeights), so an ignored infestation
    /// literally lists the ship. Scrape them off with E (each yields 1 scrap).
    ///
    /// HOST-AUTHORITATIVE: the host grows/removes them and publishes a compact CSV through the
    /// sync blob; clients rebuild their visuals from it. Scrapes are validated by the host
    /// (clients send a ScrapeRequest packet).
    /// </summary>
    public class BarnacleSystem : MonoBehaviour
    {
        public static BarnacleSystem Instance { get; private set; }

        [Header("Growth")]
        [Tooltip("Base seconds per new barnacle while threat >= 1 (nothing grows at threat 0).")]
        public float baseGrowthInterval = 75f;
        [Tooltip("Growth speed multiplier while the ship is inside the storm or a whisper bank.")]
        public float hazardGrowthMultiplier = 4f;
        public int maxBarnacles = 12;
        [Tooltip("Phantom weight per barnacle (12 x 30 = 36% of default capacity, lopsided).")]
        public float barnacleWeight = 30f;

        [Header("Placement (ShipVisualRoot-local hull rim)")]
        public float railX = 4.2f;
        public float railY = 0.7f;
        public float railZRange = 13f;

        [Header("Runtime (read-only)")]
        public int count;

        private struct Entry
        {
            public string id;
            public Vector3 localPos;
            public float weight;
        }

        private readonly List<Entry> entries = new List<Entry>();
        private readonly Dictionary<string, GameObject> visuals = new Dictionary<string, GameObject>();
        private ShipBalanceController balance;
        private Transform rideParent;
        private float growthTimer;
        private int idCounter;
        private string appliedCsv = ""; // client: last CSV we rebuilt visuals from
        private System.Random rng = new System.Random();
        private Material shellMat;

        private void Awake() => Instance = this;
        private void OnDestroy() { if (Instance == this) Instance = null; }

        private void Start()
        {
            var ship = GameObject.Find("ShipRoot");
            if (ship != null)
            {
                balance = ship.GetComponent<ShipBalanceController>();
                rideParent = balance != null && balance.shipVisualRoot != null
                    ? balance.shipVisualRoot : ship.transform;
            }
            shellMat = SkyDressing.MakeMat(new Color(0.25f, 0.75f, 0.70f));
            shellMat.EnableKeyword("_EMISSION");
            if (shellMat.HasProperty("_EmissionColor"))
                shellMat.SetColor("_EmissionColor", new Color(0.10f, 0.45f, 0.40f));
        }

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            bool inMission = manager != null &&
                             (manager.runtime.phase == ExpeditionPhase.Active ||
                              manager.runtime.phase == ExpeditionPhase.ReturnReady);
            if (!inMission || rideParent == null) return;

            if (manager.IsAuthority)
            {
                HostGrow(manager);
                PublishWeights();
            }
            else
            {
                // Client: rebuild from the synced CSV whenever it changes.
                if (manager.runtime.barnaclesCsv != appliedCsv)
                    ApplyCsv(manager.runtime.barnaclesCsv);
            }
            count = entries.Count;
        }

        // ---------------- HOST ----------------

        private void HostGrow(ExpeditionManager manager)
        {
            var rt = manager.runtime;
            if (entries.Count >= maxBarnacles) return;

            bool inHazard = false;
            var storm = FindAnyObjectByType<StormSystem>();
            if (storm != null && storm.shipDepth > 0.15f) inHazard = true;
            // Whisper banks use the LOCAL player's depth; on the host that's a fine proxy for the
            // ship (the host is aboard in practice) and keeps the systems decoupled.
            if (WhisperFogSystem.LocalDistortion > 0.25f) inHazard = true;

            if (rt.threatLevel < 1 && !inHazard) return; // calm skies: nothing latches

            growthTimer += Time.deltaTime * (inHazard ? hazardGrowthMultiplier : 1f);
            if (growthTimer < baseGrowthInterval) return;
            growthTimer = 0f;

            var e = new Entry
            {
                id = "B" + idCounter++,
                localPos = new Vector3(
                    (rng.NextDouble() < 0.5 ? -railX : railX) * (0.9f + (float)rng.NextDouble() * 0.15f),
                    railY,
                    ((float)rng.NextDouble() * 2f - 1f) * railZRange),
                weight = barnacleWeight
            };
            entries.Add(e);
            BuildVisual(e);
            RebuildCsv(manager);
            manager.BroadcastEvent("Something has latched onto the hull. Scrape it off (E) before she lists.");
        }

        /// <summary>Feed our phantom weights into the balance sim (authority only — the sim is off on clients).</summary>
        private void PublishWeights()
        {
            if (balance == null) return;
            balance.externalWeights.Clear();
            for (int i = 0; i < entries.Count; i++)
                balance.externalWeights.Add(new ShipBalanceController.ExternalWeight
                {
                    localPos = entries[i].localPos,
                    weight = entries[i].weight
                });
        }

        /// <summary>Host: validate and execute a scrape (local press or a client's request).</summary>
        public void HostScrape(string id)
        {
            var manager = ExpeditionManager.Instance;
            if (manager == null || !manager.IsAuthority || string.IsNullOrEmpty(id)) return;

            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].id != id) continue;
                entries.RemoveAt(i);
                RemoveVisual(id);
                RebuildCsv(manager);
                PublishWeights();
                manager.progress.scrap += 1; // shells grind down into usable scrap
                manager.BroadcastEvent("Barnacle scraped off the hull (+1 scrap).");
                return;
            }
        }

        /// <summary>Route a local E-press on a barnacle: authority scrapes; a client asks the host.</summary>
        public static void RequestScrape(string id)
        {
            var manager = ExpeditionManager.Instance;
            var nm = NetworkManagerP2P.Instance;
            if (manager != null && manager.IsAuthority)
                Instance?.HostScrape(id);
            else if (nm != null)
                nm.SendScrapeRequest(id);
        }

        // ---------------- SYNC (CSV in the blob) ----------------

        private void RebuildCsv(ExpeditionManager manager)
        {
            var sb = new StringBuilder(entries.Count * 32);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (i > 0) sb.Append(';');
                sb.Append(e.id).Append(',')
                  .Append(e.localPos.x.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.localPos.y.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.localPos.z.ToString("0.00", CultureInfo.InvariantCulture)).Append(',')
                  .Append(e.weight.ToString("0.#", CultureInfo.InvariantCulture));
            }
            manager.runtime.barnaclesCsv = sb.ToString();
        }

        /// <summary>Client: reconcile entries + visuals with the host's CSV.</summary>
        private void ApplyCsv(string csv)
        {
            appliedCsv = csv ?? "";
            entries.Clear();

            var alive = new HashSet<string>();
            if (!string.IsNullOrEmpty(csv))
            {
                foreach (string part in csv.Split(';'))
                {
                    var f = part.Split(',');
                    if (f.Length != 5) continue;
                    var e = new Entry
                    {
                        id = f[0],
                        localPos = new Vector3(
                            float.Parse(f[1], CultureInfo.InvariantCulture),
                            float.Parse(f[2], CultureInfo.InvariantCulture),
                            float.Parse(f[3], CultureInfo.InvariantCulture)),
                        weight = float.Parse(f[4], CultureInfo.InvariantCulture)
                    };
                    entries.Add(e);
                    alive.Add(e.id);
                    if (!visuals.ContainsKey(e.id)) BuildVisual(e);
                }
            }

            // Remove visuals the host no longer lists (scraped by someone else).
            var stale = new List<string>();
            foreach (var kvp in visuals)
                if (!alive.Contains(kvp.Key)) stale.Add(kvp.Key);
            foreach (string id in stale) RemoveVisual(id);
        }

        // ---------------- VISUALS ----------------

        private void BuildVisual(Entry e)
        {
            var go = new GameObject("Barnacle_" + e.id);
            go.transform.SetParent(rideParent, false);
            go.transform.localPosition = e.localPos;

            var tag = go.AddComponent<Barnacle>();
            tag.id = e.id;

            // Trigger collider for the interaction ray only — never trips the player or cargo.
            var col = go.AddComponent<SphereCollider>();
            col.isTrigger = true;
            col.radius = 0.4f;

            // A little cluster of glowing shells.
            for (int i = 0; i < 3; i++)
            {
                var shell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                shell.name = "Shell";
                Destroy(shell.GetComponent<Collider>());
                shell.transform.SetParent(go.transform, false);
                shell.transform.localPosition = Random.insideUnitSphere * 0.18f;
                shell.transform.localScale = Vector3.one * Random.Range(0.16f, 0.30f);
                shell.GetComponent<Renderer>().sharedMaterial = shellMat;
            }
            visuals[e.id] = go;
        }

        private void RemoveVisual(string id)
        {
            if (visuals.TryGetValue(id, out GameObject go) && go != null)
                Destroy(go);
            visuals.Remove(id);
        }
    }
}

using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// WHISPER FOG — drifting banks of sickly, dead-calm fog. Inside one, your INSTRUMENTS LIE:
    /// the HUD text garbles, the chart table's markers jitter and the ship arrow spins wrong,
    /// and half-heard whispers address you by name. Navigation horror, not physical horror —
    /// the banks do no damage, they just make you doubt everything you read.
    ///
    /// SYNC-FREE DESIGN: bank spawn positions and drift headings come from the world seed, and
    /// their motion runs off runtime.elapsedSeconds — the HOST-SYNCED mission clock — so every
    /// peer computes the same bank positions with zero extra network state. Each peer's
    /// distortion is its own camera depth in the nearest bank (exposed via LocalDistortion for
    /// GameplayHUD / ShipMapTable).
    /// </summary>
    public class WhisperFogSystem : MonoBehaviour
    {
        /// <summary>0..1 — how deep the LOCAL player is inside a whisper bank right now.</summary>
        public static float LocalDistortion { get; private set; }

        [Header("Banks")]
        public int bankCount = 4;
        public Vector2 bankRadiusRange = new Vector2(260f, 420f);
        [Tooltip("Drift speed (m/s). Slow — these are pockets of dead air, not weather fronts.")]
        public float driftSpeed = 3f;

        [Header("Whispers")]
        [Tooltip("Local-only banner lines shown while inside a bank.")]
        public float whisperInterval = 14f;

        private static readonly string[] WhisperLines =
        {
            "...it knows your name...",
            "...the compass is wrong. trust me instead...",
            "...you left someone below...",
            "...she is still transmitting...",
            "...turn back. turn back. turn back...",
        };

        private struct Bank
        {
            public Vector3 start;     // origin-relative
            public Vector3 dir;       // fixed drift heading
            public float radius;
            public Transform visual;
        }

        private readonly List<Bank> banks = new List<Bank>();
        private Vector3 origin;
        private Vector3 halfExtent;
        private Transform localPlayer;
        private float clock;          // smoothed copy of the synced mission clock
        private float nextWhisper;
        private bool wasInside;

        private void Start()
        {
            var gen = FindAnyObjectByType<WorldGenerator>();
            origin = gen != null ? gen.transform.position : Vector3.zero;
            halfExtent = (gen != null ? gen.worldExtent : new Vector3(8000f, 700f, 8000f)) * 0.5f;

            var player = GameObject.Find("Player");
            localPlayer = player != null ? player.transform : null;

            var nm = NetworkManagerP2P.Instance;
            var rng = new System.Random((nm != null ? nm.worldSeed : System.Environment.TickCount) * 17 + 3);

            Material fogMat = SkyDressing.MakeMat(new Color(0.42f, 0.50f, 0.42f));
            for (int i = 0; i < bankCount; i++)
            {
                float dirAng = (float)(rng.NextDouble() * Mathf.PI * 2.0);
                float radius = Mathf.Lerp(bankRadiusRange.x, bankRadiusRange.y, (float)rng.NextDouble());
                banks.Add(new Bank
                {
                    start = new Vector3(
                        ((float)rng.NextDouble() * 2f - 1f) * halfExtent.x * 0.7f,
                        ((float)rng.NextDouble() * 2f - 1f) * halfExtent.y * 0.4f,
                        ((float)rng.NextDouble() * 2f - 1f) * halfExtent.z * 0.7f),
                    dir = new Vector3(Mathf.Cos(dirAng), 0f, Mathf.Sin(dirAng)),
                    radius = radius,
                    visual = BuildBankVisual(rng, fogMat, radius)
                });
            }
        }

        private Transform BuildBankVisual(System.Random rng, Material mat, float radius)
        {
            var root = new GameObject("WhisperBank").transform;
            for (int i = 0; i < 12; i++)
            {
                float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0);
                float dist = radius * Mathf.Sqrt((float)rng.NextDouble()) * 0.9f;
                float w = Mathf.Lerp(radius * 0.5f, radius * 1.0f, (float)rng.NextDouble());

                var puff = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                puff.name = "FogPuff";
                Destroy(puff.GetComponent<Collider>()); // dead air, not a wall
                puff.transform.SetParent(root, false);
                puff.transform.localPosition = new Vector3(
                    Mathf.Cos(ang) * dist,
                    ((float)rng.NextDouble() - 0.5f) * radius * 0.5f,
                    Mathf.Sin(ang) * dist);
                puff.transform.localScale = new Vector3(w, w * 0.55f, w);
                puff.GetComponent<Renderer>().sharedMaterial = mat;
            }
            return root;
        }

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            bool inMission = manager != null &&
                             (manager.runtime.phase == ExpeditionPhase.Active ||
                              manager.runtime.phase == ExpeditionPhase.ReturnReady);
            if (!inMission)
            {
                LocalDistortion = 0f;
                return;
            }

            // Smooth local clock corrected toward the host-synced mission time, so bank motion
            // is identical (within centimeters) on every peer without extra packets.
            clock += Time.deltaTime;
            clock = Mathf.Lerp(clock, manager.runtime.elapsedSeconds, 0.05f);

            float deepest = 0f;
            Vector3 playerPos = localPlayer != null ? localPlayer.position : origin;

            for (int i = 0; i < banks.Count; i++)
            {
                Bank b = banks[i];
                Vector3 pos = origin + WrapIntoBounds(b.start + b.dir * (driftSpeed * clock));
                if (b.visual != null) b.visual.position = pos;

                Vector2 d = new Vector2(playerPos.x - pos.x, playerPos.z - pos.z);
                float depth = Mathf.Clamp01(1f - d.magnitude / b.radius);
                if (depth > deepest) deepest = depth;
            }
            LocalDistortion = deepest;

            // Whispers (LOCAL banner — each crew member hears their own).
            if (deepest > 0.25f)
            {
                if (!wasInside)
                {
                    wasInside = true;
                    nextWhisper = Time.time + 2f;
                }
                if (Time.time >= nextWhisper)
                {
                    nextWhisper = Time.time + whisperInterval * Random.Range(0.7f, 1.3f);
                    NetworkManagerP2P.Instance?.ShowBanner(
                        WhisperLines[Random.Range(0, WhisperLines.Length)], 3f);
                }
            }
            else
            {
                wasInside = false;
            }
        }

        /// <summary>Wrap a drifting origin-relative position back across the world bounds.</summary>
        private Vector3 WrapIntoBounds(Vector3 p)
        {
            p.x = Mathf.Repeat(p.x + halfExtent.x, halfExtent.x * 2f) - halfExtent.x;
            p.z = Mathf.Repeat(p.z + halfExtent.z, halfExtent.z * 2f) - halfExtent.z;
            return p;
        }

        private void OnDisable() => LocalDistortion = 0f;

        /// <summary>
        /// Corrupt readable text by the local distortion level — the shared "instruments lie"
        /// primitive used by GameplayHUD (and anything else that shows information).
        /// Reseeds every few frames so it flickers rather than settles.
        /// </summary>
        public static string Garble(string text)
        {
            float severity = LocalDistortion;
            if (severity < 0.25f || string.IsNullOrEmpty(text)) return text;

            const string noise = "▓▒░#%&?!";
            var r = new System.Random(Time.frameCount / 4);
            var chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (!char.IsWhiteSpace(chars[i]) && r.NextDouble() < severity * 0.30)
                    chars[i] = noise[r.Next(noise.Length)];
            }
            return new string(chars);
        }
    }
}

using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// THE STATIC — the Black Navigation Box is itself a threat. While corrupted cargo is aboard
    /// the ship, the box periodically SCREAMS: for a few seconds every peer gets a mouse-look
    /// scramble, a flickering static overlay, and the deck cargo hops. Screams start rare and
    /// come faster the longer the box stays aboard — hauling it home carelessly gets expensive.
    ///
    /// HOST decides when a scream fires (runtime.screamActive rides the sync blob, so every
    /// peer screams together); each peer applies its own local FX from that flag.
    /// Created in the world scene by VerticalSliceBootstrap.
    /// </summary>
    public class StaticScreamSystem : MonoBehaviour
    {
        [Header("Timing")]
        [Tooltip("Seconds after corrupted cargo first comes aboard before the first scream.")]
        public float firstScreamDelay = 25f;
        [Tooltip("Scream interval range early on (shrinks the longer the box stays aboard).")]
        public Vector2 intervalRange = new Vector2(45f, 80f);
        [Tooltip("Minimum interval no matter how long it's been aboard.")]
        public float minInterval = 20f;
        [Tooltip("How long each scream lasts.")]
        public float screamDuration = 2.8f;

        [Header("Effects")]
        [Tooltip("Peak look scramble (degrees per frame) at the height of a scream.")]
        public float lookNoiseStrength = 1.6f;
        [Tooltip("Upward velocity kick given to loose deck cargo when a scream starts.")]
        public float cargoHop = 1.4f;

        [Header("Runtime (read-only)")]
        public float aboardSeconds; // how long corrupted cargo has been aboard (host)

        private float nextScreamAt = -1f;
        private float screamEndsAt;
        private FirstPersonController localController;
        private ShipPlatformArea platform;

        private void Start()
        {
            var player = GameObject.Find("Player");
            localController = player != null ? player.GetComponent<FirstPersonController>() : null;
            var ship = GameObject.Find("ShipRoot");
            platform = ship != null ? ship.GetComponent<ShipPlatformArea>() : null;
        }

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            bool inMission = manager != null &&
                             (manager.runtime.phase == ExpeditionPhase.Active ||
                              manager.runtime.phase == ExpeditionPhase.ReturnReady);
            if (!inMission)
            {
                ClearNoise();
                return;
            }

            if (manager.IsAuthority)
                HostDrive(manager);

            // Every peer: local FX while the synced flag is up.
            if (manager.runtime.screamActive)
                ApplyScreamFx();
            else
                ClearNoise();
        }

        // ---------------- HOST ----------------

        private void HostDrive(ExpeditionManager manager)
        {
            var rt = manager.runtime;
            bool corruptedAboard = manager.corruptionAboard > 0.5f;

            if (!corruptedAboard)
            {
                aboardSeconds = 0f;
                nextScreamAt = -1f;
                if (rt.screamActive && Time.time >= screamEndsAt) rt.screamActive = false;
                return;
            }

            aboardSeconds += Time.deltaTime;
            if (nextScreamAt < 0f)
                nextScreamAt = Time.time + firstScreamDelay;

            if (rt.screamActive)
            {
                if (Time.time >= screamEndsAt) rt.screamActive = false;
                return;
            }

            if (Time.time >= nextScreamAt)
            {
                rt.screamActive = true;
                screamEndsAt = Time.time + screamDuration;

                // Intervals tighten as the box festers aboard (full pressure after ~5 minutes).
                float festering = Mathf.Clamp01(aboardSeconds / 300f);
                float interval = Mathf.Lerp(Random.Range(intervalRange.x, intervalRange.y), minInterval, festering);
                nextScreamAt = Time.time + screamDuration + interval;

                manager.BroadcastEvent("!! THE BOX SCREAMS IN A DEAD MAN'S VOICE !!");

                // The scream physically startles the cargo.
                if (platform != null)
                {
                    var items = platform.itemsInPlatform;
                    for (int i = 0; i < items.Count; i++)
                    {
                        var item = items[i];
                        if (item == null || item.isHeld || item.Body == null || item.Body.isKinematic) continue;
                        item.Body.AddForce(Vector3.up * cargoHop + Random.insideUnitSphere * 0.5f,
                                           ForceMode.VelocityChange);
                    }
                }
            }
        }

        // ---------------- EVERY PEER ----------------

        private void ApplyScreamFx()
        {
            if (localController == null || !localController.enabled) return;
            float t = Time.time * 23f;
            localController.externalLookNoise = new Vector2(
                (Mathf.PerlinNoise(t, 0.3f) - 0.5f) * 2f,
                (Mathf.PerlinNoise(t, 7.9f) - 0.5f) * 2f) * lookNoiseStrength;
        }

        private void ClearNoise()
        {
            if (localController != null)
                localController.externalLookNoise = Vector2.zero;
        }

        private void OnDisable() => ClearNoise();

        /// <summary>Flickering static overlay while the scream is live (every peer).</summary>
        private void OnGUI()
        {
            var manager = ExpeditionManager.Instance;
            if (manager == null || !manager.runtime.screamActive) return;

            var r = new System.Random(Time.frameCount / 2); // reroll every couple of frames
            GUI.color = new Color(1f, 1f, 1f, 0.05f + (float)r.NextDouble() * 0.10f);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

            // Torn horizontal interference bars.
            for (int i = 0; i < 6; i++)
            {
                GUI.color = new Color(0f, 0f, 0f, 0.15f + (float)r.NextDouble() * 0.25f);
                float y = (float)r.NextDouble() * Screen.height;
                GUI.DrawTexture(new Rect(0, y, Screen.width, 2f + (float)r.NextDouble() * 10f), Texture2D.whiteTexture);
            }

            GUI.color = new Color(1f, 1f, 1f, 0.7f + (float)r.NextDouble() * 0.3f);
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.MiddleCenter,
                fontSize = 22,
                fontStyle = FontStyle.Bold
            };
            GUI.Label(new Rect(0, Screen.height * 0.3f, Screen.width, 40f), "▓▓ S I G N A L ▓▓", style);
            GUI.color = Color.white;
        }
    }
}

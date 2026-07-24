using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// The player hub / skydock logic. The hub GEOMETRY (bunker room, menu board, upgrade kiosks,
    /// dock, gangway) is authored directly in HubScene — this component only handles behaviour:
    ///
    ///  1. Makes the docked ship NON-DRIVEABLE (same ShipRoot prefab, but flight + helm are disabled).
    ///  2. Watches for EVERY player standing on the ship; when all are aboard, runs a short countdown.
    ///  3. At zero, the host launches everyone into a freshly-seeded procedural world (WorldScene).
    ///
    /// Later the board's selections (challenges, quests, events, areas) will feed into that launch.
    /// The launch reuses NetworkManagerP2P's host-authoritative StartGame path; the countdown value is
    /// relayed to clients in the existing State packets (NetworkManagerP2P.hubCountdown) for display.
    /// </summary>
    public class HubController : MonoBehaviour
    {
        [Header("Launch")]
        [Tooltip("Scene to load once the countdown completes.")]
        public string worldSceneName = "WorldScene";
        [Tooltip("Seconds every player must remain aboard the parked ship before launch.")]
        public float countdownSeconds = 5f;

        [Header("Runtime (read-only)")]
        [SerializeField] private bool allAboard;
        [SerializeField] private float countdown = -1f;
        [SerializeField] private string launchBlockReason = "";

        private NetworkManagerP2P nm;

        private void Start()
        {
            nm = NetworkManagerP2P.Instance;
            ParkAndNeuterShip();
        }

        private void Update()
        {
            if (nm == null) nm = NetworkManagerP2P.Instance;
            if (nm == null) return;

            if (nm.IsWorldAuthority)
            {
                // Host / solo owns the launch decision and drives the countdown value the clients see.
                // Launch now ALSO requires a launchable expedition: selected at the Sky Chart with
                // the fuel to cover it (ExpeditionManager validates + deducts).
                var manager = ExpeditionManager.Instance;
                bool expeditionReady = manager == null || manager.CanLaunch(out launchBlockReason);
                if (expeditionReady) launchBlockReason = "";

                allAboard = nm.AreAllPlayersAboard();
                if (allAboard && expeditionReady)
                {
                    if (countdown < 0f) countdown = countdownSeconds;
                    countdown -= Time.deltaTime;
                    nm.hubCountdown = Mathf.Max(0f, countdown);

                    if (countdown <= 0f)
                    {
                        enabled = false;               // scene is about to unload; stop ticking
                        // Commit the expedition (deduct fuel, phase -> Active) BEFORE the launch
                        // broadcast so the StartGame packet carries the expedition id.
                        manager?.HostBeginExpedition();
                        nm.LaunchToWorld(worldSceneName);
                    }
                }
                else
                {
                    countdown = -1f;
                    nm.hubCountdown = -1f;
                }
            }
            else
            {
                // Client: purely mirror the host's networked countdown for display.
                countdown = nm.hubCountdown;
                allAboard = countdown >= 0f;
            }
        }

        /// <summary>
        /// Same ship, but it can't be flown in the hub: disable its flight controller and make the helm
        /// inert so pressing E at the wheel does nothing. Balance/platform stay enabled (harmless with
        /// no cargo) so ShipRider still detects the deck and players ride it smoothly.
        /// </summary>
        private void ParkAndNeuterShip()
        {
            var ship = GameObject.Find("ShipRoot");
            if (ship == null) return;

            var move = ship.GetComponent<ShipMovementController>();
            if (move != null) move.enabled = false;

            var helm = FindAnyObjectByType<ShipHelm>();
            if (helm != null)
            {
                helm.enabled = false;
                // PlayerInteraction finds the helm via GetComponentInParent on whatever collider the
                // E-ray hits, so disable EVERY collider under the wheel — otherwise a child collider
                // would still let someone take the (disabled) helm.
                foreach (var col in helm.GetComponentsInChildren<Collider>(true))
                    col.enabled = false;
            }
        }

        private void OnGUI()
        {
            if (countdown >= 0f)
            {
                var style = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 26,
                    fontStyle = FontStyle.Bold
                };
                style.normal.textColor = Color.white;

                int secs = Mathf.CeilToInt(countdown);
                GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTexture(new Rect(Screen.width * 0.5f - 220f, Screen.height * 0.14f, 440f, 70f), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(0f, Screen.height * 0.14f, Screen.width, 70f), $"LAUNCHING IN {secs}", style);
            }
            else
            {
                var hint = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 16
                };
                hint.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
                string text = !string.IsNullOrEmpty(launchBlockReason)
                    ? launchBlockReason + "  —  press F at the menu board"
                    : "Board the ship with your whole crew to launch the expedition";
                GUI.Label(new Rect(0f, Screen.height - 46f, Screen.width, 30f), text, hint);
            }
        }
    }
}

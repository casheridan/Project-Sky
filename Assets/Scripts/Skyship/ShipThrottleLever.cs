using UnityEngine;

namespace Skyship
{
    /// <summary>The five telegraph detents, slowest-to-fastest. Order matters (indexes arrays below).</summary>
    public enum ThrottleStage
    {
        Reverse = 0,
        Neutral = 1,
        Slow = 2,
        Medium = 3,
        Fast = 4
    }

    /// <summary>
    /// The ship's engine telegraph: a big 5-detent deck lever standing STARBOARD of the wheel
    /// (built procedurally by ShipHelm at Awake). It is the ship's ONLY throttle: the wheel only
    /// steers, and the ship holds the set speed even with nobody at the helm — one player can run
    /// the throttle while another steers (or one player does both). Unlike the spring-loaded lift
    /// lever, it STAYS where you click it.
    ///
    /// Hold F to work it; see ShipDeckLever for the grab/detent interaction and sync model.
    /// The host reads CurrentThrottle in NetworkManagerP2P.DriveShipFromPilot.
    /// </summary>
    public class ShipThrottleLever : ShipDeckLever
    {
        private static readonly float[] DetentAngles = { -30f, 0f, 15f, 30f, 45f };
        private static readonly float[] StageThrottle = { -0.5f, 0f, 0.35f, 0.7f, 1f };
        private static readonly Color[] KnobColors =
        {
            new Color(0.85f, 0.25f, 0.20f), // reverse
            new Color(0.80f, 0.80f, 0.80f), // neutral
            new Color(0.60f, 0.75f, 0.40f), // slow
            new Color(0.40f, 0.70f, 0.30f), // medium
            new Color(0.15f, 0.65f, 0.25f)  // fast
        };

        protected override float[] Detents => DetentAngles;
        protected override Color[] StageColors => KnobColors;

        public float CurrentThrottle => StageThrottle[Mathf.Clamp(Stage, 0, StageThrottle.Length - 1)];

        protected override void RequestStage(int newStage)
        {
            var nm = NetworkManagerP2P.Instance;
            if (nm != null) nm.RequestThrottleStage(newStage);
            else ApplyStage(newStage); // scene running without a network manager
        }

        /// <summary>Build the telegraph starboard of the wheel, hinged on the deck. Idempotent per ship.</summary>
        public static ShipThrottleLever CreateNear(Transform helm)
        {
            if (helm == null || helm.parent == null) return null;
            var existing = helm.parent.GetComponentInChildren<ShipThrottleLever>();
            if (existing != null) return existing;

            var helmComp = helm.GetComponent<ShipHelm>();
            float deckY = helmComp != null && helmComp.pilotSeat != null
                ? helmComp.pilotSeat.position.y
                : helm.position.y;

            var go = new GameObject("ThrottleLever");
            go.transform.SetParent(helm.parent, false);
            Vector3 pos = helm.position + helm.right * 1.25f; // starboard, room for the arm's swing
            pos.y = deckY;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(helm.forward, Vector3.up);
            go.layer = helm.gameObject.layer;

            var lever = go.AddComponent<ShipThrottleLever>();
            lever.Initialize((int)ThrottleStage.Neutral, 1.3f, 0.26f);
            return lever;
        }
    }
}

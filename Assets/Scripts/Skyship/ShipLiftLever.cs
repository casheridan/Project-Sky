using UnityEngine;

namespace Skyship
{
    /// <summary>The three lift detents. Order matters (indexes arrays below).</summary>
    public enum LiftStage
    {
        Down = 0,
        Neutral = 1,
        Up = 2
    }

    /// <summary>
    /// The ship's lift lever: a big 3-detent deck lever standing PORT of the wheel, opposite the
    /// engine telegraph (built procedurally by ShipHelm at Awake). It is the ship's ONLY climb
    /// control: push toward the bow to climb, pull back to descend. SPRING-LOADED — letting go
    /// of F snaps it back to Neutral, so the ship only climbs/descends while someone is actively
    /// holding the lever over a detent.
    ///
    /// Hold F to work it; see ShipDeckLever for the grab/detent interaction and sync model.
    /// The host reads CurrentLift in NetworkManagerP2P.DriveShipFromPilot.
    /// </summary>
    public class ShipLiftLever : ShipDeckLever
    {
        private static readonly float[] DetentAngles = { -30f, 0f, 30f };
        private static readonly float[] StageLift = { -1f, 0f, 1f };
        private static readonly Color[] KnobColors =
        {
            new Color(0.85f, 0.55f, 0.20f), // down (descend)
            new Color(0.80f, 0.80f, 0.80f), // neutral
            new Color(0.35f, 0.60f, 0.90f)  // up (climb)
        };

        protected override float[] Detents => DetentAngles;
        protected override Color[] StageColors => KnobColors;

        /// <summary>Spring home to Neutral whenever the lever is released.</summary>
        protected override int SpringStage => (int)LiftStage.Neutral;

        public float CurrentLift => StageLift[Mathf.Clamp(Stage, 0, StageLift.Length - 1)];

        protected override void RequestStage(int newStage)
        {
            var nm = NetworkManagerP2P.Instance;
            if (nm != null) nm.RequestLiftStage(newStage);
            else ApplyStage(newStage); // scene running without a network manager
        }

        /// <summary>Build the lift lever port of the wheel, hinged on the deck. Idempotent per ship.</summary>
        public static ShipLiftLever CreateNear(Transform helm)
        {
            if (helm == null || helm.parent == null) return null;
            var existing = helm.parent.GetComponentInChildren<ShipLiftLever>();
            if (existing != null) return existing;

            var helmComp = helm.GetComponent<ShipHelm>();
            float deckY = helmComp != null && helmComp.pilotSeat != null
                ? helmComp.pilotSeat.position.y
                : helm.position.y;

            var go = new GameObject("LiftLever");
            go.transform.SetParent(helm.parent, false);
            Vector3 pos = helm.position - helm.right * 1.25f; // port, opposite the telegraph
            pos.y = deckY;
            go.transform.position = pos;
            go.transform.rotation = Quaternion.LookRotation(helm.forward, Vector3.up);
            go.layer = helm.gameObject.layer;

            var lever = go.AddComponent<ShipLiftLever>();
            lever.Initialize((int)LiftStage.Neutral, 1.2f, 0.24f);
            return lever;
        }
    }
}

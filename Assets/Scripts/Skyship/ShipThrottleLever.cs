using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// The ship's engine telegraph, standing STARBOARD of the wheel (built procedurally by
    /// ShipHelm at Awake). It is the ship's ONLY throttle: the wheel only steers, and the ship
    /// holds the set speed even with nobody at the helm.
    ///
    /// FEEL (reworked): the FORWARD range is fully ANALOG — the arm sweeps smoothly from neutral
    /// to full ahead and holds wherever you leave it, giving fine speed control when creeping up
    /// on a POI. Only REVERSE keeps the old telegraph CLICK: pulling back past neutral has a
    /// deliberate detent clunk into reverse, and pushing forward clicks back out to neutral —
    /// so you always know when the props flip, and neutral itself acts as a hard stop you can
    /// feel (the arm rests there before the reverse click engages).
    ///
    /// Hold F to work it; see ShipDeckLever for the grab interaction. Host-authoritative: the
    /// value routes through NetworkManagerP2P.RequestThrottleValue (rate-limited while dragging)
    /// and mirrors to everyone via the float in State packets.
    /// </summary>
    public class ShipThrottleLever : ShipDeckLever
    {
        // Vestigial detent tables for the base class (only used as arm-angle bookkeeping bounds —
        // OnGrabTravel/UpdateVisual are fully overridden with the analog behavior below).
        private static readonly float[] LegacyDetents = { -30f, 0f };
        private static readonly Color[] LegacyColors =
        {
            new Color(0.85f, 0.25f, 0.20f), // reverse
            new Color(0.80f, 0.80f, 0.80f)  // neutral/forward zone
        };

        [Header("Analog Throttle")]
        [Tooltip("Throttle applied while clicked into the reverse detent.")]
        public float reverseThrottle = -0.5f;
        [Tooltip("Arm angle at the reverse detent (degrees, negative = leaned aft).")]
        public float reverseAngle = -30f;
        [Tooltip("Arm angle at full ahead (degrees).")]
        public float fullForwardAngle = 45f;
        [Tooltip("View travel (pixels) for a full neutral→full-ahead sweep. Higher = finer control.")]
        public float analogSweep = 700f;

        [Header("Runtime (read-only)")]
        [SerializeField] private float forwardValue; // 0..1 analog when not in reverse
        [SerializeField] private bool inReverse;

        private float nextNetSend;
        private float lastSentValue = -999f;

        protected override float[] Detents => LegacyDetents;
        protected override Color[] StageColors => LegacyColors;
        protected override void RequestStage(int newStage) { } // analog lever: unused base path

        /// <summary>The throttle the host feeds into ShipMovementController (-reverse..1).</summary>
        public float CurrentThrottle => inReverse ? reverseThrottle : forwardValue;

        /// <summary>
        /// Analog grab: forward of neutral the arm tracks the view smoothly; at/under neutral the
        /// old detent accumulation kicks in so reverse engages (and disengages) with a click.
        /// </summary>
        protected override void OnGrabTravel(float travel)
        {
            if (inReverse)
            {
                // Clicked into reverse: push forward far enough to clunk back out to neutral.
                accum = Mathf.Clamp(accum + travel, -detentThreshold, detentThreshold);
                if (accum >= detentThreshold)
                {
                    inReverse = false;
                    forwardValue = 0f;
                    accum = 0f;
                }
                UpdateVisual(Mathf.Clamp(accum / detentThreshold, -1f, 1f) * detentWiggle);
            }
            else if (forwardValue <= 0f && travel < 0f)
            {
                // Resting at neutral and still pulling back: lean toward the reverse click.
                accum = Mathf.Clamp(accum + travel, -detentThreshold, 0f);
                if (accum <= -detentThreshold)
                {
                    inReverse = true;
                    accum = 0f;
                }
                UpdateVisual(Mathf.Clamp(accum / detentThreshold, -1f, 0f) * detentWiggle);
            }
            else
            {
                // Analog forward range: the arm follows the view directly and stays put.
                accum = 0f;
                forwardValue = Mathf.Clamp01(forwardValue + travel / Mathf.Max(1f, analogSweep));
                UpdateVisual(0f);
            }

            PushValue(false);
        }

        protected override void OnReleased() => PushValue(true); // final value lands exactly

        /// <summary>Send the current value to the authority (rate-limited while dragging).</summary>
        private void PushValue(bool force)
        {
            float cur = CurrentThrottle;
            if (!force && (Time.time < nextNetSend || Mathf.Abs(cur - lastSentValue) < 0.01f))
                return;
            nextNetSend = Time.time + 0.1f;
            lastSentValue = cur;

            var nm = NetworkManagerP2P.Instance;
            if (nm != null) nm.RequestThrottleValue(cur);
        }

        /// <summary>Set the throttle outright (authority decision or local/optimistic drag).</summary>
        public void ApplyValue(float value)
        {
            value = Mathf.Clamp(value, -1f, 1f);
            inReverse = value < -0.01f;
            forwardValue = Mathf.Clamp01(value);
            if (!IsGrabbed) UpdateVisual(0f);
        }

        /// <summary>Mirror the host's throttle from State packets. Ignored while the local player
        /// is dragging the lever (their optimistic position wins visually).</summary>
        public void ApplyRemoteValue(float value)
        {
            if (IsGrabbed || Mathf.Abs(value - CurrentThrottle) < 0.005f) return;
            ApplyValue(value);
        }

        /// <summary>Arm angle + knob tint track the analog value (green deepens toward full ahead).</summary>
        protected override void UpdateVisual(float wiggleDegrees)
        {
            float angle = inReverse ? reverseAngle : Mathf.Lerp(0f, fullForwardAngle, forwardValue);
            SetArmAngle(angle + wiggleDegrees);

            if (knobMat != null)
            {
                Color c = inReverse
                    ? LegacyColors[0]
                    : Color.Lerp(LegacyColors[1], new Color(0.15f, 0.65f, 0.25f), forwardValue);
                knobMat.color = c;
                if (knobMat.HasProperty("_BaseColor")) knobMat.SetColor("_BaseColor", c);
            }
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
            lever.Initialize(1, 1.3f, 0.26f); // stage 1 = the neutral/forward zone
            return lever;
        }
    }
}

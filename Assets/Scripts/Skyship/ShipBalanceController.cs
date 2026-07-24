using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Core of the prototype. Reads weight directly from all CargoItems resting on the deck
    /// (retrieved from ShipPlatformArea), computes infinite-resolution load and
    /// roll/pitch torque imbalance, tilts ONLY the ShipVisualRoot child, and exposes
    /// handling modifiers + a load percentage for the movement/failure systems.
    ///
    /// This is a fully continuous, coordinate-weighted balance model where EVERY
    /// cargo box's physical coordinates on any deck surface contribute to balance.
    /// </summary>
    // Anti-jitter execution chain (see ShipRider): tilt applies right after ship movement
    // (-100) and before the deck-carry (-50) and player move (0).
    [DefaultExecutionOrder(-90)]
    public class ShipBalanceController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Child object that visually tilts. NEVER the gameplay/physics root.")]
        public Transform shipVisualRoot;

        [Tooltip("The ShipPlatformArea that tracks all items on the deck. Found automatically if empty.")]
        public ShipPlatformArea platformArea;

        [Header("Capacity & Imbalance Tuning")]
        [Tooltip("Total weight considered 100% load.")]
        public float maxCapacity = 1000f;
        [Tooltip("Maximum roll torque (weight * distance) that maps to maximum roll angle. Larger values make the ship more stable (tilts less).")]
        public float maxSideImbalance = 1200f;
        [Tooltip("Maximum pitch torque (weight * distance) that maps to maximum pitch angle. Larger values make the ship more stable (tilts less).")]
        public float maxFrontBackImbalance = 2000f;

        [Header("Tilt Tuning")]
        public float maxRollAngle = 18f;
        public float maxPitchAngle = 14f;
        [Tooltip("How quickly the visual tilt eases toward its target.")]
        public float tiltSmoothSpeed = 2f;

        [Header("Handling Tuning")]
        [Tooltip("Speed multiplier at 0% load.")]
        public float speedAtEmpty = 1f;
        [Tooltip("Speed multiplier at (or above) 100% load.")]
        public float speedAtFull = 0.5f;
        [Tooltip("Max steering pull toward the heavy side at full roll imbalance (deg/sec).")]
        public float maxTurnPull = 15f;

        [Header("Calculated Weights (read-only)")]
        public float totalWeight;
        public float leftWeight;
        public float rightWeight;
        public float frontWeight;
        public float rearWeight;

        [Header("Calculated Imbalance (read-only)")]
        [Tooltip("Normalized -1..1. Right-heavy positive, left-heavy negative.")]
        public float rollImbalance;
        [Tooltip("Normalized -1..1. Front-heavy positive, rear-heavy negative.")]
        public float pitchImbalance;
        [Tooltip("totalWeight / maxCapacity. 1 = 100% load.")]
        [Range(0f, 2f)] public float loadPercent;

        [Header("Handling Output (read-only)")]
        public float speedMultiplier = 1f;
        [Tooltip("Steering bias toward the heavy side (deg/sec).")]
        public float turnPull;
        [Tooltip("0..1 strain from overload + imbalance, for warnings/FX.")]
        public float engineStrain;

        // External tilt constraints from ship fixtures resting against the world (e.g. the
        // boarding ramp grounded on terrain: the ground props the ship up instead of letting
        // the tilt push the ramp through it). Owners re-assert these every frame while active
        // and reset them to ±infinity when their contact ends. Sign convention: +Z roll = port
        // (-X) down, so a grounded PORT ramp lowers externalRollMax.
        [System.NonSerialized] public float externalRollMin = float.NegativeInfinity;
        [System.NonSerialized] public float externalRollMax = float.PositiveInfinity;

        // Storm rocking written by StormSystem (host only): additive roll/pitch degrees layered on
        // top of the cargo-driven tilt target. Clients see it through the synced visualTilt.
        [System.NonSerialized] public float externalRollAdd = 0f;
        [System.NonSerialized] public float externalPitchAdd = 0f;

        /// <summary>Phantom weight stuck to the hull (e.g. barnacles): counts toward load and
        /// torque exactly like a crate at that ShipVisualRoot-local position. Owned/rebuilt by
        /// BarnacleSystem on the authority each frame.</summary>
        public struct ExternalWeight { public Vector3 localPos; public float weight; }
        [System.NonSerialized] public List<ExternalWeight> externalWeights = new List<ExternalWeight>();

        // One-shot tilt jolts (e.g. a leviathan breach slamming the deck): decay back to zero.
        private float joltRoll, joltPitch;

        /// <summary>Kick the deck: an impulse (degrees) layered on the tilt target that decays away.</summary>
        public void AddTiltImpulse(float roll, float pitch)
        {
            joltRoll += roll;
            joltPitch += pitch;
        }

        // Reused each frame to avoid allocations when gathering cargo held by aboard players.
        private readonly List<CargoItem> heldAboard = new List<CargoItem>();

        private void Start()
        {
            if (platformArea == null)
                platformArea = GetComponent<ShipPlatformArea>();
        }

        private void Update()
        {
            RecalculateBalance();
            ApplyTilt();
        }

        private void RecalculateBalance()
        {
            totalWeight = 0f;
            leftWeight = 0f;
            rightWeight = 0f;
            frontWeight = 0f;
            rearWeight = 0f;

            float rollTorque = 0f;
            float pitchTorque = 0f;

            if (shipVisualRoot == null || platformArea == null) return;

            // Retrieve the active list of cargo items physically inside our deck bounds
            var activeItems = platformArea.itemsInPlatform;

            for (int i = 0; i < activeItems.Count; i++)
            {
                CargoItem item = activeItems[i];
                if (item == null) continue;

                // Only count loose items resting on deck (exclude held items)
                if (!item.isHeld)
                {
                    float w = item.weight;
                    totalWeight += w;

                    // Get the exact coordinates of this item relative to the ship center/pivot
                    Vector3 relativePos = shipVisualRoot.InverseTransformPoint(item.transform.position);

                    // Add to continuous torque: Torque = Weight * Relative Distance from pivot
                    rollTorque += w * relativePos.x;
                    pitchTorque += w * relativePos.z;

                    // Bin into directional weights for inspector-friendly diagnostics
                    if (relativePos.x < -0.1f) leftWeight += w;
                    else if (relativePos.x > 0.1f) rightWeight += w;

                    if (relativePos.z > 0.1f) frontWeight += w;
                    else if (relativePos.z < -0.1f) rearWeight += w;
                }
            }

            // Also count cargo currently HELD by players standing on the deck, at the carrier's
            // position. This makes carrying a crate tip the ship toward the carrier (only while
            // carrying), and stops a held crate from dodging its weight until it's set down.
            var nm = NetworkManagerP2P.Instance;
            if (nm != null)
            {
                nm.CollectAboardHeldCargo(heldAboard);
                for (int i = 0; i < heldAboard.Count; i++)
                {
                    CargoItem item = heldAboard[i];
                    if (item == null) continue;

                    float w = item.weight;
                    totalWeight += w;

                    Vector3 relativePos = shipVisualRoot.InverseTransformPoint(item.transform.position);
                    rollTorque += w * relativePos.x;
                    pitchTorque += w * relativePos.z;

                    if (relativePos.x < -0.1f) leftWeight += w;
                    else if (relativePos.x > 0.1f) rightWeight += w;

                    if (relativePos.z > 0.1f) frontWeight += w;
                    else if (relativePos.z < -0.1f) rearWeight += w;
                }
            }

            // Phantom hull weights (barnacles): same continuous torque math as deck cargo.
            for (int i = 0; i < externalWeights.Count; i++)
            {
                float w = externalWeights[i].weight;
                Vector3 lp = externalWeights[i].localPos;
                totalWeight += w;
                rollTorque += w * lp.x;
                pitchTorque += w * lp.z;
                if (lp.x < -0.1f) leftWeight += w; else if (lp.x > 0.1f) rightWeight += w;
                if (lp.z > 0.1f) frontWeight += w; else if (lp.z < -0.1f) rearWeight += w;
            }

            loadPercent = maxCapacity > 0.01f ? totalWeight / maxCapacity : 0f;

            // Normalized -1..1 imbalances.
            rollImbalance = Mathf.Clamp(rollTorque / Mathf.Max(0.01f, maxSideImbalance), -1f, 1f);
            pitchImbalance = Mathf.Clamp(pitchTorque / Mathf.Max(0.01f, maxFrontBackImbalance), -1f, 1f);

            // Handling outputs.
            float loadClamped = Mathf.Clamp01(loadPercent);
            speedMultiplier = Mathf.Lerp(speedAtEmpty, speedAtFull, loadClamped);
            turnPull = rollImbalance * maxTurnPull; // bias toward the heavy side
            engineStrain = Mathf.Clamp01(
                Mathf.Max(0f, loadPercent - 1f) +                                   // overload term
                (Mathf.Abs(rollImbalance) + Mathf.Abs(pitchImbalance)) * 0.5f);     // imbalance term
        }

        private void ApplyTilt()
        {
            if (shipVisualRoot == null) return;

            // Roll about Z from left/right weight, pitch about X from front/rear.
            // Signs assume +X = right, +Z = forward.
            // Decaying one-shot jolts (breaches etc.) on top of the steady external adds.
            joltRoll = Mathf.MoveTowards(joltRoll, 0f, 12f * Time.deltaTime);
            joltPitch = Mathf.MoveTowards(joltPitch, 0f, 12f * Time.deltaTime);

            float targetRoll = -rollImbalance * maxRollAngle + externalRollAdd + joltRoll;    // heavy-right dips right
            float targetPitch = pitchImbalance * maxPitchAngle + externalPitchAdd + joltPitch; // heavy-front dips nose down

            // Fixtures braced against the world (grounded boarding ramp) cap how far the ship
            // may lean toward them — the ground pushes back through the fixture.
            targetRoll = Mathf.Clamp(targetRoll, externalRollMin, externalRollMax);

            Quaternion target = Quaternion.Euler(targetPitch, 0f, targetRoll);
            shipVisualRoot.localRotation = Quaternion.Slerp(
                shipVisualRoot.localRotation, target, tiltSmoothSpeed * Time.deltaTime);
        }
    }
}
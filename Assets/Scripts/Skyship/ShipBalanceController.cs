using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Core of the prototype. Reads weight from each CargoZone, computes load and
    /// roll/pitch imbalance, tilts ONLY the ShipVisualRoot child, and exposes
    /// handling modifiers + a load percentage for the movement/failure systems.
    ///
    /// This does NOT use raw Rigidbody tipping — the balance is a custom,
    /// fully tunable zone-weight model.
    ///
    /// SCENE SETUP:
    ///  - Add to the ShipRoot empty GameObject.
    ///  - Assign 'shipVisualRoot' to the ShipVisualRoot child (the deck mesh that tilts).
    ///  - Drag every CargoZone object into the 'zones' list, or leave it empty to
    ///    auto-collect CargoZone children at Start.
    /// </summary>
    public class ShipBalanceController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Child object that visually tilts. NEVER the gameplay/physics root.")]
        public Transform shipVisualRoot;

        [Tooltip("All cargo zones on the ship. If empty, children are auto-collected at Start.")]
        public List<CargoZone> zones = new List<CargoZone>();

        [Header("Capacity & Imbalance Tuning")]
        [Tooltip("Total weight (all zones) considered 100% load.")]
        public float maxCapacity = 200f;
        [Tooltip("Maximum roll torque (weight * distance) that maps to maximum roll angle.")]
        public float maxSideImbalance = 160f;
        [Tooltip("Maximum pitch torque (weight * distance) that maps to maximum pitch angle.")]
        public float maxFrontBackImbalance = 160f;

        [Header("Tilt Tuning")]
        public float maxRollAngle = 20f;
        public float maxPitchAngle = 20f;
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

        private void Start()
        {
            if (zones == null || zones.Count == 0)
                zones = new List<CargoZone>(GetComponentsInChildren<CargoZone>());
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

            if (shipVisualRoot == null) return;

            for (int i = 0; i < zones.Count; i++)
            {
                CargoZone z = zones[i];
                if (z == null) continue;

                float w = z.TotalWeight;
                if (w <= 0f) continue;

                totalWeight += w;

                // Calculate relative position of this zone to the ship center/pivot
                Vector3 relativePos = shipVisualRoot.InverseTransformPoint(z.transform.position);

                // Add to torques (Weight * Distance)
                rollTorque += w * relativePos.x;
                pitchTorque += w * relativePos.z;

                // Bin into directional weights for inspector-friendly diagnostics
                if (relativePos.x < -0.1f) leftWeight += w;
                else if (relativePos.x > 0.1f) rightWeight += w;

                if (relativePos.z > 0.1f) frontWeight += w;
                else if (relativePos.z < -0.1f) rearWeight += w;
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
            float targetRoll = -rollImbalance * maxRollAngle;  // heavy-right dips right
            float targetPitch = pitchImbalance * maxPitchAngle; // heavy-front dips nose down

            Quaternion target = Quaternion.Euler(targetPitch, 0f, targetRoll);
            shipVisualRoot.localRotation = Quaternion.Slerp(
                shipVisualRoot.localRotation, target, tiltSmoothSpeed * Time.deltaTime);
        }
    }
}

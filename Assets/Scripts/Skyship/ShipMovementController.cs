using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Flight model with realistic heavy momentum and inertia.
    /// Moves the ShipRoot forward/backward and turns it slowly, taking time to:
    ///   - Accelerate and build up speed
    ///   - Coast/glide to a stop (decelerate)
    ///   - Build up angular speed to turn and recover from turns (yaw inertia)
    ///
    /// This controller no longer reads input directly. The active pilot is decided by the
    /// steering wheel (ShipHelm) + NetworkManagerP2P, which feeds throttle/steer in via
    /// SetThrottle/SetSteer each frame. It is HOST-AUTHORITATIVE: on clients this component
    /// is disabled (the ship transform is network-synced) so only the host integrates motion.
    /// </summary>
    public class ShipMovementController : MonoBehaviour
    {
        [Header("References")]
        public ShipBalanceController balance;

        [Header("Base Flight Speed")]
        [Tooltip("Maximum forward flight speed at 0% load.")]
        public float baseSpeed = 8f;
        [Tooltip("Maximum turning speed at 0% load (degrees per second).")]
        public float baseTurnSpeed = 25f;

        [Header("Heavy Momentum & Inertia Tuning")]
        [Tooltip("How fast the ship builds forward speed (acceleration). Lower = heavier feel.")]
        public float accelerationRate = 1.0f;
        [Tooltip("How fast the ship slows down when throttle is zero (passive glide drag). Lower = glides longer.")]
        public float decelerationRate = 0.5f;
        [Tooltip("How fast the ship slows down when active reverse throttle is applied (active brake).")]
        public float brakeRate = 2.0f;
        [Tooltip("How fast the ship builds up turning speed (turn inertia). Lower = heavier turning feel.")]
        public float turnAccelerationRate = 1.5f;
        [Tooltip("How fast the ship stops rotating when steering input is released (yaw friction).")]
        public float turnDecelerationRate = 2.5f;

        [Header("Runtime State (read-only)")]
        [Tooltip("Whether the ship currently has an active pilot at the helm.")]
        public bool inputEnabled = false;
        [Tooltip("Current forward speed of the ship (meters per second).")]
        public float currentSpeed = 0f;
        [Tooltip("Current rotation speed of the ship (degrees per second).")]
        public float currentTurnSpeed = 0f;

        private float throttleInput; // -1..1, set externally via SetThrottle
        private float steerInput;    // -1..1, set externally via SetSteer

        private void Awake()
        {
            if (balance == null)
                balance = GetComponent<ShipBalanceController>();
        }

        private void Update()
        {
            // Throttle/steer are pushed in each frame by NetworkManagerP2P (the active pilot's
            // input). We just integrate heavy momentum from whatever the latest values are.
            ApplyHeavyFlightPhysics();
        }

        private void ApplyHeavyFlightPhysics()
        {
            float speedMult = balance != null ? balance.speedMultiplier : 1f;
            float turnPull = balance != null ? balance.turnPull : 0f;

            // ----------------------------------------------------
            // 1. TRANSLATIONAL MOMENTUM (FORWARD / BACKWARD)
            // ----------------------------------------------------
            // Max speed is scaled by weight/load multipliers
            float targetSpeed = throttleInput * baseSpeed * speedMult;

            if (Mathf.Abs(throttleInput) > 0.05f)
            {
                // If applying throttle in the OPPOSITE direction of movement, use brake rate
                bool isBraking = (throttleInput > 0f && currentSpeed < -0.05f) || (throttleInput < 0f && currentSpeed > 0.05f);
                float rate = isBraking ? brakeRate : accelerationRate;

                currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed, rate * Time.deltaTime);
            }
            else
            {
                // Coast/glide passively to a stop
                currentSpeed = Mathf.MoveTowards(currentSpeed, 0f, decelerationRate * Time.deltaTime);
            }

            // Move the ship forward based on current speed
            transform.position += transform.forward * (currentSpeed * Time.deltaTime);

            // ----------------------------------------------------
            // 2. ROTATIONAL MOMENTUM (YAW / TURNING)
            // ----------------------------------------------------
            // Turning is scaled by speed (harder to turn at high speed, and steering pull matches movement speed)
            float speedFactor = Mathf.Clamp01(Mathf.Abs(currentSpeed) / Mathf.Max(0.1f, baseSpeed));
            float targetTurnSpeed = steerInput * baseTurnSpeed * speedMult;

            // Add auto-pull toward the heavy side (only when the ship has forward/backward momentum)
            targetTurnSpeed += turnPull * speedFactor;

            if (Mathf.Abs(steerInput) > 0.05f)
            {
                currentTurnSpeed = Mathf.MoveTowards(currentTurnSpeed, targetTurnSpeed, turnAccelerationRate * Time.deltaTime);
            }
            else
            {
                // Decay rotation speed passively to simulate yaw momentum and friction
                currentTurnSpeed = Mathf.MoveTowards(currentTurnSpeed, 0f, turnDecelerationRate * Time.deltaTime);
            }

            // Rotate the ship around its yaw axis based on current turn speed
            transform.Rotate(Vector3.up, currentTurnSpeed * Time.deltaTime, Space.World);
        }

        // --- Pilot input, pushed in by NetworkManagerP2P each frame (host-authoritative) ---
        public void SetThrottle(float value) => throttleInput = Mathf.Clamp(value, -1f, 1f);
        public void SetSteer(float value) => steerInput = Mathf.Clamp(value, -1f, 1f);
    }
}
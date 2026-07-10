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
    // Anti-jitter execution chain (see ShipRider): ship movers run at negative order so the
    // deck-carry (-50) and the player's own move (0) always see the deck's final pose.
    [DefaultExecutionOrder(-100)]
    public class ShipMovementController : MonoBehaviour
    {
        [Header("References")]
        public ShipBalanceController balance;

        [Header("Base Flight Speed")]
        [Tooltip("Maximum forward flight speed at 0% load.")]
        public float baseSpeed = 6f;
        [Tooltip("Maximum turning speed at 0% load (degrees per second).")]
        public float baseTurnSpeed = 42f;
        [Tooltip("Maximum vertical climb/descend speed at 0% load (meters per second).")]
        public float baseClimbSpeed = 6f;

        [Header("Heavy Momentum & Inertia Tuning")]
        [Tooltip("How fast the ship builds forward speed (acceleration). Lower = heavier feel.")]
        public float accelerationRate = 2.0f;
        [Tooltip("How fast the ship slows down when throttle is zero (passive glide drag). Lower = glides longer.")]
        public float decelerationRate = 0.9f;
        [Tooltip("How fast the ship slows down when active reverse throttle is applied (active brake).")]
        public float brakeRate = 3.0f;
        [Tooltip("How fast the ship builds up turning speed (turn inertia). Lower = heavier turning feel.")]
        public float turnAccelerationRate = 3.0f;
        [Tooltip("How fast the ship stops rotating when steering input is released (yaw friction).")]
        public float turnDecelerationRate = 4.0f;
        [Tooltip("How fast the ship builds vertical climb speed (lift inertia). Lower = heavier feel.")]
        public float climbAccelerationRate = 2.5f;
        [Tooltip("How fast vertical speed bleeds off when lift input is released (hover settle).")]
        public float climbDecelerationRate = 3.5f;

        [Header("Terrain Collision")]
        [Tooltip("Stop the ship from flying through islands/derelicts by sweeping its volume before moving.")]
        public bool collideWithTerrain = true;
        [Tooltip("Layers the ship's hull collides with. Auto-set to the \"Terrain\" layer in Awake if left empty.")]
        public LayerMask collisionMask;
        [Tooltip("Center of the ship's collision box, in ShipRoot-local space. Conforms to the hull/deck " +
                 "(not the tall envelope, which would otherwise hold the ship far off all terrain).")]
        public Vector3 collisionBoxCenter = new Vector3(0f, 0.3f, 0f);
        [Tooltip("Half-extents of the ship's collision box (hull + deck).")]
        public Vector3 collisionBoxHalfExtents = new Vector3(6f, 3.4f, 16f);
        [Tooltip("Small gap kept between the hull and terrain so casts don't get stuck overlapping.")]
        public float collisionSkin = 0.15f;
        [Tooltip("How far per frame the ship is pushed back out of terrain it has penetrated (e.g. after a turn).")]
        public float depenetrationStep = 0.6f;
        [Tooltip("How much momentum is kept (reversed) when the ship hits terrain. 0 = dead stop, 1 = full bounce.")]
        [Range(0f, 1f)] public float bounceFactor = 0.4f;

        [Header("Runtime State (read-only)")]
        [Tooltip("Whether the ship currently has an active pilot at the helm.")]
        public bool inputEnabled = false;
        [Tooltip("Current forward speed of the ship (meters per second).")]
        public float currentSpeed = 0f;
        [Tooltip("Current rotation speed of the ship (degrees per second).")]
        public float currentTurnSpeed = 0f;
        [Tooltip("Current vertical speed of the ship (meters per second, + = climbing).")]
        public float currentClimbSpeed = 0f;

        // Consequence hooks written by ShipStressSystem each frame (host only — this component
        // is disabled on clients). Multiplies max speed/turn/climb; sink drags the ship down.
        [System.NonSerialized] public float externalHandlingMultiplier = 1f;
        [System.NonSerialized] public float externalSinkRate = 0f;

        // Storm hooks written by StormSystem (host only): world-space gust drift (m/s) and a slow
        // yaw shove (deg/s) that push the ship off course until the pilot corrects.
        [System.NonSerialized] public Vector3 externalWindDrift = Vector3.zero;
        [System.NonSerialized] public float externalYawDrift = 0f;

        private float throttleInput; // -1..1, set externally via SetThrottle
        private float steerInput;    // -1..1, set externally via SetSteer
        private float liftInput;     // -1..1, set externally via SetLift (+ = climb)

        private readonly Collider[] overlapBuffer = new Collider[16]; // reused by depenetration

        private void Awake()
        {
            if (balance == null)
                balance = GetComponent<ShipBalanceController>();

            // Default to the "Terrain" layer (islands/derelicts) if nothing is assigned. Falls back to
            // 0 (collision disabled) if the layer doesn't exist, which is safe.
            if (collisionMask.value == 0)
                collisionMask = LayerMask.GetMask("Terrain");
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
            speedMult *= Mathf.Clamp(externalHandlingMultiplier, 0.1f, 1f);

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

            // Move the ship forward based on current speed (stopping at / bouncing off terrain).
            if (!MoveWithCollision(transform.forward * (currentSpeed * Time.deltaTime)))
                currentSpeed = -currentSpeed * bounceFactor; // rebound instead of an instant dead stop

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

            // Rotate the ship around its yaw axis based on current turn speed. Because the hull box is
            // long, yawing near terrain can swing the bow/stern INTO it — ResolveTerrainPenetration()
            // (called at the end of this method) pushes the hull back out so it can't clip through.
            transform.Rotate(Vector3.up, currentTurnSpeed * Time.deltaTime, Space.World);

            // ----------------------------------------------------
            // 3. VERTICAL MOMENTUM (CLIMB / DESCEND)
            // ----------------------------------------------------
            // Always world-up, never affected by visual tilt. Heavy load slows the climb.
            float targetClimbSpeed = liftInput * baseClimbSpeed * speedMult;

            if (Mathf.Abs(liftInput) > 0.05f)
            {
                currentClimbSpeed = Mathf.MoveTowards(currentClimbSpeed, targetClimbSpeed, climbAccelerationRate * Time.deltaTime);
            }
            else
            {
                // Settle to a hover when no lift input is applied
                currentClimbSpeed = Mathf.MoveTowards(currentClimbSpeed, 0f, climbDecelerationRate * Time.deltaTime);
            }

            // Climb/descend (stopping at / bouncing off terrain above/below).
            if (!MoveWithCollision(Vector3.up * (currentClimbSpeed * Time.deltaTime)))
                currentClimbSpeed = -currentClimbSpeed * bounceFactor;

            // Critical overload past its grace timer drags the ship down until weight is shed
            // (ShipStressSystem sets the rate). Ignored when the descent is already blocked.
            if (externalSinkRate > 0.001f)
                MoveWithCollision(Vector3.down * (externalSinkRate * Time.deltaTime));

            // Storm gusts (StormSystem): drift the hull and lean on the rudder.
            if (externalWindDrift.sqrMagnitude > 1e-6f)
                MoveWithCollision(externalWindDrift * Time.deltaTime);
            if (Mathf.Abs(externalYawDrift) > 0.001f)
                transform.Rotate(Vector3.up, externalYawDrift * Time.deltaTime, Space.World);

            // Final safety net: if any motion (especially a yaw) left the hull inside terrain, push it
            // back out. This is what actually stops "turn into a wall and clip through".
            ResolveTerrainPenetration();
        }

        /// <summary>
        /// If the ship's hull box currently overlaps terrain, step it back out along the escape
        /// direction until it's clear (a few sub-steps per frame). Bleeds momentum on contact so the
        /// ship doesn't keep grinding in. Catches penetration from turning, fast moves, or spawning.
        /// </summary>
        private void ResolveTerrainPenetration()
        {
            if (!collideWithTerrain || collisionMask.value == 0) return;

            bool hitTerrain = false;
            for (int step = 0; step < 6; step++)
            {
                Vector3 worldCenter = transform.TransformPoint(collisionBoxCenter);
                int n = Physics.OverlapBoxNonAlloc(worldCenter, collisionBoxHalfExtents, overlapBuffer,
                    transform.rotation, collisionMask, QueryTriggerInteraction.Ignore);
                if (n == 0) break;

                hitTerrain = true;
                Vector3 push = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    Collider col = overlapBuffer[i];
                    Vector3 closest = col.ClosestPoint(worldCenter);
                    Vector3 away = worldCenter - closest;
                    if (away.sqrMagnitude < 1e-4f) away = worldCenter - col.bounds.center; // deep inside / concave fallback
                    if (away.sqrMagnitude > 1e-6f) push += away.normalized;
                }
                if (push.sqrMagnitude < 1e-6f) break;

                transform.position += push.normalized * depenetrationStep;
            }

            if (hitTerrain)
            {
                // Rebound: bleed momentum so we don't immediately drive back into the terrain.
                currentSpeed = -currentSpeed * bounceFactor;
                currentClimbSpeed = -currentClimbSpeed * bounceFactor;
                currentTurnSpeed = -currentTurnSpeed * bounceFactor;
            }
        }


        /// <summary>
        /// Move the ship by <paramref name="delta"/>, but sweep the hull box first and clamp at the
        /// first terrain hit so the ship can't pass through islands/derelicts. Returns false if the
        /// move was blocked. Yaw-only ShipRoot rotation keeps the box axis-aligned to the ship.
        /// </summary>
        private bool MoveWithCollision(Vector3 delta)
        {
            float dist = delta.magnitude;
            if (!collideWithTerrain || collisionMask.value == 0 || dist < 1e-5f)
            {
                transform.position += delta;
                return true;
            }

            Vector3 dir = delta / dist;
            Vector3 worldCenter = transform.TransformPoint(collisionBoxCenter);
            if (Physics.BoxCast(worldCenter, collisionBoxHalfExtents, dir, out RaycastHit hit,
                    transform.rotation, dist + collisionSkin, collisionMask, QueryTriggerInteraction.Ignore))
            {
                float allowed = Mathf.Max(0f, hit.distance - collisionSkin);
                transform.position += dir * allowed;
                return false;
            }

            transform.position += delta;
            return true;
        }

        // --- Pilot input, pushed in by NetworkManagerP2P each frame (host-authoritative) ---
        public void SetThrottle(float value) => throttleInput = Mathf.Clamp(value, -1f, 1f);
        public void SetSteer(float value) => steerInput = Mathf.Clamp(value, -1f, 1f);
        public void SetLift(float value) => liftInput = Mathf.Clamp(value, -1f, 1f);

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            if (!collideWithTerrain) return;
            Gizmos.color = new Color(1f, 0.5f, 0.2f, 0.5f);
            Gizmos.matrix = Matrix4x4.TRS(transform.TransformPoint(collisionBoxCenter), transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, collisionBoxHalfExtents * 2f);
        }
#endif
    }
}
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Turns the prototype's balance numbers into MEANINGFUL, escalating consequences. Reads
    /// ShipBalanceController (which keeps computing tilt/load exactly as before) and derives:
    ///
    ///  TILT:   Stable → Unstable (reduced handling) → Critical (cargo shoved down-slope, heavy
    ///          handling penalty) → Capsize IF critical tilt is SUSTAINED past a grace timer —
    ///          and even then the ship doesn't die: it violently dumps its deck cargo overboard
    ///          and takes hull damage. Tension, not instant punishment.
    ///  WEIGHT: Normal → Heavy (slower) → Overloaded (fuel drain + worse handling) →
    ///          CriticalOverload (ship sinks after a grace timer until weight is shed).
    ///
    /// Players fix every state the physical way: move/dump/redistribute cargo.
    ///
    /// HOST-AUTHORITATIVE: consequences only run on the authority (the ship sim is disabled on
    /// clients anyway); the resulting state enums ride to clients in ExpeditionNetState for HUD/FX.
    /// Added to ShipRoot at runtime by VerticalSliceBootstrap (world scene only).
    /// </summary>
    public class ShipStressSystem : MonoBehaviour
    {
        [Header("Tilt thresholds (fraction of max tilt, from |roll/pitch imbalance|)")]
        public float unstableTilt = 0.5f;
        public float criticalTilt = 0.8f;
        [Tooltip("Seconds of sustained critical tilt before the ship capsize-dumps its cargo.")]
        public float capsizeGraceSeconds = 8f;

        [Header("Weight thresholds (fraction of capacity)")]
        public float heavyLoad = 0.7f;
        public float overloadedLoad = 1.0f;
        public float criticalLoad = 1.3f;
        [Tooltip("Seconds of critical overload before the ship starts losing altitude.")]
        public float overloadGraceSeconds = 8f;

        [Header("Consequences")]
        [Tooltip("Handling multiplier per weight state (Normal/Heavy/Overloaded/Critical).")]
        public float[] weightHandling = { 1f, 0.85f, 0.7f, 0.5f };
        [Tooltip("Extra handling multiplier while tilt is Critical.")]
        public float criticalTiltHandling = 0.6f;
        [Tooltip("Fuel drained per second while Overloaded (doubles at CriticalOverload).")]
        public float overloadFuelDrain = 0.02f;
        [Tooltip("Sink speed (m/s) once the overload grace timer runs out.")]
        public float overloadSinkRate = 1.5f;
        [Tooltip("Down-slope shove applied to deck cargo each second at Critical tilt (m/s).")]
        public float cargoSlideImpulse = 1.2f;
        [Tooltip("Hull damage taken when the ship capsize-dumps its cargo.")]
        public float capsizeHullDamage = 20f;

        [Header("Debug overrides (-1 = off; see ExpeditionDebugTools)")]
        public int forcedTiltState = -1;
        public int forcedWeightState = -1;

        [Header("Runtime (read-only)")]
        public ShipTiltState tiltState = ShipTiltState.Stable;
        public ShipWeightState weightState = ShipWeightState.Normal;
        public float capsizeTimer;
        public float overloadTimer;
        public float handlingMultiplier = 1f;

        private ShipBalanceController balance;
        private ShipMovementController movement;
        private ShipPlatformArea platform;
        private float nextSlideShove;
        private float lastWarnTime;

        private void Awake()
        {
            balance = GetComponent<ShipBalanceController>();
            movement = GetComponent<ShipMovementController>();
            platform = GetComponent<ShipPlatformArea>();
        }

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            var nm = NetworkManagerP2P.Instance;
            bool authority = nm == null || nm.IsWorldAuthority;

            if (!authority)
            {
                // Clients just mirror the synced state for local FX/HUD.
                if (manager != null)
                {
                    tiltState = manager.runtime.tiltState;
                    weightState = manager.runtime.weightState;
                }
                return;
            }

            if (balance == null) return;

            EvaluateTilt();
            EvaluateWeight();
            ApplyConsequences(manager);

            // Publish for the sync blob → client HUDs.
            if (manager != null)
            {
                manager.runtime.tiltState = tiltState;
                manager.runtime.weightState = weightState;
            }
        }

        private void EvaluateTilt()
        {
            float tilt = Mathf.Max(Mathf.Abs(balance.rollImbalance), Mathf.Abs(balance.pitchImbalance));

            ShipTiltState newState =
                tilt >= criticalTilt ? ShipTiltState.Critical :
                tilt >= unstableTilt ? ShipTiltState.Unstable :
                                       ShipTiltState.Stable;
            if (forcedTiltState >= 0) newState = (ShipTiltState)forcedTiltState;

            if (newState == ShipTiltState.Critical)
            {
                capsizeTimer += Time.deltaTime;
                if (capsizeTimer >= capsizeGraceSeconds)
                {
                    CapsizeDumpCargo();
                    capsizeTimer = 0f;
                }
                else if (Time.time - lastWarnTime > 2f)
                {
                    lastWarnTime = Time.time;
                    float left = capsizeGraceSeconds - capsizeTimer;
                    Banner($"CRITICAL TILT — she'll go over in {left:0}s! Shift the cargo!");
                }
            }
            else
            {
                // Recovering eases the timer down instead of resetting: half-fixed isn't fixed.
                capsizeTimer = Mathf.Max(0f, capsizeTimer - Time.deltaTime * 2f);
            }

            if (newState != tiltState)
            {
                Debug.Log($"[ShipStressSystem] Tilt state -> {newState}");
                if (newState == ShipTiltState.Unstable) Banner("The ship groans — load is shifting her trim.");
                tiltState = newState;
            }
        }

        private void EvaluateWeight()
        {
            float load = balance.loadPercent;
            ShipWeightState newState =
                load >= criticalLoad ? ShipWeightState.CriticalOverload :
                load >= overloadedLoad ? ShipWeightState.Overloaded :
                load >= heavyLoad ? ShipWeightState.Heavy :
                                        ShipWeightState.Normal;
            if (forcedWeightState >= 0) newState = (ShipWeightState)forcedWeightState;

            if (newState == ShipWeightState.CriticalOverload)
                overloadTimer += Time.deltaTime;
            else
                overloadTimer = Mathf.Max(0f, overloadTimer - Time.deltaTime * 2f);

            if (newState != weightState)
            {
                Debug.Log($"[ShipStressSystem] Weight state -> {newState}");
                switch (newState)
                {
                    case ShipWeightState.Heavy: Banner("Heavy load — she'll answer the helm slowly."); break;
                    case ShipWeightState.Overloaded: Banner("OVERLOADED — burning extra fuel to stay up!"); break;
                    case ShipWeightState.CriticalOverload: Banner("CRITICAL OVERLOAD — dump cargo or lose altitude!"); break;
                }
                weightState = newState;
            }
        }

        private void ApplyConsequences(ExpeditionManager manager)
        {
            // Handling penalty: weight state x critical tilt x threat pressure.
            float mult = weightHandling[Mathf.Clamp((int)weightState, 0, weightHandling.Length - 1)];
            if (tiltState >= ShipTiltState.Critical) mult *= criticalTiltHandling;
            if (manager != null) mult *= 1f - 0.05f * Mathf.Clamp(manager.runtime.threatLevel, 0, 4);
            handlingMultiplier = mult;

            if (movement != null)
            {
                movement.externalHandlingMultiplier = mult;
                // Past the grace timer, critical overload drags the ship down until weight is shed.
                movement.externalSinkRate = overloadTimer >= overloadGraceSeconds ? overloadSinkRate : 0f;
            }

            // Overload burns campaign fuel (host-owned resource).
            if (manager != null && manager.IsAuthority && weightState >= ShipWeightState.Overloaded)
            {
                float drain = overloadFuelDrain * (weightState == ShipWeightState.CriticalOverload ? 2f : 1f);
                manager.progress.fuel = Mathf.Max(0f, manager.progress.fuel - drain * Time.deltaTime);
            }

            // Critical tilt makes loose deck cargo creep down-slope (a periodic shove; the
            // crates' own physics does the rest).
            if (tiltState >= ShipTiltState.Critical && platform != null && Time.time >= nextSlideShove)
            {
                nextSlideShove = Time.time + 1f;
                Vector3 downSlope = DownSlopeDirection();
                var items = platform.itemsInPlatform;
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null || item.isHeld || item.Body == null || item.Body.isKinematic) continue;
                    item.Body.AddForce(downSlope * cargoSlideImpulse, ForceMode.VelocityChange);
                }
            }
        }

        /// <summary>World-space direction cargo slides at the current tilt (toward the heavy side).</summary>
        private Vector3 DownSlopeDirection()
        {
            Transform vis = balance.shipVisualRoot != null ? balance.shipVisualRoot : transform;
            Vector3 dir = vis.right * Mathf.Sign(balance.rollImbalance) * Mathf.Abs(balance.rollImbalance)
                        + vis.forward * Mathf.Sign(balance.pitchImbalance) * Mathf.Abs(balance.pitchImbalance);
            dir.y = 0f;
            return dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.zero;
        }

        /// <summary>
        /// The capsize consequence: fling all loose deck cargo overboard toward the low side and
        /// take hull damage. The run continues — the punishment is losing the load you mis-stowed.
        /// </summary>
        private void CapsizeDumpCargo()
        {
            Banner("SHE'S GOING OVER — cargo lost overboard!");
            Debug.LogWarning("[ShipStressSystem] Capsize! Dumping deck cargo.");

            var manager = ExpeditionManager.Instance;
            if (manager != null && manager.IsAuthority)
            {
                manager.progress.hullDamage += capsizeHullDamage;
                manager.progress.Save();
            }

            if (platform == null) return;
            Vector3 fling = DownSlopeDirection();
            if (fling == Vector3.zero) fling = transform.right;

            // A whole deck-load going into the clouds is a feast — the leviathan notices.
            LeviathanSystem.NotifyFed(3);

            // Copy: unparenting/shoving mutates the platform list via trigger exits.
            var items = new System.Collections.Generic.List<CargoItem>(platform.itemsInPlatform);
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null || item.isHeld || item.Body == null || item.Body.isKinematic) continue;
                item.transform.SetParent(null);
                item.Body.linearVelocity = fling * 7f + Vector3.up * 2.5f +
                                           Random.insideUnitSphere * 1.5f;
            }
        }

        private void Banner(string msg)
        {
            var manager = ExpeditionManager.Instance;
            if (manager != null) manager.BroadcastEvent(msg);
        }
    }
}

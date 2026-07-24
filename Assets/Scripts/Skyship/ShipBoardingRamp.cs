using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// The deck button that operates the boarding ramp. Press F while looking at it
    /// (PlayerInteraction routes the press here). Host-authoritative via NetworkManagerP2P.
    /// </summary>
    public class ShipRampButton : MonoBehaviour
    {
        public ShipBoardingRamp ramp;

        public void Press()
        {
            if (ramp == null) return;
            var nm = NetworkManagerP2P.Instance;
            if (nm != null) nm.RequestRampToggle();
            else ramp.SetDeployedTarget(!ramp.DeployedTarget);
        }
    }

    /// <summary>
    /// A boarding ramp hinged on the PORT deck edge amidships. The deck button extends it
    /// outboard, then it swings down until it rests on the nearest terrain/object below —
    /// capped at maxDropAngle so it never hangs too steep. While deployed it continuously
    /// RE-SEATS: as the ship drifts, climbs, or tilts, the resting angle is recomputed from
    /// short downward probes along the plank, so the ramp rides the ground contact.
    ///
    /// WALKABLE + WEIGHTED: the plank is a solid collider under the ship's visual root, so
    /// ShipRider carries players standing on it and a carried crate's weight counts at the
    /// carrier's position (way outboard = strong roll torque).
    ///
    /// TILT CONSTRAINT: if ship tilt (e.g. a loaded player walking down the ramp) would push
    /// the grounded ramp through the terrain — i.e. the contact would need the plank to rise
    /// above its upper travel stop — the ramp feeds a roll limit into ShipBalanceController
    /// (externalRollMax), so the ground effectively props the ship up instead of being clipped.
    ///
    /// NETWORKING: only the deployed/stowed TARGET is synced (button presses are
    /// host-authoritative; State packets carry the flag). Each peer animates and seats its own
    /// ramp locally — terrain is deterministic and the ship transform is already synced.
    /// </summary>
    public class ShipBoardingRamp : MonoBehaviour
    {
        private enum RampState { Stowed, Extending, Dropping, Deployed, Raising, Retracting }

        [Header("Geometry")]
        public float rampLength = 6f;
        public float rampWidth = 2.2f;
        public float rampThickness = 0.15f;

        [Header("Motion")]
        [Tooltip("Seconds to slide the plank out before it starts dropping.")]
        public float extendTime = 0.8f;
        [Tooltip("Swing rate while dropping/raising/re-seating (degrees per second).")]
        public float swingSpeed = 45f;
        [Tooltip("Steepest the ramp may hang/rest (degrees below horizontal).")]
        public float maxDropAngle = 38f;
        [Tooltip("Highest the plank may ride above horizontal before the ground must push the SHIP instead.")]
        public float minAngle = -8f;

        [Header("Runtime (read-only)")]
        [SerializeField] private RampState state = RampState.Stowed;
        [SerializeField] private bool deployedTarget;
        [SerializeField] private float currentAngle;

        private Transform plankPivot;
        private ShipBalanceController balance;
        private float extendProgress; // 0 stowed .. 1 extended
        private Vector3 pivotStowedPos, pivotExtendedPos;

        public bool DeployedTarget => deployedTarget;

        /// <summary>
        /// Build the ramp + its control button on the port deck edge, amidships. The edge is
        /// computed from the prefab's UpperDeck_* slabs in visual-root local space (they are
        /// scaled cube primitives parented directly under ShipVisualRoot).
        /// </summary>
        public static ShipBoardingRamp CreateOnShip(ShipBalanceController balanceController)
        {
            if (balanceController == null || balanceController.shipVisualRoot == null) return null;
            Transform visualRoot = balanceController.shipVisualRoot;

            var existing = visualRoot.GetComponentInChildren<ShipBoardingRamp>();
            if (existing != null) return existing;

            float portEdgeX = float.PositiveInfinity;
            float deckTopY = float.NegativeInfinity;
            bool found = false;
            foreach (Transform child in visualRoot)
            {
                if (!child.name.StartsWith("UpperDeck")) continue;
                found = true;
                portEdgeX = Mathf.Min(portEdgeX, child.localPosition.x - child.localScale.x * 0.5f);
                deckTopY = Mathf.Max(deckTopY, child.localPosition.y + child.localScale.y * 0.5f);
            }
            if (!found) { portEdgeX = -3f; deckTopY = 0f; } // graybox fallback

            var go = new GameObject("BoardingRamp");
            go.transform.SetParent(visualRoot, false);
            go.transform.localPosition = new Vector3(portEdgeX + 0.05f, deckTopY, 0f);
            go.transform.localRotation = Quaternion.Euler(0f, -90f, 0f); // local forward = outboard (port)
            go.layer = visualRoot.gameObject.layer;

            var ramp = go.AddComponent<ShipBoardingRamp>();
            ramp.balance = balanceController;
            ramp.Build();

            // Control button on a post just aft of the hinge.
            var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
            post.name = "RampButtonPost";
            post.transform.SetParent(visualRoot, false);
            post.transform.localPosition = new Vector3(portEdgeX + 0.55f, deckTopY + 0.55f, -1.9f);
            post.transform.localScale = new Vector3(0.12f, 1.1f, 0.12f);
            post.GetComponent<Renderer>().sharedMaterial = LeverMat(new Color(0.30f, 0.28f, 0.26f));
            Object.Destroy(post.GetComponent<Collider>());

            var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
            button.name = "RampButton";
            button.transform.SetParent(post.transform, false);
            button.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            button.transform.localScale = new Vector3(1.8f, 0.12f, 1.8f); // post-relative (post is thin)
            button.GetComponent<Renderer>().sharedMaterial = LeverMat(new Color(0.85f, 0.30f, 0.20f));
            var bCol = button.GetComponent<BoxCollider>();
            bCol.isTrigger = true;
            bCol.size = new Vector3(1.6f, 4f, 1.6f); // generous press target
            button.AddComponent<ShipRampButton>().ramp = ramp;

            return ramp;
        }

        private void Build()
        {
            plankPivot = new GameObject("PlankPivot").transform;
            plankPivot.SetParent(transform, false);

            // Stowed: plank tucked back inboard and slightly sunk; Extended: hinge at the deck edge.
            pivotExtendedPos = Vector3.zero;
            pivotStowedPos = new Vector3(0f, -0.45f, -(rampLength - 0.6f));
            plankPivot.localPosition = pivotStowedPos;

            var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plank.name = "Plank";
            plank.transform.SetParent(plankPivot, false);
            plank.transform.localPosition = new Vector3(0f, -rampThickness * 0.5f, rampLength * 0.5f);
            plank.transform.localScale = new Vector3(rampWidth, rampThickness, rampLength);
            plank.GetComponent<Renderer>().sharedMaterial = LeverMat(new Color(0.50f, 0.40f, 0.28f));
            // Ignore Raycast layer: our own seating probes (explicit Default|Terrain mask) never
            // self-hit, while CharacterControllers still collide and ShipRider's ~0 ray still
            // detects it as deck — so players walk and ride on it.
            plank.layer = 2;

            state = RampState.Stowed;
            currentAngle = 0f;
            extendProgress = 0f;
        }

        // ==========================================
        // DEPLOY STATE (host-authoritative flag)
        // ==========================================

        /// <summary>Authority/optimistic: start deploying (true) or stowing (false).</summary>
        public void SetDeployedTarget(bool deployed)
        {
            if (deployedTarget == deployed) return;
            deployedTarget = deployed;
            if (deployed)
            {
                if (state == RampState.Stowed || state == RampState.Retracting || state == RampState.Raising)
                    state = extendProgress >= 1f ? RampState.Dropping : RampState.Extending;
            }
            else
            {
                if (state != RampState.Stowed)
                    state = currentAngle > 0.5f ? RampState.Raising : RampState.Retracting;
            }
        }

        /// <summary>Mirror the host's flag from State packets.</summary>
        public void ApplyRemoteDeployed(bool deployed) => SetDeployedTarget(deployed);

        private void Update()
        {
            switch (state)
            {
                case RampState.Extending:
                    extendProgress = Mathf.MoveTowards(extendProgress, 1f, Time.deltaTime / Mathf.Max(0.05f, extendTime));
                    plankPivot.localPosition = Vector3.Lerp(pivotStowedPos, pivotExtendedPos, extendProgress);
                    if (extendProgress >= 1f) state = RampState.Dropping;
                    break;

                case RampState.Dropping:
                case RampState.Deployed:
                    SeatOnGround();
                    break;

                case RampState.Raising:
                    currentAngle = Mathf.MoveTowards(currentAngle, 0f, swingSpeed * Time.deltaTime);
                    ApplyAngle();
                    ClearTiltConstraint();
                    if (currentAngle <= 0.01f) state = RampState.Retracting;
                    break;

                case RampState.Retracting:
                    extendProgress = Mathf.MoveTowards(extendProgress, 0f, Time.deltaTime / Mathf.Max(0.05f, extendTime));
                    plankPivot.localPosition = Vector3.Lerp(pivotStowedPos, pivotExtendedPos, extendProgress);
                    if (extendProgress <= 0f) state = RampState.Stowed;
                    break;
            }
        }

        private void OnDisable()
        {
            ClearTiltConstraint();
        }

        // ==========================================
        // GROUND SEATING + TILT CONSTRAINT
        // ==========================================

        /// <summary>
        /// Probe downward from points along the plank's swing plane to find the shallowest angle
        /// that touches something, swing toward it, and — if the ground would push the plank past
        /// its upper stop — convert the overshoot into a roll limit on the balance controller.
        /// </summary>
        private void SeatOnGround()
        {
            float rawRest = ComputeRawRestAngle();
            float target = Mathf.Clamp(rawRest, minAngle, maxDropAngle);

            currentAngle = Mathf.MoveTowards(currentAngle, target, swingSpeed * Time.deltaTime);
            ApplyAngle();
            if (state == RampState.Dropping && Mathf.Abs(currentAngle - target) < 0.5f)
                state = RampState.Deployed;

            // Ground pushing past the upper stop: the ship must not roll any further port-down.
            if (rawRest < minAngle && balance != null && balance.shipVisualRoot != null)
            {
                float deficit = minAngle - rawRest; // degrees of roll relief the contact demands
                float currentRoll = Mathf.DeltaAngle(0f, balance.shipVisualRoot.localEulerAngles.z);
                balance.externalRollMax = currentRoll - deficit; // +Z roll = port down (see balance)
            }
            else
            {
                ClearTiltConstraint();
            }
        }

        /// <summary>
        /// The unclamped angle the plank wants: for probe points along its (extended, horizontal)
        /// line, cast down in the hinge frame and take the FIRST contact — min over probes of
        /// asin(depth/distance). No contact in reach = past maxDropAngle (hangs at the stop).
        /// </summary>
        private float ComputeRawRestAngle()
        {
            const int probes = 3;
            float[] fractions = { 0.35f, 0.65f, 1f };
            int mask = (1 << 0); // Default (loose objects, derelict floors...)
            int terrain = LayerMask.NameToLayer("Terrain");
            if (terrain >= 0) mask |= 1 << terrain;

            Vector3 hingePos = plankPivot.position;
            Vector3 outboard = transform.forward; // follows ship tilt
            Vector3 up = transform.up;

            float rest = maxDropAngle + 15f; // "no contact" default: past the stop
            for (int i = 0; i < probes; i++)
            {
                float d = rampLength * fractions[i];
                Vector3 basePoint = hingePos + outboard * d;
                float castStart = 1.2f; // start above the plank line so uphill contacts register
                float maxDepth = d * Mathf.Sin(maxDropAngle * Mathf.Deg2Rad) + 2f;

                if (Physics.Raycast(basePoint + up * castStart, -up, out RaycastHit hit,
                                    castStart + maxDepth, mask, QueryTriggerInteraction.Ignore))
                {
                    float depth = hit.distance - castStart; // below the horizontal plank line (may be negative)
                    float angle = Mathf.Asin(Mathf.Clamp(depth / d, -1f, 1f)) * Mathf.Rad2Deg;
                    rest = Mathf.Min(rest, angle);
                }
            }
            return rest;
        }

        private void ApplyAngle()
        {
            if (plankPivot != null)
                plankPivot.localRotation = Quaternion.Euler(currentAngle, 0f, 0f);
        }

        private void ClearTiltConstraint()
        {
            if (balance != null)
                balance.externalRollMax = float.PositiveInfinity;
        }

        private static Material LeverMat(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }
    }
}

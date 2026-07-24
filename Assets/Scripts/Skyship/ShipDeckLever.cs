using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// Base for the ship's big deck-hinged control levers (engine telegraph, lift lever):
    /// a deck plate + hinge + tall arm + knob, swinging fore/aft through detents.
    ///
    /// INTERACTION (local player): HOLD F while looking at the lever to work it (PlayerInteraction
    /// calls BeginGrab on the press; releasing F lets go). While held, mouse look stays live at
    /// reduced sensitivity (the lever feels heavy); pushing the view toward the ship's BOW —
    /// whichever screen direction that is from where the player stands — shoves the lever forward,
    /// toward the stern pulls it back. The arm leans slightly within its detent until enough
    /// travel accumulates, then CLICKS into the next stage. Subclasses with SpringStage >= 0
    /// snap back to that stage when released (e.g. the lift lever returns to neutral).
    ///
    /// Stage changes route through RequestStage (subclass → NetworkManagerP2P), so all levers are
    /// host-authoritative and mirrored to clients via State packets (ApplyRemoteStage).
    /// </summary>
    public abstract class ShipDeckLever : MonoBehaviour
    {
        [Header("Feel")]
        [Tooltip("Mouse sensitivity multiplier while holding the lever (lower = heavier).")]
        public float grabSensitivityFactor = 0.25f;
        [Tooltip("Accumulated view travel needed to click into the next detent.")]
        public float detentThreshold = 140f;
        [Tooltip("Max visual lean inside a detent before the click (degrees).")]
        public float detentWiggle = 6f;
        [Tooltip("Walking farther than this from the lever releases the grab.")]
        public float maxGrabDistance = 3.5f;

        [Header("Runtime (read-only)")]
        [SerializeField] protected int stage;

        protected Transform armPivot;
        protected Material knobMat;

        // Local grab state (protected so analog subclasses can drive their own feel).
        private bool grabbed;
        private GameObject grabber;
        private FirstPersonController grabberController;
        private Transform grabberCamera;
        private float savedSensitivity;
        protected float accum;

        public int Stage => stage;
        public bool IsGrabbed => grabbed;

        /// <summary>Arm angle per stage (X euler; forward lean = higher stage).</summary>
        protected abstract float[] Detents { get; }
        /// <summary>Knob tint per stage.</summary>
        protected abstract Color[] StageColors { get; }
        /// <summary>Stage to snap back to when released, or -1 to hold position (telegraph).</summary>
        protected virtual int SpringStage => -1;
        /// <summary>Route a local click to the authority (subclasses call NetworkManagerP2P).</summary>
        protected abstract void RequestStage(int newStage);

        // ==========================================
        // CONSTRUCTION (graybox, shared by subclasses)
        // ==========================================

        protected void Initialize(int startStage, float armLength, float knobSize)
        {
            stage = Mathf.Clamp(startStage, 0, Detents.Length - 1);

            // Deck plate — the hinge is mounted straight onto the deck.
            var plate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            plate.name = "DeckPlate";
            Destroy(plate.GetComponent<Collider>());
            plate.transform.SetParent(transform, false);
            plate.transform.localPosition = new Vector3(0f, 0.025f, 0f);
            plate.transform.localScale = new Vector3(0.45f, 0.05f, 0.9f);
            plate.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.25f, 0.22f, 0.20f));

            var hinge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hinge.name = "Hinge";
            Destroy(hinge.GetComponent<Collider>());
            hinge.transform.SetParent(transform, false);
            hinge.transform.localPosition = new Vector3(0f, 0.12f, 0f);
            hinge.transform.localScale = new Vector3(0.28f, 0.18f, 0.2f);
            hinge.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.30f, 0.28f, 0.26f));

            armPivot = new GameObject("ArmPivot").transform;
            armPivot.SetParent(transform, false);
            armPivot.localPosition = new Vector3(0f, 0.08f, 0f);

            var arm = GameObject.CreatePrimitive(PrimitiveType.Cube);
            arm.name = "Arm";
            Destroy(arm.GetComponent<Collider>());
            arm.transform.SetParent(armPivot, false);
            arm.transform.localPosition = new Vector3(0f, armLength * 0.5f, 0f);
            arm.transform.localScale = new Vector3(0.09f, armLength, 0.09f);
            arm.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.55f, 0.45f, 0.30f));

            var knob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            knob.name = "Knob";
            Destroy(knob.GetComponent<Collider>());
            knob.transform.SetParent(armPivot, false);
            knob.transform.localPosition = new Vector3(0f, armLength + knobSize * 0.15f, 0f);
            knob.transform.localScale = Vector3.one * knobSize;
            knobMat = MakeMat(StageColors[stage]);
            knob.GetComponent<Renderer>().sharedMaterial = knobMat;

            // Grab volume for the interaction ray (trigger: never blocks walking or cargo).
            // Deep along Z to cover the arm's full fore/aft swing.
            var grabVolume = gameObject.AddComponent<BoxCollider>();
            grabVolume.isTrigger = true;
            grabVolume.center = new Vector3(0f, armLength * 0.55f, 0.15f);
            grabVolume.size = new Vector3(0.6f, armLength * 1.25f, armLength * 1.4f);

            UpdateVisual(0f);
        }

        // ==========================================
        // HOLD-F GRAB (local player only)
        // ==========================================

        /// <summary>Called by PlayerInteraction on the F press; the grab lasts while F stays held.</summary>
        public void BeginGrab(GameObject player)
        {
            if (grabbed || player == null) return;

            grabberController = player.GetComponent<FirstPersonController>();
            if (grabberController == null) return;

            grabber = player;
            grabberCamera = grabberController.cameraTransform != null
                ? grabberController.cameraTransform
                : (Camera.main != null ? Camera.main.transform : null);
            savedSensitivity = grabberController.lookSensitivity;
            grabberController.lookSensitivity = savedSensitivity * grabSensitivityFactor;
            accum = 0f;
            grabbed = true;
        }

        private void Release()
        {
            if (grabberController != null)
                grabberController.lookSensitivity = savedSensitivity;
            grabber = null;
            grabberController = null;
            grabberCamera = null;
            grabbed = false;
            accum = 0f;

            if (SpringStage >= 0 && stage != SpringStage)
                RequestStage(SpringStage); // spring-loaded levers snap home when let go
            UpdateVisual(0f);
            OnReleased();
        }

        /// <summary>Hook for subclasses (e.g. the analog throttle pushes its final value).</summary>
        protected virtual void OnReleased() { }

        protected virtual void Update()
        {
            if (!grabbed) return;

            // Safety releases: player gone, controller suspended (chart view/helm), or walked away.
            if (grabber == null || grabberController == null || !grabberController.enabled
                || Vector3.Distance(grabber.transform.position, transform.position) > maxGrabDistance)
            {
                Release();
                return;
            }

            // HOLD to operate: letting go of F drops the lever.
            var k = Keyboard.current;
            if (k == null || !k.fKey.isPressed)
            {
                Release();
                return;
            }

            // Which way is "toward the bow" on this player's screen right now? Project the
            // lever's forward axis into camera space: moving the view along that direction
            // shoves the lever forward, against it pulls back. Works standing on either side.
            float travel = 0f;
            var m = Mouse.current;
            if (m != null && grabberCamera != null)
            {
                Vector3 bow = transform.forward;
                Vector2 screenBow = new Vector2(
                    Vector3.Dot(bow, grabberCamera.right),
                    Vector3.Dot(bow, grabberCamera.up));

                if (screenBow.sqrMagnitude > 0.04f) // ambiguous while sighting straight down the axis
                {
                    screenBow.Normalize();
                    travel = Vector2.Dot(m.delta.ReadValue(), screenBow);
                }
            }
            OnGrabTravel(travel);
        }

        /// <summary>
        /// Integrate this frame's view travel (screen px toward the bow). Default: the classic
        /// detent lever — accumulate and CLICK a stage per threshold. The analog throttle
        /// overrides this with continuous behavior.
        /// </summary>
        protected virtual void OnGrabTravel(float travel)
        {
            accum += travel;

            if (accum > detentThreshold && stage < Detents.Length - 1)
            {
                RequestStage(stage + 1);
                accum = 0f;
            }
            else if (accum < -detentThreshold && stage > 0)
            {
                RequestStage(stage - 1);
                accum = 0f;
            }
            // At the end stops the lever just leans against the stop.
            accum = Mathf.Clamp(accum, -detentThreshold, detentThreshold);

            UpdateVisual(Mathf.Clamp(accum / detentThreshold, -1f, 1f) * detentWiggle);
        }

        // ==========================================
        // STAGE STATE (applied by NetworkManagerP2P)
        // ==========================================

        /// <summary>Snap to a stage (authority decision or local/optimistic click).</summary>
        public void ApplyStage(int newStage)
        {
            stage = Mathf.Clamp(newStage, 0, Detents.Length - 1);
            if (knobMat != null)
            {
                knobMat.color = StageColors[stage];
                if (knobMat.HasProperty("_BaseColor")) knobMat.SetColor("_BaseColor", StageColors[stage]);
            }
            if (!grabbed) UpdateVisual(0f);
        }

        /// <summary>Mirror the host's stage from State packets. Ignored while the local player is
        /// holding the lever (their optimistic position wins visually; the host stays authoritative).</summary>
        public void ApplyRemoteStage(int remoteStage)
        {
            if (grabbed || remoteStage == stage) return;
            ApplyStage(remoteStage);
        }

        protected virtual void UpdateVisual(float wiggleDegrees)
        {
            SetArmAngle(Detents[stage] + wiggleDegrees);
        }

        /// <summary>Pose the arm at the given fore/aft angle (degrees; forward lean = positive).</summary>
        protected void SetArmAngle(float degrees)
        {
            if (armPivot == null) return;
            armPivot.localRotation = Quaternion.Euler(degrees, 0f, 0f);
        }

        protected static Material MakeMat(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }
    }
}

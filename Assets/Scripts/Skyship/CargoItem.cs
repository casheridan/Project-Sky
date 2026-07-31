using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Broad loot/resource type. Islands yield raw resources (plus occasional fuel/treasure);
    /// derelict ships yield repair cargo, special cargo, fuel, and treasure. Back at base these
    /// get forged into usable cargo, fuel, and upgrades (economy is future work).
    /// </summary>
    public enum CargoCategory
    {
        Generic,
        RawResource,
        Fuel,
        Treasure,
        RepairCargo,
        SpecialCargo,
        // Node-harvested raw resources (see ResourceNode). Appended so existing serialized values keep their meaning.
        Stone,
        Ore,
        Crystal
    }

    /// <summary>
    /// An interactable salvage item the player can carry and drop. Physics are ON
    /// while loose (including gravity, sliding, and rolling). Its weight is
    /// dynamically integrated into the ship's balance based on its exact local
    /// coordinates on the deck.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CargoItem : MonoBehaviour
    {
        [Header("Identity")]
        public string itemName = "Crate";

        [Tooltip("Loot/resource category. Drives where it spawns and what it forges into.")]
        public CargoCategory category = CargoCategory.Generic;

        [Tooltip("Weight in arbitrary units. Drives ship balance & load.")]
        public float weight = 10f;

        [Tooltip("Salvage value. Converted to money/scrap/fuel by the return-to-port tally.")]
        public float value = 50f;

        [Header("Expedition (stamped from a CargoDefinition; empty for legacy prototype cubes)")]
        [Tooltip("CargoDatabase id this item was spawned from. Objective tracking matches on this.")]
        public string definitionId = "";
        [Tooltip("Mission-critical cargo (e.g. the Black Navigation Box).")]
        public bool isObjectiveItem;
        [Tooltip("Eldritch corruption carried aboard — feeds the threat director.")]
        public float corruptionValue;
        [Tooltip("Extra ship strain beyond raw weight (future stability sim).")]
        public float stabilityImpact;

        [Header("Deck Contact")]
        [Tooltip("Apply a category-flavoured grip/impact profile at Start. Disable to tune this item by hand.")]
        public bool useCategorySurfaceDefaults = true;
        [Tooltip("Multiplies deck traction. Below 1 slides more; above 1 grips more.")]
        [Range(0.1f, 2f)] public float deckGripMultiplier = 1f;
        [Tooltip("Multiplies collision damage transferred into breakable barriers.")]
        [Range(0.1f, 2f)] public float barrierImpactMultiplier = 1f;
        [Tooltip("Unity Rigidbody mass per gameplay weight unit. Gameplay balance still uses 'weight' directly.")]
        [Min(0.005f)] public float physicsMassPerWeight = 0.05f;

        [Header("Runtime state (read-only)")]
        public bool isHeld;

        private Rigidbody rb;
        private Collider col;
        private readonly HashSet<CargoItem> touchingCargo = new HashSet<CargoItem>();

        public Rigidbody Body => rb;
        public Collider CollisionShape => col;
        internal HashSet<CargoItem> TouchingCargo => touchingCargo;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            col = GetComponent<Collider>();
        }

        private void Start()
        {
            if (useCategorySurfaceDefaults)
                ApplyCategorySurfaceDefaults();
            RefreshPhysicalProperties();
        }

        /// <summary>
        /// Keep collision momentum proportional to the gameplay weight. The conversion stays
        /// deliberately small so the existing cargo physics remains stable while heavy objects
        /// transfer meaningfully larger impulses through cargo chains and into railings.
        /// </summary>
        public void RefreshPhysicalProperties()
        {
            if (rb == null) rb = GetComponent<Rigidbody>();
            if (rb == null) return;

            rb.mass = Mathf.Max(0.1f, weight * Mathf.Max(0.005f, physicsMassPerWeight));
            rb.collisionDetectionMode = rb.isKinematic
                ? CollisionDetectionMode.ContinuousSpeculative
                : CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = isHeld
                ? RigidbodyInterpolation.None
                : RigidbodyInterpolation.Interpolate;
            if (isHeld)
            {
                rb.useGravity = false;
                rb.detectCollisions = false;
            }
        }

        private void ApplyCategorySurfaceDefaults()
        {
            switch (category)
            {
                case CargoCategory.Crystal:
                    deckGripMultiplier = 0.65f;
                    barrierImpactMultiplier = 1.15f;
                    break;
                case CargoCategory.Treasure:
                case CargoCategory.SpecialCargo:
                    deckGripMultiplier = 0.75f;
                    barrierImpactMultiplier = 1.1f;
                    break;
                case CargoCategory.Fuel:
                    deckGripMultiplier = 0.85f;
                    barrierImpactMultiplier = 0.9f;
                    break;
                case CargoCategory.Stone:
                case CargoCategory.RepairCargo:
                    deckGripMultiplier = 1.15f;
                    barrierImpactMultiplier = 1f;
                    break;
                case CargoCategory.Ore:
                    deckGripMultiplier = 0.95f;
                    barrierImpactMultiplier = 1.2f;
                    break;
                default:
                    deckGripMultiplier = 1f;
                    barrierImpactMultiplier = 1f;
                    break;
            }
        }

        /// <summary>Called by PlayerInteraction when the item is picked up.</summary>
        public void OnPickedUp(Transform holdParent)
        {
            if (holdParent == null)
            {
                Debug.LogWarning($"[CargoItem] Cannot pick up '{itemName}' without a hold point.");
                return;
            }

            isHeld = true;

            // Kinematic + collider off lets the transform follow the camera without physics
            // interpolation writing the previous loose-cargo pose back over it.
            ApplyPhysics(dynamic: false, colliderEnabled: false);
            FollowHoldPoint(holdParent);
        }

        /// <summary>
        /// Snap held cargo after player/camera movement. PlayerInteraction calls this in
        /// LateUpdate so CharacterController motion and Rigidbody interpolation cannot leave
        /// the rendered crate behind.
        /// </summary>
        public void FollowHoldPoint(Transform holdParent)
        {
            FollowHoldPoint(holdParent, Vector3.zero, Quaternion.identity);
        }

        /// <summary>Variant used by remote-player puppets whose carry socket is an offset.</summary>
        public void FollowHoldPoint(Transform holdParent, Vector3 localPosition, Quaternion localRotation)
        {
            if (!isHeld || holdParent == null) return;

            if (transform.parent != holdParent)
                transform.SetParent(holdParent, false);
            transform.SetLocalPositionAndRotation(localPosition, localRotation);

            // Keep the kinematic physics pose in lockstep with the rendered transform. Its
            // collider is disabled while carried, but this prevents a stale pose on drop.
            if (rb != null)
            {
                rb.position = transform.position;
                rb.rotation = transform.rotation;
            }
        }

        /// <summary>Called by PlayerInteraction when the item is dropped into the world.</summary>
        public void OnDropped()
        {
            OnDropped(Vector3.zero);
        }

        /// <summary>
        /// Release the item with the carrier's world velocity plus any deliberate drop speed.
        /// This prevents a carried kinematic body from hanging momentarily at zero velocity.
        /// </summary>
        public void OnDropped(Vector3 releaseVelocity)
        {
            isHeld = false;
            transform.SetParent(null);
            ApplyPhysics(dynamic: true, colliderEnabled: true);
            if (rb != null)
            {
                rb.linearVelocity = releaseVelocity;
                rb.WakeUp();
            }
        }

        private void ApplyPhysics(bool dynamic, bool colliderEnabled)
        {
            if (rb != null)
            {
                if (!dynamic)
                {
                    // Only set velocity if it was not already kinematic, avoiding warnings.
                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }

                    // ContinuousDynamic is invalid for a kinematic carried body, and interpolation
                    // can render its old physics pose after its parent/player already moved.
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                    rb.interpolation = RigidbodyInterpolation.None;
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    rb.detectCollisions = false;
                }
                else
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                    rb.detectCollisions = true;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    rb.WakeUp();
                }
            }
            if (col != null)
                col.enabled = colliderEnabled;
        }

        private void OnCollisionEnter(Collision collision) => RegisterCargoContact(collision, true);
        private void OnCollisionStay(Collision collision) => RegisterCargoContact(collision, true);
        private void OnCollisionExit(Collision collision) => RegisterCargoContact(collision, false);

        private void RegisterCargoContact(Collision collision, bool touching)
        {
            if (collision == null || collision.collider == null) return;
            CargoItem other = collision.collider.GetComponentInParent<CargoItem>();
            if (other == null || other == this) return;

            if (touching) touchingCargo.Add(other);
            else touchingCargo.Remove(other);
        }

        private void OnDisable()
        {
            touchingCargo.Clear();
        }
    }
}

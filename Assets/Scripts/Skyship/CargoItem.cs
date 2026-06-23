using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// An interactable salvage item the player can carry, drop, and secure into a
    /// CargoZone. Physics are ON while loose and OFF while held/secured.
    ///
    /// SCENE SETUP:
    ///  - Create a Cube primitive (it already has a BoxCollider).
    ///  - Add this component (a Rigidbody is added automatically if missing).
    ///  - Set itemName / weight / value.
    ///  - Turn it into a prefab and scatter several around the level with
    ///    different weights so the balance system has something to react to.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CargoItem : MonoBehaviour
    {
        [Header("Identity")]
        public string itemName = "Crate";

        [Tooltip("Weight in arbitrary units. Drives ship balance & load.")]
        public float weight = 10f;

        [Tooltip("Salvage value. Not used by the balance sim yet; here for future scoring.")]
        public float value = 50f;

        [Header("Runtime state (read-only)")]
        public bool isHeld;
        public bool isSecured;
        [Tooltip("The zone this item is currently secured in (null if loose or held).")]
        public CargoZone currentZone;

        private Rigidbody rb;
        private Collider col;

        public Rigidbody Body => rb;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            col = GetComponent<Collider>();
        }

        /// <summary>Called by PlayerInteraction when the item is picked up.</summary>
        public void OnPickedUp(Transform holdParent)
        {
            // If it was secured, leave its zone first (this subtracts its weight).
            if (isSecured)
                RemoveFromZone();

            isHeld = true;

            // Kinematic + collider off so it follows the hold point and doesn't
            // shove the player or other physics objects while carried.
            ApplyPhysics(dynamic: false, colliderEnabled: false);

            if (holdParent != null)
            {
                transform.SetParent(holdParent);
                transform.localPosition = Vector3.zero;
                transform.localRotation = Quaternion.identity;
            }
        }

        /// <summary>Called by PlayerInteraction when the item is dropped into the world.</summary>
        public void OnDropped()
        {
            isHeld = false;
            transform.SetParent(null);
            ApplyPhysics(dynamic: true, colliderEnabled: true);
        }

        /// <summary>Secure this (currently held) item into the given zone.</summary>
        public void SecureToZone(CargoZone zone)
        {
            if (zone == null) return;

            isHeld = false;
            isSecured = true;
            currentZone = zone;

            // Kinematic so it rides with the deck, collider ON so the player can
            // look at it again to pick it back up.
            ApplyPhysics(dynamic: false, colliderEnabled: true);

            transform.SetParent(zone.GetParentForItems());
            transform.position = zone.GetPlacePosition();
            transform.rotation = Quaternion.identity;

            zone.AddItem(this);
        }

        /// <summary>Detach from the current zone (subtracts weight). Leaves the item loose state-wise.</summary>
        public void RemoveFromZone()
        {
            if (currentZone != null)
                currentZone.RemoveItem(this);

            currentZone = null;
            isSecured = false;
        }

        private void ApplyPhysics(bool dynamic, bool colliderEnabled)
        {
            if (rb != null)
            {
                if (!dynamic)
                {
                    // Only set velocity if it was not already kinematic, avoiding warnings
                    if (!rb.isKinematic)
                    {
                        rb.linearVelocity = Vector3.zero;
                        rb.angularVelocity = Vector3.zero;
                    }
                    rb.isKinematic = true;
                    rb.useGravity = false;
                }
                else
                {
                    rb.isKinematic = false;
                    rb.useGravity = true;
                }
            }
            if (col != null)
                col.enabled = colliderEnabled;
        }
    }
}

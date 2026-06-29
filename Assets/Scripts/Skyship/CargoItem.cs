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
        SpecialCargo
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

        [Tooltip("Salvage value. Not used by the balance sim yet.")]
        public float value = 50f;

        [Header("Runtime state (read-only)")]
        public bool isHeld;

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
            isHeld = true;

            // Kinematic + collider off so it follows the hold point nicely
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

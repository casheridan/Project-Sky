using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// An interactable salvage item the player can carry and drop. Physics are ON
    /// while loose (including gravity, sliding, and rolling). When dropped inside
    /// any CargoZone trigger, it contributes its weight dynamically to the zone.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class CargoItem : MonoBehaviour
    {
        [Header("Identity")]
        public string itemName = "Crate";

        [Tooltip("Weight in arbitrary units. Drives ship balance & load.")]
        public float weight = 10f;

        [Tooltip("Salvage value. Not used by the balance sim yet.")]
        public float value = 50f;

        [Header("Runtime state (read-only)")]
        public bool isHeld;
        public bool isSecured;
        [Tooltip("The zone this item is currently resting inside (null if loose or held outside zones).")]
        public CargoZone currentZone;

        [Header("Physics Overlaps")]
        [Tooltip("List of zones this item is physically overlapping with.")]
        public List<CargoZone> overlappingZones = new List<CargoZone>();

        private Rigidbody rb;
        private Collider col;

        public Rigidbody Body => rb;

        private void Awake()
        {
            rb = GetComponent<Rigidbody>();
            col = GetComponent<Collider>();
        }

        private void Update()
        {
            // Continuously evaluate the closest zone as we slide, roll, or move
            if (overlappingZones.Count > 0 && !isHeld)
            {
                UpdateCurrentZone();
            }
        }

        /// <summary>Called by PlayerInteraction when the item is picked up.</summary>
        public void OnPickedUp(Transform holdParent)
        {
            if (isSecured || currentZone != null)
                RemoveFromZone();

            overlappingZones.Clear();
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

        /// <summary>Register a zone this cargo item is physically overlapping with.</summary>
        public void RegisterOverlappingZone(CargoZone zone)
        {
            if (zone == null) return;
            if (!overlappingZones.Contains(zone))
            {
                overlappingZones.Add(zone);
                UpdateCurrentZone();
            }
        }

        /// <summary>Unregister a zone this cargo item has left physically.</summary>
        public void UnregisterOverlappingZone(CargoZone zone)
        {
            if (zone == null) return;
            if (overlappingZones.Remove(zone))
            {
                UpdateCurrentZone();
            }
        }

        private void UpdateCurrentZone()
        {
            CargoZone bestZone = null;
            float minDst = float.MaxValue;

            for (int i = 0; i < overlappingZones.Count; i++)
            {
                CargoZone zone = overlappingZones[i];
                if (zone == null) continue;

                float dst = Vector3.Distance(transform.position, zone.transform.position);
                if (dst < minDst)
                {
                    minDst = dst;
                    bestZone = zone;
                }
            }

            if (currentZone != bestZone)
            {
                if (currentZone != null)
                {
                    currentZone.RemoveItem(this);
                }

                currentZone = bestZone;
                isSecured = (currentZone != null);

                if (currentZone != null)
                {
                    currentZone.AddItem(this);
                }
            }
        }

        /// <summary>Detach from the current zone (subtracts weight).</summary>
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

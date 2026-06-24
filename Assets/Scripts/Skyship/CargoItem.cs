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
            // Continuously evaluate and distribute our weight across overlapping zones
            if (!isHeld)
            {
                UpdateWeightDistribution();
            }
        }

        /// <summary>Called by PlayerInteraction when the item is picked up.</summary>
        public void OnPickedUp(Transform holdParent)
        {
            RemoveFromAllZones();

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
                UpdateWeightDistribution();
            }
        }

        /// <summary>Unregister a zone this cargo item has left physically.</summary>
        public void UnregisterOverlappingZone(CargoZone zone)
        {
            if (zone == null) return;
            if (overlappingZones.Remove(zone))
            {
                // Remove contribution to this zone completely
                zone.SetItemWeightContribution(this, 0f);
                UpdateWeightDistribution();
            }
        }

        [Header("Interpolation Settings")]
        [Tooltip("The max distance from a zone's center to still contribute weight to it.")]
        public float maxInterpolationDistance = 2.0f;

        // Keep track of which zones we currently contribute weight to
        private List<CargoZone> contributingZones = new List<CargoZone>();

        private void UpdateWeightDistribution()
        {
            if (overlappingZones.Count == 0)
            {
                RemoveFromAllZones();
                currentZone = null;
                isSecured = false;
                return;
            }

            // Calculate raw weights and find the closest zone
            float totalRawFactor = 0f;
            float[] rawFactors = new float[overlappingZones.Count];
            float minDst = float.MaxValue;
            CargoZone bestZone = null;

            for (int i = 0; i < overlappingZones.Count; i++)
            {
                CargoZone zone = overlappingZones[i];
                if (zone == null) continue;

                float dst = Vector3.Distance(transform.position, zone.transform.position);
                
                // Track closest zone for general 'currentZone' reference
                if (dst < minDst)
                {
                    minDst = dst;
                    bestZone = zone;
                }

                // Linear falloff: max at center, 0 at maxInterpolationDistance
                float rawFactor = Mathf.Max(0f, maxInterpolationDistance - dst);
                rawFactors[i] = rawFactor;
                totalRawFactor += rawFactor;
            }

            // Update general state
            currentZone = bestZone;
            isSecured = (currentZone != null);

            // Distribute weight
            List<CargoZone> activeZonesThisFrame = new List<CargoZone>();

            for (int i = 0; i < overlappingZones.Count; i++)
            {
                CargoZone zone = overlappingZones[i];
                if (zone == null) continue;

                // Normalize factor
                float factor = totalRawFactor > 0.001f ? rawFactors[i] / totalRawFactor : 0f;
                float contributedWeight = factor * weight;

                if (contributedWeight > 0.001f)
                {
                    zone.SetItemWeightContribution(this, contributedWeight);
                    activeZonesThisFrame.Add(zone);
                    if (!contributingZones.Contains(zone))
                    {
                        contributingZones.Add(zone);
                    }
                }
                else
                {
                    zone.SetItemWeightContribution(this, 0f);
                    contributingZones.Remove(zone);
                }
            }

            // Clean up any zones we are no longer contributing to (e.g. if we went outside maxInterpolationDistance)
            for (int i = contributingZones.Count - 1; i >= 0; i--)
            {
                CargoZone zone = contributingZones[i];
                if (!activeZonesThisFrame.Contains(zone))
                {
                    if (zone != null)
                    {
                        zone.SetItemWeightContribution(this, 0f);
                    }
                    contributingZones.RemoveAt(i);
                }
            }
        }

        /// <summary>Detach completely from all zones.</summary>
        public void RemoveFromAllZones()
        {
            for (int i = contributingZones.Count - 1; i >= 0; i--)
            {
                CargoZone zone = contributingZones[i];
                if (zone != null)
                {
                    zone.SetItemWeightContribution(this, 0f);
                }
            }
            contributingZones.Clear();
        }

        /// <summary>Legacy method kept for backwards compatibility (no-op now since Update handles it).</summary>
        public void RemoveFromZone()
        {
            RemoveFromAllZones();
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

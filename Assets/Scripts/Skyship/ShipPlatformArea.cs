using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Handles dynamic parenting of cargo items when they are dropped on the ship,
    /// and ensures they inherit the ship's momentum if they slide or fall off.
    ///
    /// SCENE SETUP:
    ///  - Add this component to the ShipRoot GameObject.
    ///  - Assign 'rideParent' to the ShipVisualRoot child (where objects should parent
    ///    so they move and tilt with the ship).
    ///  - Add a BoxCollider trigger to this GameObject (or a child) that represents
    ///    the ship's "cargo deck air volume" (e.g. Center Y = 2, Size = 10 x 4 x 8)
    ///    to detect any items on the deck.
    /// </summary>
    public class ShipPlatformArea : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("The transform that cargo items parent to so they move and tilt with the ship (usually ShipVisualRoot).")]
        public Transform rideParent;

        [Header("Runtime State (read-only)")]
        [SerializeField] private Vector3 currentShipVelocity;
        [SerializeField] public List<CargoItem> itemsInPlatform = new List<CargoItem>();

        private Vector3 lastPosition;

        public Vector3 CurrentShipVelocity => currentShipVelocity;

        private void Start()
        {
            lastPosition = transform.position;

            // If rideParent is not assigned, look for ShipVisualRoot or default to ourselves
            if (rideParent == null)
            {
                var balance = GetComponent<ShipBalanceController>();
                if (balance != null)
                    rideParent = balance.shipVisualRoot;
                else
                    rideParent = transform;
            }
        }

        private void FixedUpdate()
        {
            // Calculate actual ship velocity vector in world space
            if (Time.fixedDeltaTime > 0.0001f)
            {
                currentShipVelocity = (transform.position - lastPosition) / Time.fixedDeltaTime;
            }
            lastPosition = transform.position;

            // Maintain parenting of non-held cargo items
            for (int i = itemsInPlatform.Count - 1; i >= 0; i--)
            {
                CargoItem item = itemsInPlatform[i];
                if (item == null)
                {
                    itemsInPlatform.RemoveAt(i);
                    continue;
                }

                // If the item is NOT held, ensure it is parented to the rideParent
                if (!item.isHeld)
                {
                    if (item.transform.parent != rideParent)
                    {
                        item.transform.SetParent(rideParent);
                    }
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            CargoItem item = other.GetComponentInParent<CargoItem>();
            if (item != null && !itemsInPlatform.Contains(item))
            {
                itemsInPlatform.Add(item);
                Debug.Log($"[ShipPlatformArea] Cargo '{item.itemName}' entered ship platform.");
            }
        }

        private void OnTriggerExit(Collider other)
        {
            CargoItem item = other.GetComponentInParent<CargoItem>();
            if (item != null)
            {
                if (itemsInPlatform.Remove(item))
                {
                    Debug.Log($"[ShipPlatformArea] Cargo '{item.itemName}' exited ship platform. Unparenting.");

                    // If it was parented to our ship structure, restore it to global space
                    if (item.transform.parent == rideParent || item.transform.parent == transform)
                    {
                        item.transform.SetParent(null);

                        // Inherit the ship's current linear velocity so it keeps its momentum
                        Rigidbody rb = item.GetComponent<Rigidbody>();
                        if (rb != null)
                        {
                            rb.linearVelocity = currentShipVelocity;
                        }
                    }
                }
            }
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            // Draw a green wire cube representation of our trigger area if a box collider exists
            BoxCollider col = GetComponent<BoxCollider>();
            if (col != null && col.isTrigger)
            {
                Gizmos.color = new Color(0f, 1f, 0f, 0.2f);
                Gizmos.matrix = transform.localToWorldMatrix;
                Gizmos.DrawWireCube(col.center, col.size);
            }
        }
#endif
    }
}

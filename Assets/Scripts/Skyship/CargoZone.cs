using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Identifies which balance area of the ship a zone represents.
    /// ShipBalanceController sums secured cargo weight per quadrant using this.
    /// </summary>
    public enum ZoneType
    {
        FrontLeft,
        FrontRight,
        RearLeft,
        RearRight,
        Center
    }

    /// <summary>
    /// A single cargo placement area on the deck. Tracks the total weight of any
    /// cargo currently sitting physically inside its trigger bounds.
    ///
    /// SCENE SETUP:
    ///  - Create a Cube on the deck (e.g. Scale 2 x 0.2 x 2) for each zone.
    ///  - Add this component and pick the matching ZoneType.
    ///  - The script automatically configures a trigger zone that extends upwards
    ///    so you can place items anywhere inside its bounds.
    /// </summary>
    public class CargoZone : MonoBehaviour
    {
        [Tooltip("Which balance quadrant this zone feeds into.")]
        public ZoneType zoneType = ZoneType.Center;

        [Header("Runtime (read-only)")]
        [SerializeField] private float totalWeight;
        [SerializeField] private List<CargoItem> securedItems = new List<CargoItem>();

        /// <summary>Total weight of all cargo currently inside this zone.</summary>
        public float TotalWeight => totalWeight;
        public IReadOnlyList<CargoItem> SecuredItems => securedItems;

        private void Awake()
        {
            // Setup physical bounds: Keep the main BoxCollider solid so player & crates stand on it
            BoxCollider solidCol = GetComponent<BoxCollider>();
            if (solidCol == null)
            {
                solidCol = gameObject.AddComponent<BoxCollider>();
            }
            solidCol.isTrigger = false;

            // Dynamically add a tall trigger volume directly above the pad to detect overlapping cargo items
            BoxCollider triggerCol = gameObject.AddComponent<BoxCollider>();
            triggerCol.isTrigger = true;
            triggerCol.size = new Vector3(solidCol.size.x, 3.0f, solidCol.size.z);
            triggerCol.center = new Vector3(solidCol.center.x, 1.5f, solidCol.center.z);
        }

        private void OnTriggerEnter(Collider other)
        {
            CargoItem item = other.GetComponentInParent<CargoItem>();
            if (item != null)
            {
                item.RegisterOverlappingZone(this);
            }
        }

        private void OnTriggerExit(Collider other)
        {
            CargoItem item = other.GetComponentInParent<CargoItem>();
            if (item != null)
            {
                item.UnregisterOverlappingZone(this);
            }
        }

        /// <summary>Register an item as active here and add its weight.</summary>
        public void AddItem(CargoItem item)
        {
            if (item == null || securedItems.Contains(item)) return;
            securedItems.Add(item);
            RecalculateWeight();
        }

        /// <summary>Remove an item and subtract its weight.</summary>
        public void RemoveItem(CargoItem item)
        {
            if (item == null) return;
            if (securedItems.Remove(item))
                RecalculateWeight();
        }

        private void RecalculateWeight()
        {
            totalWeight = 0f;
            for (int i = 0; i < securedItems.Count; i++)
            {
                if (securedItems[i] != null)
                    totalWeight += securedItems[i].weight;
            }
        }

#if UNITY_EDITOR
        // Colored gizmo + label so zones are easy to identify in the Scene view.
        private void OnDrawGizmos()
        {
            Color c = ZoneColor(zoneType);
            Gizmos.color = c;
            Vector3 center = transform.position + Vector3.up * 1.5f;
            Gizmos.DrawWireCube(center, new Vector3(2f, 3.0f, 2f));

            UnityEditor.Handles.color = c;
            UnityEditor.Handles.Label(transform.position + Vector3.up * 0.4f, $"{zoneType}\n{totalWeight:0} kg");
        }
#endif

        /// <summary>A distinct debug color per zone type.</summary>
        public static Color ZoneColor(ZoneType type)
        {
            switch (type)
            {
                case ZoneType.FrontLeft: return Color.cyan;
                case ZoneType.FrontRight: return Color.green;
                case ZoneType.RearLeft: return Color.yellow;
                case ZoneType.RearRight: return Color.magenta;
                default: return Color.white;
            }
        }
    }
}

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
    /// A single cargo placement area on the deck. Tracks the total weight of the
    /// cargo secured into it and provides a snap point for placement.
    ///
    /// SCENE SETUP:
    ///  - Create a Cube on the deck (e.g. Scale 2 x 0.2 x 2) for each zone.
    ///  - Add this component and pick the matching ZoneType.
    ///  - (Optional) Add an empty child named "SnapPoint" and assign it to
    ///    'snapPoint' so cargo lands at a tidy spot. If left empty the zone's own
    ///    transform is used.
    ///  - Keep the cube's BoxCollider NON-trigger; the interaction ray uses it so
    ///    the player can look at the zone and press F to secure cargo.
    /// </summary>
    public class CargoZone : MonoBehaviour
    {
        [Tooltip("Which balance quadrant this zone feeds into.")]
        public ZoneType zoneType = ZoneType.Center;

        [Tooltip("Optional transform where secured cargo snaps to. " +
                 "If empty, this object's transform is used.")]
        public Transform snapPoint;

        [Tooltip("Vertical offset above the snap point so cargo rests on top of the zone instead of clipping inside it.")]
        public float placeHeightOffset = 0.5f;

        [Header("Runtime (read-only)")]
        [SerializeField] private float totalWeight;
        [SerializeField] private List<CargoItem> securedItems = new List<CargoItem>();

        /// <summary>Total weight of all cargo currently secured in this zone.</summary>
        public float TotalWeight => totalWeight;
        public IReadOnlyList<CargoItem> SecuredItems => securedItems;

        /// <summary>World position where a newly secured item should be placed.</summary>
        public Vector3 GetPlacePosition()
        {
            Transform t = snapPoint != null ? snapPoint : transform;
            return t.position + Vector3.up * placeHeightOffset;
        }

        /// <summary>Transform that secured items should be parented to (so they ride with the ship).</summary>
        public Transform GetParentForItems()
        {
            return snapPoint != null ? snapPoint : transform;
        }

        /// <summary>Register an item as secured here and add its weight.</summary>
        public void AddItem(CargoItem item)
        {
            if (item == null || securedItems.Contains(item)) return;
            securedItems.Add(item);
            RecalculateWeight();
        }

        /// <summary>Remove a secured item and subtract its weight.</summary>
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
            Vector3 center = GetPlacePosition();
            Gizmos.DrawWireCube(center, new Vector3(1.5f, 0.3f, 1.5f));
            Gizmos.DrawSphere(center, 0.1f);

            UnityEditor.Handles.color = c;
            UnityEditor.Handles.Label(center + Vector3.up * 0.4f, $"{zoneType}\n{totalWeight:0} kg");
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

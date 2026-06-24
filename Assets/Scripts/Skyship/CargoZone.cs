using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Identifies which balance area of the ship a zone represents.
    /// Kept as a simple static enum for backward compatibility with debug UI / colors.
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
    /// Retired placeholder. The ship now calculates weight distribution with infinite resolution
    /// based on exact cargo position coordinates on the deck rather than static trigger zones!
    /// </summary>
    public class CargoZone : MonoBehaviour
    {
        public ZoneType zoneType = ZoneType.Center;
        public float TotalWeight => 0f;

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

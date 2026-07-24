using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Placeholder marker that makes objective cargo unmistakable until real models exist:
    /// a slowly spinning, bobbing emissive "gem" hovering over the item plus a pulsing point
    /// light. Attached as a child, so it follows the crate through pickup/carry/drop for free.
    /// Purely visual (no collider — the crate's own physics/interaction stay untouched) and
    /// local-only, so it needs no network sync.
    /// </summary>
    public class ObjectiveBeacon : MonoBehaviour
    {
        [Header("Feel")]
        public float spinSpeed = 70f;      // deg/s
        public float bobAmplitude = 0.07f; // meters
        public float pulseSpeed = 2.2f;    // rad/s
        public Color glowColor = new Color(0.35f, 1f, 0.55f);

        private Light glow;
        private Transform gem;
        private Material gemMat;
        private Vector3 gemHome;
        private float phase;

        /// <summary>Attach a beacon above the target (idempotent).</summary>
        public static void AttachTo(GameObject target)
        {
            if (target == null || target.GetComponentInChildren<ObjectiveBeacon>() != null) return;
            var go = new GameObject("ObjectiveBeacon");
            go.transform.SetParent(target.transform, false);
            go.transform.localPosition = Vector3.up * 1.0f;
            go.AddComponent<ObjectiveBeacon>();
        }

        private void Awake()
        {
            phase = Random.Range(0f, Mathf.PI * 2f);

            glow = gameObject.AddComponent<Light>();
            glow.type = LightType.Point;
            glow.color = glowColor;
            glow.range = 7f;
            glow.intensity = 2.2f;
            glow.shadows = LightShadows.None;

            // The gem: a double-rotated cube reads as a floating crystal.
            var gemGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            gemGo.name = "Gem";
            Destroy(gemGo.GetComponent<Collider>()); // visual only — never part of the crate's physics
            gemGo.transform.SetParent(transform, false);
            gemGo.transform.localRotation = Quaternion.Euler(45f, 0f, 45f);
            gemGo.transform.localScale = Vector3.one * 0.22f;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            gemMat = new Material(shader) { color = glowColor };
            if (gemMat.HasProperty("_BaseColor")) gemMat.SetColor("_BaseColor", glowColor);
            gemMat.EnableKeyword("_EMISSION");
            gemGo.GetComponent<Renderer>().sharedMaterial = gemMat;

            gem = gemGo.transform;
            gemHome = gem.localPosition;
        }

        private void Update()
        {
            float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * pulseSpeed + phase);

            if (gem != null)
            {
                gem.Rotate(Vector3.up, spinSpeed * Time.deltaTime, Space.Self);
                gem.localPosition = gemHome + Vector3.up * (Mathf.Sin(Time.time * 1.4f + phase) * bobAmplitude);
            }
            if (glow != null)
                glow.intensity = Mathf.Lerp(1.4f, 3.2f, pulse);
            if (gemMat != null && gemMat.HasProperty("_EmissionColor"))
                gemMat.SetColor("_EmissionColor", glowColor * Mathf.Lerp(0.6f, 1.8f, pulse));
        }
    }
}

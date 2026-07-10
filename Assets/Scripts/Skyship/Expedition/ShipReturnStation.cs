using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// The "Return to Port" bell on the ship's deck (world scene only, built at runtime by
    /// VerticalSliceBootstrap near the helm). Press F while looking at it:
    ///  - objective complete → return home for full success;
    ///  - objective NOT complete → first press arms an "abandon expedition?" confirm, a second
    ///    press within a few seconds abandons (partial rewards for whatever cargo is aboard).
    ///
    /// HOST-AUTHORITATIVE: the press routes through ExpeditionManager.RequestReturnToPort —
    /// the authority executes, a client sends a ReturnRequest packet for the host to validate.
    /// </summary>
    public class ShipReturnStation : MonoBehaviour
    {
        [Tooltip("Seconds the abandon-confirm stays armed after the first press.")]
        public float confirmWindow = 4f;

        private float confirmUntil;

        /// <summary>Is the abandon confirm currently armed? (HUD shows the warning text)</summary>
        public bool ConfirmArmed => Time.time < confirmUntil;

        /// <summary>Build the bell near the helm. Idempotent; no-op without a ship.</summary>
        public static ShipReturnStation CreateOnShip()
        {
            var existing = FindAnyObjectByType<ShipReturnStation>();
            if (existing != null) return existing;

            var ship = GameObject.Find("ShipRoot");
            if (ship == null) return null;
            var balance = ship.GetComponent<ShipBalanceController>();
            Transform visualRoot = balance != null && balance.shipVisualRoot != null
                ? balance.shipVisualRoot : ship.transform;

            // Anchor: a little forward of the wheel so it sits by the pilot without blocking the levers.
            var helm = FindAnyObjectByType<ShipHelm>();
            Vector3 localPos = helm != null
                ? visualRoot.InverseTransformPoint(helm.transform.position) + new Vector3(0f, 0f, 2.2f)
                : new Vector3(0f, 1f, 4f);

            var go = new GameObject("ReturnToPortBell");
            go.transform.SetParent(visualRoot, false);
            go.transform.localPosition = new Vector3(localPos.x, 0f, localPos.z);

            // Pedestal + bell (placeholder primitives, matching the prototype's style).
            var post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            post.name = "BellPost";
            post.transform.SetParent(go.transform, false);
            post.transform.localPosition = new Vector3(0f, 0.55f, 0f);
            post.transform.localScale = new Vector3(0.12f, 0.55f, 0.12f);
            post.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.30f, 0.22f, 0.14f));

            var bell = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bell.name = "Bell";
            bell.transform.SetParent(go.transform, false);
            bell.transform.localPosition = new Vector3(0f, 1.25f, 0f);
            bell.transform.localScale = new Vector3(0.35f, 0.4f, 0.35f);
            var bellMat = MakeMat(new Color(0.85f, 0.68f, 0.25f));
            bellMat.EnableKeyword("_EMISSION");
            if (bellMat.HasProperty("_EmissionColor"))
                bellMat.SetColor("_EmissionColor", new Color(0.35f, 0.25f, 0.05f));
            bell.GetComponent<Renderer>().sharedMaterial = bellMat;

            // Floating label so players can find it (TextMesh renders double-sided in this project).
            var labelGo = new GameObject("Label");
            labelGo.transform.SetParent(go.transform, false);
            labelGo.transform.localPosition = new Vector3(0f, 1.7f, 0f);
            labelGo.transform.localScale = Vector3.one * 0.06f;
            var text = labelGo.AddComponent<TextMesh>();
            text.text = "RETURN TO PORT [F]";
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 48;
            text.color = new Color(1f, 0.9f, 0.6f);

            var station = go.AddComponent<ShipReturnStation>();
            // The primitives above already carry colliders the interaction ray can hit; F routes
            // here via PlayerInteraction → GetComponentInParent<ShipReturnStation>().
            return station;
        }

        /// <summary>Called by PlayerInteraction when the local player presses F at the bell.</summary>
        public void Press()
        {
            var manager = ExpeditionManager.Instance;
            if (manager == null) return;
            var phase = manager.runtime.phase;
            if (phase != ExpeditionPhase.Active && phase != ExpeditionPhase.ReturnReady) return;

            if (phase == ExpeditionPhase.ReturnReady)
            {
                manager.RequestReturnToPort();
                return;
            }

            // Objective incomplete: two-press confirm so nobody fat-fingers an abandon.
            if (ConfirmArmed)
            {
                confirmUntil = 0f;
                manager.RequestReturnToPort();
            }
            else
            {
                confirmUntil = Time.time + confirmWindow;
                NetworkManagerP2P.Instance?.ShowBanner(
                    "Objective incomplete — ring again to ABANDON the expedition.", confirmWindow);
            }
        }

        private static Material MakeMat(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// A flat 2D chart of the generated world, built at runtime on top of the ship's lower-cabin
    /// desk: one flat marker per island (sized/colored by tier) and derelict lying on the board
    /// like a paper map, plus a live ship arrow that tracks the real ship's position and heading.
    /// Everyone aboard can read it — no UI screens, Sea-of-Thieves style.
    ///
    /// CHART VIEW: press F while looking at the table (PlayerInteraction routes it here) to put
    /// the camera overhead — E/Q zoom in/out, WASD pans, F returns to first person. The local
    /// player's controller and interaction are suspended while viewing (view is local-only, so
    /// nothing needs syncing). The camera is parented to the table so ship motion/tilt carries
    /// the view for free.
    ///
    /// Spawned by WorldGenerator after generation (CreateOnShip). Parented under ShipVisualRoot so
    /// it rides and tilts with the deck; NOT parented under the desk itself, because the desk is a
    /// scaled primitive and would distort the markers. The only collider is a trigger over the
    /// board so the interaction ray can find it.
    /// </summary>
    public class ShipMapTable : MonoBehaviour
    {
        [Header("Board (read-only, set at build time)")]
        [SerializeField] private float boardWidth = 1.3f;

        [Header("Chart View")]
        [Tooltip("Camera height above the board when fully zoomed IN (E).")]
        public float viewHeightMin = 0.22f;
        [Tooltip("Camera height above the board when fully zoomed OUT (Q) — frames the whole map.")]
        public float viewHeightMax = 1.15f;
        [Tooltip("Zoom rate in meters of height per second.")]
        public float zoomSpeed = 0.9f;
        [Tooltip("Pan rate at full zoom-in, scaled up with height (WASD).")]
        public float panSpeed = 0.9f;

        private Vector3 worldCenter;
        private Vector3 worldExtent;
        private IReadOnlyList<MapStructureInfo> structures;
        private Transform markerRoot;
        private Transform shipMarker;
        private Transform stormMarker;
        private Transform shipRoot;
        private float worldToBoard;

        // Chart-view state (local player only).
        private bool viewing;
        private Transform viewCamera;
        private Transform camOrigParent;
        private Vector3 camOrigLocalPos;
        private Quaternion camOrigLocalRot;
        private FirstPersonController viewerController;
        private PlayerInteraction viewerInteraction;
        private float viewHeight;
        private Vector2 panOffset; // board-local XZ
        private int exitedFrame = -1; // guards F-exit against a same-frame re-enter

        public bool IsViewing => viewing;

        /// <summary>
        /// Build the map table on the ship in the current scene. Idempotent: replaces any previous
        /// table. Returns null when there's no ship or no structures (e.g. not in a generated world).
        /// </summary>
        public static ShipMapTable CreateOnShip(IReadOnlyList<MapStructureInfo> structures,
                                                Vector3 worldExtent, Vector3 worldCenter)
        {
            var ship = GameObject.Find("ShipRoot");
            if (ship == null || structures == null || structures.Count == 0) return null;

            var balance = ship.GetComponent<ShipBalanceController>();
            Transform visualRoot = balance != null && balance.shipVisualRoot != null
                ? balance.shipVisualRoot
                : ship.transform;

            // Replace any existing table (e.g. after a regenerate).
            var old = visualRoot.GetComponentInChildren<ShipMapTable>();
            if (old != null) Destroy(old.gameObject);

            // Anchor on the lower-cabin desk if it exists; otherwise sit low near the stern.
            Transform desk = FindDeep(visualRoot, "LowerCabin_Desk");

            var go = new GameObject("ShipMapTable");
            go.transform.SetParent(visualRoot, false);

            var table = go.AddComponent<ShipMapTable>();
            table.worldCenter = worldCenter;
            table.worldExtent = worldExtent;
            table.structures = structures;
            table.shipRoot = ship.transform;
            table.Build(desk);
            return table;
        }

        private void Build(Transform desk)
        {
            // Place the board on the desk's top surface (world-space bounds handle the desk's scale).
            if (desk != null)
            {
                var r = desk.GetComponent<Renderer>();
                if (r != null)
                {
                    Bounds b = r.bounds;
                    transform.position = new Vector3(b.center.x, b.max.y, b.center.z);
                    // Stay inside the desk footprint — the desk sits against a cabin wall, so an
                    // overhanging board clips into it.
                    boardWidth = Mathf.Clamp(Mathf.Min(b.size.x, b.size.z) * 0.85f, 0.8f, 1.4f);
                }
                else
                {
                    transform.position = desk.position + Vector3.up * 0.8f;
                }
            }
            else
            {
                transform.localPosition = new Vector3(0f, 1.0f, -6f); // fallback: low near the stern
            }

            float maxWorldSpan = Mathf.Max(worldExtent.x, worldExtent.z);
            worldToBoard = boardWidth / Mathf.Max(maxWorldSpan, 1f);

            // The board itself: a thin dark slab the markers float above.
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = "Board";
            Destroy(board.GetComponent<Collider>());
            board.transform.SetParent(transform, false);
            board.transform.localPosition = Vector3.zero;
            board.transform.localScale = new Vector3(boardWidth, 0.03f, boardWidth);
            board.GetComponent<Renderer>().sharedMaterial = MakeMat(new Color(0.10f, 0.14f, 0.20f));

            // Trigger over the board so PlayerInteraction's ray can find the table for F (chart view).
            var interactVolume = gameObject.AddComponent<BoxCollider>();
            interactVolume.isTrigger = true;
            interactVolume.center = new Vector3(0f, 0.1f, 0f);
            interactVolume.size = new Vector3(boardWidth, 0.25f, boardWidth);

            markerRoot = new GameObject("Markers").transform;
            markerRoot.SetParent(transform, false);
            markerRoot.localPosition = new Vector3(0f, 0.022f, 0f); // just above the board face (no z-fighting)

            foreach (var s in structures)
                BuildMarker(s);

            // Live ship marker: a flat glowing arrow (shaft + nose dot) lying on the chart,
            // spun to the ship's heading.
            var shipGo = new GameObject("ShipMarker");
            shipGo.transform.SetParent(markerRoot, false);
            var mat = MakeMat(new Color(1f, 0.45f, 0.1f));
            mat.EnableKeyword("_EMISSION");
            if (mat.HasProperty("_EmissionColor"))
                mat.SetColor("_EmissionColor", new Color(0.9f, 0.35f, 0.05f));

            var shaft = GameObject.CreatePrimitive(PrimitiveType.Cube);
            shaft.name = "Shaft";
            Destroy(shaft.GetComponent<Collider>());
            shaft.transform.SetParent(shipGo.transform, false);
            shaft.transform.localScale = new Vector3(0.012f, 0.004f, 0.034f);
            shaft.GetComponent<Renderer>().sharedMaterial = mat;

            var nose = GameObject.CreatePrimitive(PrimitiveType.Cube);
            nose.name = "Nose";
            Destroy(nose.GetComponent<Collider>());
            nose.transform.SetParent(shipGo.transform, false);
            nose.transform.localPosition = new Vector3(0f, 0f, 0.022f);
            nose.transform.localRotation = Quaternion.Euler(0f, 45f, 0f); // diamond tip = "this way"
            nose.transform.localScale = new Vector3(0.016f, 0.004f, 0.016f);
            nose.GetComponent<Renderer>().sharedMaterial = mat;

            shipMarker = shipGo.transform;

            // Live storm marker: a flat dark disc that tracks the traveling storm cell, so the
            // crew can plot a course around its path. Hidden while no cell is on the map.
            var stormGo = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            stormGo.name = "StormMarker";
            Destroy(stormGo.GetComponent<Collider>());
            stormGo.transform.SetParent(markerRoot, false);
            var stormMat = MakeMat(new Color(0.45f, 0.14f, 0.50f));
            stormMat.EnableKeyword("_EMISSION");
            if (stormMat.HasProperty("_EmissionColor"))
                stormMat.SetColor("_EmissionColor", new Color(0.55f, 0.12f, 0.70f));
            stormGo.GetComponent<Renderer>().sharedMaterial = stormMat;
            stormGo.SetActive(false);
            stormMarker = stormGo.transform;
        }

        private void BuildMarker(MapStructureInfo s)
        {
            var marker = GameObject.CreatePrimitive(
                s.kind == MapStructureKind.Derelict ? PrimitiveType.Cube : PrimitiveType.Cylinder);
            marker.name = "M_" + s.name;
            Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(markerRoot, false);
            marker.transform.localPosition = WorldToBoard(s.position);

            // Marker footprint tracks the real radius, clamped so small islands stay findable.
            // Paper-thin: everything lies flat on the chart.
            float d = Mathf.Max(s.radius * 2f * worldToBoard, 0.012f);
            marker.transform.localScale = s.kind == MapStructureKind.Derelict
                ? new Vector3(d, 0.003f, d * 1.6f) // elongated hull silhouette
                : new Vector3(d, 0.0015f, d);      // flat island disc

            marker.GetComponent<Renderer>().sharedMaterial = MakeMat(ColorFor(s.kind));
        }

        private static Color ColorFor(MapStructureKind kind)
        {
            switch (kind)
            {
                case MapStructureKind.IslandVeryLarge: return new Color(0.15f, 0.45f, 0.20f);
                case MapStructureKind.IslandLarge: return new Color(0.30f, 0.60f, 0.28f);
                case MapStructureKind.IslandMedium: return new Color(0.45f, 0.70f, 0.38f);
                case MapStructureKind.IslandSmall: return new Color(0.62f, 0.78f, 0.52f);
                case MapStructureKind.Objective: return new Color(0.85f, 0.15f, 0.12f); // mission site: red
                default: return new Color(0.45f, 0.32f, 0.20f); // derelict
            }
        }

        /// <summary>World position → board-local marker position. 2D chart: altitude is dropped —
        /// everything projects flat onto the board plane.</summary>
        private Vector3 WorldToBoard(Vector3 world)
        {
            Vector3 rel = world - worldCenter;
            return new Vector3(rel.x * worldToBoard, 0f, rel.z * worldToBoard);
        }

        private void Update()
        {
            if (shipMarker != null && shipRoot != null)
            {
                shipMarker.localPosition = WorldToBoard(shipRoot.position);
                // Show heading: spin the marker to the ship's yaw (board-local, so deck tilt is inherited).
                shipMarker.localRotation = Quaternion.Euler(0f, shipRoot.eulerAngles.y, 0f);

                // Inside a whisper fog bank the chart lies: the ship arrow wanders and spins wrong.
                float distortion = WhisperFogSystem.LocalDistortion;
                if (distortion > 0.25f)
                {
                    float t = Time.time * 3f;
                    shipMarker.localPosition += new Vector3(
                        (Mathf.PerlinNoise(t, 1.3f) - 0.5f),
                        0f,
                        (Mathf.PerlinNoise(t, 8.7f) - 0.5f)) * (boardWidth * 0.25f * distortion);
                    shipMarker.localRotation = Quaternion.Euler(0f, Time.time * 160f * distortion, 0f);
                }
            }

            // Track the traveling storm cell (synced host state) at true chart scale.
            if (stormMarker != null)
            {
                var manager = ExpeditionManager.Instance;
                bool show = manager != null && manager.runtime.stormActive;
                if (stormMarker.gameObject.activeSelf != show)
                    stormMarker.gameObject.SetActive(show);
                if (show)
                {
                    // Clamp onto the board: a cell entering from outside the map pins at the edge
                    // so the crew still sees which side the weather is coming from.
                    Vector3 p = WorldToBoard(manager.runtime.stormCenter);
                    float half = boardWidth * 0.5f;
                    p.x = Mathf.Clamp(p.x, -half, half);
                    p.z = Mathf.Clamp(p.z, -half, half);
                    stormMarker.localPosition = p + new Vector3(0f, 0.004f, 0f);

                    // True footprint at chart scale, with a slow menace-pulse so it reads at a glance.
                    float pulse = 1f + 0.06f * Mathf.Sin(Time.time * 2.5f);
                    float d = Mathf.Max(manager.runtime.stormRadius * 2f * worldToBoard, 0.05f) * pulse;
                    stormMarker.localScale = new Vector3(d, 0.0015f, d);
                }
            }

            if (viewing) HandleViewInput();
        }

        // ==========================================
        // CHART VIEW (F to enter/exit, E/Q zoom, WASD pan)
        // ==========================================

        /// <summary>Called by PlayerInteraction when the local player presses F at the table.</summary>
        public void ToggleView(GameObject player)
        {
            if (viewing) ExitView();
            // Same-frame guard: when our Update just exited on this very F press, don't let a
            // re-enabled PlayerInteraction (running later in the frame) re-enter from that press.
            else if (Time.frameCount != exitedFrame) EnterView(player);
        }

        private void EnterView(GameObject player)
        {
            var cam = Camera.main;
            if (cam == null || player == null) return;

            viewerController = player.GetComponent<FirstPersonController>();
            viewerInteraction = player.GetComponent<PlayerInteraction>();

            // A crate held at the HoldPoint is a child of the camera — drop it rather than
            // dragging it over the map (same rule as taking the helm).
            if (viewerInteraction != null) viewerInteraction.DropHeld();

            viewCamera = cam.transform;
            camOrigParent = viewCamera.parent;
            camOrigLocalPos = viewCamera.localPosition;
            camOrigLocalRot = viewCamera.localRotation;

            // Suspend the whole controller: its Update writes camera rotation every frame
            // (mouse look), which would fight the overhead view. Re-enabling restores the
            // stored pitch automatically.
            if (viewerController != null) viewerController.enabled = false;
            if (viewerInteraction != null) viewerInteraction.enabled = false;

            viewCamera.SetParent(transform, true); // ride the table through ship motion/tilt
            viewHeight = viewHeightMax;
            panOffset = Vector2.zero;
            ApplyViewPose();
            viewing = true;
        }

        private void ExitView()
        {
            viewing = false;
            exitedFrame = Time.frameCount;
            if (viewCamera != null)
            {
                viewCamera.SetParent(camOrigParent, false);
                viewCamera.localPosition = camOrigLocalPos;
                viewCamera.localRotation = camOrigLocalRot;
            }
            if (viewerController != null) viewerController.enabled = true;
            if (viewerInteraction != null) viewerInteraction.enabled = true;
            viewCamera = null;
            viewerController = null;
            viewerInteraction = null;
        }

        private void HandleViewInput()
        {
            var k = Keyboard.current;
            if (k == null) return;

            if (k.fKey.wasPressedThisFrame)
            {
                ExitView();
                return;
            }

            // E zooms in (lower over the board), Q zooms out.
            float zoom = 0f;
            if (k.eKey.isPressed) zoom -= 1f;
            if (k.qKey.isPressed) zoom += 1f;
            viewHeight = Mathf.Clamp(viewHeight + zoom * zoomSpeed * Time.deltaTime,
                                     viewHeightMin, viewHeightMax);

            // WASD pans across the board; rate scales with zoom so it feels steady on screen.
            Vector2 pan = Vector2.zero;
            if (k.wKey.isPressed) pan.y += 1f;
            if (k.sKey.isPressed) pan.y -= 1f;
            if (k.dKey.isPressed) pan.x += 1f;
            if (k.aKey.isPressed) pan.x -= 1f;
            panOffset += pan * (panSpeed * viewHeight * Time.deltaTime);

            // Keep the view over the board: pan range grows as you zoom in.
            float halfVisible = viewHeight * 0.55f; // ~half the vertical view at 60° FOV
            float panLimit = Mathf.Max(0f, boardWidth * 0.5f - halfVisible);
            panOffset.x = Mathf.Clamp(panOffset.x, -panLimit, panLimit);
            panOffset.y = Mathf.Clamp(panOffset.y, -panLimit, panLimit);

            ApplyViewPose();
        }

        private void ApplyViewPose()
        {
            if (viewCamera == null) return;
            viewCamera.localPosition = new Vector3(panOffset.x, viewHeight, panOffset.y);
            // Straight down; board-local +Z reads as screen-up.
            viewCamera.localRotation = Quaternion.Euler(90f, 0f, 0f);
        }

        private static Transform FindDeep(Transform root, string childName)
        {
            if (root.name == childName) return root;
            foreach (Transform child in root)
            {
                var found = FindDeep(child, childName);
                if (found != null) return found;
            }
            return null;
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

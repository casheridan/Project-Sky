using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// Runtime test console used only by BarrierTestScene. It drives the production cargo,
    /// balance, deck-surface, and barrier components so test results match normal gameplay.
    /// Nothing created by this harness is saved back into the scene.
    /// </summary>
    [DefaultExecutionOrder(100)]
    public sealed class ShipBarrierTestHarness : MonoBehaviour
    {
        [Header("References (auto-wired)")]
        public ShipBarrierSystem barriers;
        public ShipBalanceController balance;
        public ShipPlatformArea platformArea;
        public Transform shipVisualRoot;
        public Camera playerCamera;

        [Header("Scenario tuning")]
        [Tooltip("Multiplier applied to fatigue speed while accelerated testing is enabled.")]
        [Min(1f)] public float acceleratedFatigueMultiplier = 150f;
        [Tooltip("Weight of each crate in the two-object pressure-chain example.")]
        public float chainCrateWeight = 30f;
        [Tooltip("Weight of the crate launched during the impact example.")]
        public float impactCrateWeight = 30f;
        [Tooltip("Local launch speed of the impact-test crate toward the port railing.")]
        public float impactSpeed = 6f;

        [Header("Runtime (read-only)")]
        [SerializeField] private bool showPanel = true;
        [SerializeField] private bool acceleratedFatigue;
        [SerializeField] private int spawnedTestCargo;

        private readonly List<CargoItem> testCargo = new List<CargoItem>();
        private readonly List<Material> runtimeMaterials = new List<Material>();
        private Material chainMaterial;
        private Material overloadMaterial;
        private Material impactMaterial;
        private float originalRatedHoldSeconds;
        private ShipDeckSurfaceController.SurfaceCondition originalSurface;
        private bool initialized;
        private string lastAction = "Ready. Spawn a scenario or move cargo by hand.";
        private GUIStyle titleStyle;
        private GUIStyle bodyStyle;
        private GUIStyle boxStyle;
        private GUIStyle statusStyle;

        private void Start()
        {
            InitializeWhenReady();
        }

        private void Update()
        {
            if (!initialized)
            {
                InitializeWhenReady();
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null) return;

            if (keyboard.f1Key.wasPressedThisFrame) showPanel = !showPanel;
            if (keyboard.digit1Key.wasPressedThisFrame) SetSurface(ShipDeckSurfaceController.SurfaceCondition.Dry);
            if (keyboard.digit2Key.wasPressedThisFrame) SetSurface(ShipDeckSurfaceController.SurfaceCondition.Rain);
            if (keyboard.digit3Key.wasPressedThisFrame) SetSurface(ShipDeckSurfaceController.SurfaceCondition.Ice);
            if (keyboard.digit4Key.wasPressedThisFrame) SetSurface(ShipDeckSurfaceController.SurfaceCondition.Sand);
            if (keyboard.lKey.wasPressedThisFrame) SpawnPressureChain();
            if (keyboard.oKey.wasPressedThisFrame) SpawnOverloadChain();
            if (keyboard.iKey.wasPressedThisFrame) LaunchImpactCrate();
            if (keyboard.bKey.wasPressedThisFrame) BreakAimedBarrier();
            if (keyboard.yKey.wasPressedThisFrame) ToggleAcceleratedFatigue();
            if (keyboard.rKey.wasPressedThisFrame) ResetAllBarriers();
            if (keyboard.cKey.wasPressedThisFrame) ClearTestCargo();
        }

        private void InitializeWhenReady()
        {
            if (barriers == null) barriers = FindAnyObjectByType<ShipBarrierSystem>();
            if (balance == null) balance = FindAnyObjectByType<ShipBalanceController>();
            if (platformArea == null) platformArea = FindAnyObjectByType<ShipPlatformArea>();
            if (shipVisualRoot == null && balance != null) shipVisualRoot = balance.shipVisualRoot;
            if (playerCamera == null) playerCamera = Camera.main;

            if (barriers == null || balance == null || platformArea == null || shipVisualRoot == null)
                return;

            originalRatedHoldSeconds = barriers.ratedHoldSeconds;
            originalSurface = barriers.DeckSurface != null
                ? barriers.DeckSurface.baseCondition
                : ShipDeckSurfaceController.SurfaceCondition.Dry;
            BuildMaterials();
            BuildDeckMarkers();
            initialized = true;
            lastAction = $"Initialized {barriers.Segments.Count} production railing segments.";
            Debug.Log("[ShipBarrierTestHarness] " + lastAction);
        }

        private void SetSurface(ShipDeckSurfaceController.SurfaceCondition condition)
        {
            if (barriers.DeckSurface == null) return;
            barriers.DeckSurface.SetBaseCondition(condition);
            lastAction = $"Deck condition set to {condition}.";
        }

        private void SpawnPressureChain()
        {
            ClearTestCargo();
            float deckTop = FindUpperDeckTop();
            CreateCargo("TEST_Chain_Rail", chainCrateWeight,
                new Vector3(-4.72f, deckTop + 0.58f, 5.6f), Vector3.zero, chainMaterial);
            CreateCargo("TEST_Chain_Pusher", chainCrateWeight,
                new Vector3(-3.60f, deckTop + 0.58f, 5.6f), Vector3.zero, chainMaterial);
            lastAction = "Spawned two 30-unit crates: one touches the port rail and one pushes through it.";
        }

        private void SpawnOverloadChain()
        {
            ClearTestCargo();
            float deckTop = FindUpperDeckTop();
            for (int i = 0; i < 3; i++)
            {
                CreateCargo($"TEST_Overload_{i + 1}", 100f,
                    new Vector3(-4.72f + i * 1.12f, deckTop + 0.58f, -5.6f),
                    Vector3.zero, overloadMaterial);
            }
            lastAction = "Spawned a 300-unit port-side chain. It should overload its railing as the ship leans.";
        }

        private void LaunchImpactCrate()
        {
            float deckTop = FindUpperDeckTop();
            CargoItem item = CreateCargo($"TEST_Impact_{spawnedTestCargo + 1}", impactCrateWeight,
                new Vector3(-4.15f, deckTop + 0.58f, 3.5f),
                new Vector3(-impactSpeed, 0f, 0f), impactMaterial);
            // Keep this short launch repeatable across surface modes. The main grip comparison
            // remains the pressure-chain scenario; this crate represents smooth impact cargo.
            item.deckGripMultiplier = 0.1f;
            lastAction = $"Launched a {impactCrateWeight:0}-unit crate at {impactSpeed:0.0} m/s toward the port rail.";
        }

        private CargoItem CreateCargo(string objectName, float weight, Vector3 localPosition,
                                      Vector3 localVelocity, Material material)
        {
            GameObject crate = GameObject.CreatePrimitive(PrimitiveType.Cube);
            crate.name = objectName;
            crate.transform.SetParent(shipVisualRoot, false);
            crate.transform.localPosition = localPosition;
            crate.transform.localRotation = Quaternion.identity;
            crate.transform.localScale = new Vector3(1.05f, 1f, 1.05f);

            Renderer renderer = crate.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = material;

            Rigidbody body = crate.AddComponent<Rigidbody>();
            CargoItem item = crate.AddComponent<CargoItem>();
            item.itemName = objectName;
            item.category = CargoCategory.Generic;
            item.weight = weight;
            item.value = 0f;
            item.useCategorySurfaceDefaults = false;
            item.deckGripMultiplier = 1f;
            item.barrierImpactMultiplier = 1f;
            item.RefreshPhysicalProperties();

            Vector3 shipVelocity = platformArea != null ? platformArea.CurrentShipVelocity : Vector3.zero;
            body.linearVelocity = shipVelocity + shipVisualRoot.TransformDirection(localVelocity);
            body.angularVelocity = Vector3.zero;

            testCargo.Add(item);
            spawnedTestCargo = testCargo.Count;
            return item;
        }

        private void BreakAimedBarrier()
        {
            ShipBarrierSegment aimed = GetAimedBarrier();
            if (aimed == null)
            {
                lastAction = "No railing targeted. Look directly at a railing segment and press B.";
                return;
            }

            aimed.ApplyDamage(1f, "test command");
            lastAction = $"Broke {aimed.barrierId}. Walk within range, look at the remaining posts/gap, and press E.";
        }

        private void ToggleAcceleratedFatigue()
        {
            acceleratedFatigue = !acceleratedFatigue;
            barriers.ratedHoldSeconds = acceleratedFatigue
                ? originalRatedHoldSeconds / Mathf.Max(1f, acceleratedFatigueMultiplier)
                : originalRatedHoldSeconds;
            lastAction = acceleratedFatigue
                ? $"Fatigue accelerated ×{acceleratedFatigueMultiplier:0}. The 30+30 chain should fail in roughly 10 seconds."
                : $"Fatigue restored to production timing ({originalRatedHoldSeconds:0} seconds at rated load).";
        }

        private void ResetAllBarriers()
        {
            for (int i = 0; i < barriers.Segments.Count; i++)
            {
                ShipBarrierSegment segment = barriers.Segments[i];
                if (segment != null) segment.RepairFull();
            }
            lastAction = $"Restored all {barriers.Segments.Count} railing segments to full integrity.";
        }

        private void ClearTestCargo()
        {
            for (int i = 0; i < testCargo.Count; i++)
            {
                CargoItem item = testCargo[i];
                if (item != null) Destroy(item.gameObject);
            }
            testCargo.Clear();
            spawnedTestCargo = 0;
            lastAction = "Cleared test cargo.";
        }

        private ShipBarrierSegment GetAimedBarrier()
        {
            if (playerCamera == null) playerCamera = Camera.main;
            if (playerCamera == null) return null;
            Ray ray = new Ray(playerCamera.transform.position, playerCamera.transform.forward);
            if (!Physics.Raycast(ray, out RaycastHit hit, 12f, ~0, QueryTriggerInteraction.Collide))
                return null;
            return hit.collider.GetComponentInParent<ShipBarrierSegment>();
        }

        private float FindUpperDeckTop()
        {
            float top = 0.4f;
            foreach (Transform child in shipVisualRoot)
            {
                if (!child.name.StartsWith("UpperDeck")) continue;
                top = Mathf.Max(top, child.localPosition.y + Mathf.Abs(child.localScale.y) * 0.5f);
            }
            return top;
        }

        private void BuildDeckMarkers()
        {
            float deckTop = FindUpperDeckTop();
            CreateMarker("TEST_LoadChainMarker", new Vector3(-4.15f, deckTop + 0.015f, 5.6f),
                new Vector3(2.5f, 0.025f, 1.3f), new Color(0.15f, 0.55f, 1f, 0.45f));
            CreateMarker("TEST_OverloadMarker", new Vector3(-3.6f, deckTop + 0.015f, -5.6f),
                new Vector3(3.6f, 0.025f, 1.3f), new Color(1f, 0.25f, 0.12f, 0.45f));
            CreateMarker("TEST_ImpactLane", new Vector3(-4.3f, deckTop + 0.015f, 3.5f),
                new Vector3(1.8f, 0.025f, 1.3f), new Color(1f, 0.75f, 0.1f, 0.45f));
        }

        private void CreateMarker(string markerName, Vector3 localPosition, Vector3 localScale, Color color)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = markerName;
            marker.transform.SetParent(shipVisualRoot, false);
            marker.transform.localPosition = localPosition;
            marker.transform.localScale = localScale;
            Collider markerCollider = marker.GetComponent<Collider>();
            if (markerCollider != null) Destroy(markerCollider);
            Renderer renderer = marker.GetComponent<Renderer>();
            if (renderer != null) renderer.sharedMaterial = CreateMaterial(markerName + "_Material", color, true);
        }

        private void BuildMaterials()
        {
            chainMaterial = CreateMaterial("Test_ChainCargo", new Color(0.12f, 0.52f, 1f));
            overloadMaterial = CreateMaterial("Test_OverloadCargo", new Color(0.95f, 0.16f, 0.08f));
            impactMaterial = CreateMaterial("Test_ImpactCargo", new Color(1f, 0.7f, 0.05f));
        }

        private Material CreateMaterial(string materialName, Color color, bool transparent = false)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader) { name = materialName, color = color };
            if (transparent)
            {
                material.SetFloat("_Surface", 1f);
                material.SetFloat("_Blend", 0f);
                material.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite", 0f);
                material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                material.renderQueue = 3000;
            }
            runtimeMaterials.Add(material);
            return material;
        }

        private void OnGUI()
        {
            if (!showPanel) return;
            EnsureGuiStyles();

            float width = Mathf.Min(520f, Screen.width - 24f);
            GUILayout.BeginArea(new Rect(12f, 12f, width, Screen.height - 24f), boxStyle);
            GUILayout.Label("BREAKABLE RAILING TEST DECK", titleStyle);

            if (!initialized)
            {
                GUILayout.Label("Waiting for ship systems to initialize…", bodyStyle);
                GUILayout.EndArea();
                return;
            }

            int intact = 0;
            int damaged = 0;
            int broken = 0;
            float highestLoad = 0f;
            for (int i = 0; i < barriers.Segments.Count; i++)
            {
                ShipBarrierSegment segment = barriers.Segments[i];
                if (segment == null) continue;
                highestLoad = Mathf.Max(highestLoad, segment.currentAppliedLoad);
                if (segment.broken) broken++;
                else if (segment.integrity < 0.999f) damaged++;
                else intact++;
            }

            ShipDeckSurfaceController surface = barriers.DeckSurface;
            GUILayout.Label(
                $"Surface: {(surface != null ? surface.CurrentCondition.ToString() : "None")}  " +
                $"Grip: {(surface != null ? surface.CurrentGrip : 0f):0.000}    " +
                $"Fatigue: {(acceleratedFatigue ? "ACCELERATED" : "Production")}",
                statusStyle);
            GUILayout.Label(
                $"Cargo: {balance.totalWeight:0.0} units    Roll: {balance.rollImbalance:+0.000;-0.000;0.000}    " +
                $"Pitch: {balance.pitchImbalance:+0.000;-0.000;0.000}    Highest rail load: {highestLoad:0.0}",
                bodyStyle);
            GUILayout.Label($"Railings — intact {intact} | damaged {damaged} | broken {broken}", bodyStyle);

            ShipBarrierSegment aimed = GetAimedBarrier();
            string aimText = aimed == null
                ? "Aimed railing: none"
                : $"Aimed: {aimed.barrierId} | integrity {aimed.integrity:P0} | load {aimed.currentAppliedLoad:0.0} | " +
                  $"impact {aimed.lastImpactDamage:P1}";
            GUILayout.Label(aimText, statusStyle);
            GUILayout.Space(6f);

            GUILayout.Label("SCENARIOS", titleStyle);
            GUILayout.Label(
                "L  Two 30-unit chained crates (blue port marker)\n" +
                "O  Instant-overload chain (red port marker)\n" +
                "I  Launch an impact crate (yellow port lane)\n" +
                "B  Break the railing you are looking at\n" +
                "Y  Toggle ×150 fatigue speed\n" +
                "R  Restore every railing    C  Clear test cargo",
                bodyStyle);
            GUILayout.Space(5f);
            GUILayout.Label(
                "1 Dry    2 Rain    3 Ice    4 Sand\n" +
                "E picks up/drops cargo and repairs a damaged railing.\n" +
                "F1 hides/shows this panel. Normal WASD/mouse controls remain active.",
                bodyStyle);
            GUILayout.Space(7f);
            GUILayout.Label("Last action: " + lastAction, statusStyle);
            GUILayout.EndArea();
        }

        private void EnsureGuiStyles()
        {
            if (titleStyle != null) return;

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 17,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.94f, 0.82f, 0.38f) }
            };
            bodyStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = Color.white }
            };
            statusStyle = new GUIStyle(bodyStyle)
            {
                normal = { textColor = new Color(0.55f, 0.9f, 1f) }
            };
            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(12, 12, 10, 10),
                alignment = TextAnchor.UpperLeft
            };
        }

        private void OnDestroy()
        {
            if (barriers != null)
            {
                barriers.ratedHoldSeconds = originalRatedHoldSeconds;
                if (barriers.DeckSurface != null)
                    barriers.DeckSurface.SetBaseCondition(originalSurface);
            }

            for (int i = 0; i < runtimeMaterials.Count; i++)
            {
                if (runtimeMaterials[i] != null) Destroy(runtimeMaterials[i]);
            }
            runtimeMaterials.Clear();
        }
    }
}

using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// Handles looking at and manipulating cargo:
    ///   E             -> pick up the cargo item in view (or drop the held one)
    ///   Left Mouse    -> drop the held item
    ///
    /// Lives on the Player next to FirstPersonController. Kept separate so the
    /// movement controller can be swapped without touching interaction logic.
    ///
    /// SCENE SETUP:
    ///  - Add this to the Player GameObject.
    ///  - Assign 'cameraTransform' to PlayerCamera (the raycast origin).
    ///  - Assign 'holdPoint' to the HoldPoint child of the camera.
    /// </summary>
    public class PlayerInteraction : MonoBehaviour
    {
        [Header("References")]
        public Transform cameraTransform;
        [Tooltip("Empty transform in front of the camera where held cargo sits.")]
        public Transform holdPoint;

        [Header("Tuning")]
        public float interactionDistance = 3f;
        [Tooltip("Layers the interaction ray can hit. Default = Everything.")]
        public LayerMask interactionMask = ~0;
        [Tooltip("Minimum downward speed applied when cargo is released, removing the floaty zero-velocity drop.")]
        [Min(0f)] public float dropDownwardSpeed = 2.5f;

        private CargoItem heldItem;
        public CargoItem HeldItem => heldItem;

        private void Awake()
        {
            ResolveHoldPoint();
        }

        private void Update()
        {
            var k = Keyboard.current;
            var m = Mouse.current;

            bool pickDropPressed = k != null && k.eKey.wasPressedThisFrame;
            bool dropClick = m != null && m.leftButton.wasPressedThisFrame;
            bool stationPressed = k != null && k.fKey.wasPressedThisFrame;

            // F aimed at a ship station engages it: wheel (toggle helm), levers (hold to work),
            // ramp button (press), map table (chart view). Each station handles its own
            // controls/exit from there. E stays purely for cargo and resource nodes.
            if (stationPressed && TryUseStation())
                return;

            if (heldItem == null)
            {
                // E priority: repair a damaged railing, harvest a resource node, scrape a
                // hull barnacle, then pick up cargo.
                if (pickDropPressed && !TryRepairBarrier() && !TryHarvestNode() && !TryScrapeBarnacle())
                    TryPickUp();
            }
            else
            {
                if (pickDropPressed || dropClick)
                    DropHeld();
            }
        }

        private void LateUpdate()
        {
            if (heldItem == null) return;

            // CharacterController movement and mouse-look both happen in Update. Reassert the
            // carried pose afterward so the crate follows the final camera pose for this frame.
            if (ResolveHoldPoint())
                heldItem.FollowHoldPoint(holdPoint);
        }

        /// <summary>
        /// Keep scene wiring resilient. Authored scenes assign these references, but a generated
        /// or copied player should still receive a functional camera-relative hold point.
        /// </summary>
        private bool ResolveHoldPoint()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
            if (cameraTransform == null) return false;

            if (holdPoint == null)
            {
                Transform found = cameraTransform.Find("HoldPoint");
                if (found != null)
                {
                    holdPoint = found;
                }
                else
                {
                    GameObject created = new GameObject("HoldPoint");
                    holdPoint = created.transform;
                    holdPoint.SetParent(cameraTransform, false);
                    holdPoint.localPosition = new Vector3(0f, -0.3f, 1.2f);
                    holdPoint.localRotation = Quaternion.identity;
                    Debug.LogWarning("[PlayerInteraction] Created a missing HoldPoint under the player camera.");
                }
            }
            return true;
        }

        /// <summary>
        /// Repair a damaged/broken railing module. The invisible interaction trigger survives
        /// breakage, so the gap can still be targeted. Host validates range and owns the state.
        /// </summary>
        private bool TryRepairBarrier()
        {
            if (!RaycastFromCamera(out RaycastHit hit)) return false;
            var segment = hit.collider.GetComponentInParent<ShipBarrierSegment>();
            if (segment == null || !segment.NeedsRepair) return false;

            var nm = NetworkManagerP2P.Instance;
            if (nm != null)
                nm.RequestBarrierRepair(segment, transform.position);
            else
            {
                var system = segment.GetComponentInParent<ShipBarrierSystem>();
                if (system != null) system.TryRepair(segment.barrierId, transform.position);
            }
            return true;
        }

        /// <summary>
        /// If the interaction ray hits a ResourceNode, harvest one crate from it and return true.
        /// Routed through the network manager: the authority harvests immediately, a connected
        /// client asks the host and the crate appears with the HarvestResult packet.
        /// </summary>
        private bool TryHarvestNode()
        {
            if (!RaycastFromCamera(out RaycastHit hit)) return false;
            var node = hit.collider.GetComponentInParent<ResourceNode>();
            if (node == null) return false;
            if (node.IsDepleted) return true; // spent husk: swallow the press, no crate

            Vector3 forward = cameraTransform != null ? cameraTransform.forward : transform.forward;
            var nm = NetworkManagerP2P.Instance;
            if (nm != null)
                nm.RequestHarvest(node, transform.position, forward);
            else
                HarvestLocally(node); // scene running without a network manager
            return true;
        }

        private void HarvestLocally(ResourceNode node)
        {
            var gen = FindAnyObjectByType<WorldGenerator>();
            if (gen == null) return;

            string crateName = node.NextCargoName; // derive BEFORE consuming so the index matches
            Vector3 forward = cameraTransform != null ? cameraTransform.forward : transform.forward;
            Vector3 dropPos = ResourceNode.FindDropPoint(transform.position, forward, node.transform.position);

            if (!node.TryConsumeOne()) return;
            gen.SpawnCargoNamed(crateName, node.CargoCategory, dropPos);
            Debug.Log($"[PlayerInteraction] Harvested '{crateName}' ({node.nodeType}) — " +
                      $"{node.remainingYield}/{node.maxYield} left in node.");
        }

        /// <summary>If the interaction ray hits a hull barnacle, scrape it (host-validated via
        /// BarnacleSystem) and return true.</summary>
        private bool TryScrapeBarnacle()
        {
            if (!RaycastFromCamera(out RaycastHit hit)) return false;
            var barnacle = hit.collider.GetComponentInParent<Barnacle>();
            if (barnacle == null) return false;
            BarnacleSystem.RequestScrape(barnacle.id);
            return true;
        }

        /// <summary>F dispatch: deck lever (hold to work), ramp button, wheel (toggle helm), or
        /// map-table chart view — whichever the ray hits.</summary>
        private bool TryUseStation()
        {
            if (!RaycastFromCamera(out RaycastHit hit)) return false;

            var lever = hit.collider.GetComponentInParent<ShipDeckLever>();
            if (lever != null)
            {
                lever.BeginGrab(gameObject); // held while F stays down; no-op if already grabbed
                return true;
            }

            var rampButton = hit.collider.GetComponentInParent<ShipRampButton>();
            if (rampButton != null)
            {
                rampButton.Press();
                return true;
            }

            if (hit.collider.GetComponentInParent<ShipHelm>() != null)
            {
                if (NetworkManagerP2P.Instance != null)
                    NetworkManagerP2P.Instance.ToggleLocalHelm();
                return true;
            }

            var table = hit.collider.GetComponentInParent<ShipMapTable>();
            if (table != null)
            {
                table.ToggleView(gameObject);
                return true;
            }

            // Hub Sky Chart (expedition selection UI).
            var chart = hit.collider.GetComponentInParent<SkyChartTable>();
            if (chart != null)
            {
                chart.ToggleView(gameObject);
                return true;
            }

            // Return-to-Port bell on deck (host-validated via ExpeditionManager).
            var returnStation = hit.collider.GetComponentInParent<ShipReturnStation>();
            if (returnStation != null)
            {
                returnStation.Press();
                return true;
            }
            return false;
        }

        private bool RaycastFromCamera(out RaycastHit hit)
        {
            hit = default;
            if (cameraTransform == null) return false;
            Ray ray = new Ray(cameraTransform.position, cameraTransform.forward);
            return Physics.Raycast(ray, out hit, interactionDistance, interactionMask, QueryTriggerInteraction.Collide);
        }

        private void TryPickUp()
        {
            if (!RaycastFromCamera(out RaycastHit hit)) return;

            CargoItem item = hit.collider.GetComponentInParent<CargoItem>();
            if (item == null) return;

            // Light host-arbitration: an item already flagged held (locally or by a remote player
            // via the sync) can't be double-picked. Pickup itself stays optimistic-local for feel;
            // the host's heldCargoName sync remains the source of truth for conflicts.
            if (item.isHeld) return;
            if (!ResolveHoldPoint()) return;

            item.OnPickedUp(holdPoint);
            if (!item.isHeld) return;

            heldItem = item;
            Debug.Log($"[PlayerInteraction] Picked up '{item.itemName}' ({item.weight} kg).");
        }

        public void DropHeld()
        {
            if (heldItem == null) return;
            Debug.Log($"[PlayerInteraction] Dropped '{heldItem.itemName}'.");

            // Preserve walking/deck movement so the crate keeps travelling with its carrier,
            // then ensure it separates downward from the camera-height hold point immediately.
            CharacterController controller = GetComponent<CharacterController>();
            Vector3 releaseVelocity = controller != null ? controller.velocity : Vector3.zero;
            releaseVelocity.y = Mathf.Min(releaseVelocity.y, -dropDownwardSpeed);

            heldItem.OnDropped(releaseVelocity);
            heldItem = null;
        }
    }
}

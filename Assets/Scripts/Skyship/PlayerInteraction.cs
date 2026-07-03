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

        private CargoItem heldItem;
        public CargoItem HeldItem => heldItem;

        private void Awake()
        {
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
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
                // Harvest takes priority over pickup: aiming at a resource node breaks a crate off
                // it; otherwise fall through to normal cargo pickup.
                if (pickDropPressed && !TryHarvestNode())
                    TryPickUp();
            }
            else
            {
                if (pickDropPressed || dropClick)
                    DropHeld();
            }
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

            item.OnPickedUp(holdPoint);
            heldItem = item;
            Debug.Log($"[PlayerInteraction] Picked up '{item.itemName}' ({item.weight} kg).");
        }

        public void DropHeld()
        {
            if (heldItem == null) return;
            Debug.Log($"[PlayerInteraction] Dropped '{heldItem.itemName}'.");
            heldItem.OnDropped();
            heldItem = null;
        }
    }
}

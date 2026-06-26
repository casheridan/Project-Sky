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

            // E aimed at the steering wheel takes/releases the helm instead of touching cargo.
            if (pickDropPressed && TryToggleHelm())
                return;

            if (heldItem == null)
            {
                if (pickDropPressed)
                    TryPickUp();
            }
            else
            {
                if (pickDropPressed || dropClick)
                    DropHeld();
            }
        }

        /// <summary>If the interaction ray hits a ShipHelm, request the helm and return true.</summary>
        private bool TryToggleHelm()
        {
            if (!RaycastFromCamera(out RaycastHit hit)) return false;
            if (hit.collider.GetComponentInParent<ShipHelm>() == null) return false;

            if (NetworkManagerP2P.Instance != null)
                NetworkManagerP2P.Instance.ToggleLocalHelm();
            return true;
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

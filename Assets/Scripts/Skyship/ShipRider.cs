using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Carries a walking player smoothly with the moving/tilting ship deck WITHOUT
    /// parenting the CharacterController (parenting a CharacterController to a
    /// translating+rotating transform fights its own world-space Move and jitters).
    ///
    /// Instead, every LateUpdate — AFTER the ship has moved (ShipMovementController.Update)
    /// and tilted (ShipBalanceController.Update) this frame — we sample the deck's rigid
    /// motion at the point the player occupies and feed that delta to controller.Move.
    /// The CharacterController stays the sole authority over the player's world position,
    /// so gravity, grounding and walking input keep working unchanged.
    ///
    /// SCENE SETUP: add to the Player GameObject (alongside FirstPersonController).
    /// No references to wire — the deck is found by a short downward raycast.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class ShipRider : MonoBehaviour
    {
        [Header("Boarding")]
        [Tooltip("How far below the player to look for a ship deck collider.")]
        public float groundCheckDistance = 1.5f;

        [Header("Runtime State (read-only)")]
        [SerializeField] private bool isRiding;

        private CharacterController controller;
        private FirstPersonController fpc;

        private Transform deck;           // = ShipVisualRoot of the deck we stand on
        private Matrix4x4 prevDeckMatrix; // deck world matrix captured last frame
        private float prevDeckYaw;

        /// <summary>True while the player is standing on (and being carried by) a deck.</summary>
        public bool IsRiding => isRiding;

        /// <summary>The ShipVisualRoot we are riding, or null. Used by the networking send side.</summary>
        public Transform CurrentDeck => deck;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            fpc = GetComponent<FirstPersonController>();
        }

        private void LateUpdate()
        {
            // Don't carry while seated/piloting (player is parented to the seat and the
            // CharacterController is disabled) or whenever the controller is off.
            if (controller == null || !controller.enabled || (fpc != null && !fpc.allowMovement))
            {
                deck = null;
                isRiding = false;
                return;
            }

            Transform foundDeck = DetectDeck();

            // Boarding / leaving / switching decks: re-baseline so the first frame has no pop.
            if (foundDeck != deck)
            {
                deck = foundDeck;
                isRiding = deck != null;
                if (deck != null)
                {
                    prevDeckMatrix = deck.localToWorldMatrix;
                    prevDeckYaw = deck.rotation.eulerAngles.y;
                }
                return;
            }

            if (deck == null) return;

            // Where the player sat on the deck last frame, mapped to where that point is now.
            Vector3 localPos = prevDeckMatrix.inverse.MultiplyPoint3x4(transform.position);
            Vector3 newWorld = deck.localToWorldMatrix.MultiplyPoint3x4(localPos);
            Vector3 deckDelta = newWorld - transform.position;
            if (deckDelta.sqrMagnitude > 0f)
                controller.Move(deckDelta); // translation + yaw arc + tilt-slide in one shot

            // Turn the body with the ship's yaw only (never pitch/roll, so the camera
            // stays upright while the deck tilts beneath the player).
            float curYaw = deck.rotation.eulerAngles.y;
            float yawDelta = Mathf.DeltaAngle(prevDeckYaw, curYaw);
            if (Mathf.Abs(yawDelta) > 0.0001f)
                transform.Rotate(Vector3.up, yawDelta, Space.World);

            prevDeckMatrix = deck.localToWorldMatrix;
            prevDeckYaw = curYaw;
        }

        /// <summary>
        /// Short downward raycast; if it lands on any collider belonging to a ship
        /// (i.e. under a ShipBalanceController), return that ship's tilting ShipVisualRoot.
        /// Authority-agnostic (works on host and client) and needs no layers/triggers.
        /// </summary>
        private Transform DetectDeck()
        {
            Vector3 origin = transform.position + Vector3.up * 0.1f;
            var hits = Physics.RaycastAll(origin, Vector3.down, groundCheckDistance + 0.1f, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                var balance = hits[i].collider.GetComponentInParent<ShipBalanceController>();
                if (balance != null && balance.shipVisualRoot != null)
                    return balance.shipVisualRoot;
            }
            return null;
        }
    }
}

using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// The ship's steering wheel. A player presses F while looking at it to take the
    /// helm; only ONE player may pilot at a time (arbitrated by NetworkManagerP2P, which
    /// owns the authoritative currentPilotId). Taking the helm seats the local player at
    /// the PilotSeat anchor and locks their walking (mouse-look stays free), like sitting
    /// in a chair. Pressing F again releases the helm.
    ///
    /// This component only performs the LOCAL seat/unseat of the local player; the network
    /// claim/release lock lives in NetworkManagerP2P. PlayerInteraction routes the F press
    /// here via NetworkManagerP2P.ToggleLocalHelm().
    ///
    /// SCENE SETUP:
    ///  - Put this on a "SteeringWheel" GameObject parented under ShipVisualRoot at the
    ///    back top deck, with a (non-trigger) Collider the interaction ray can hit.
    ///  - Add a child empty "PilotSeat" posed where the pilot should stand and face.
    /// </summary>
    public class ShipHelm : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Where the pilot is placed while steering. Auto-finds a child named 'PilotSeat'.")]
        public Transform pilotSeat;

        [Header("Tuning")]
        [Tooltip("How far back from the seat the pilot is placed when they stand up.")]
        public float dismountDistance = 1.2f;

        [Header("Runtime State (read-only)")]
        [SerializeField] private bool isOccupied;

        public bool IsOccupied => isOccupied;

        private Transform savedParent;

        private void Awake()
        {
            if (pilotSeat == null)
            {
                Transform found = transform.Find("PilotSeat");
                pilotSeat = found != null ? found : transform;
            }

            // Deck stations, built procedurally (idempotent): the engine telegraph starboard of
            // the wheel, the spring-loaded lift lever port of it, and the boarding ramp + button
            // on the port deck edge amidships.
            ShipThrottleLever.CreateNear(transform);
            ShipLiftLever.CreateNear(transform);
            var balance = GetComponentInParent<ShipBalanceController>();
            if (balance != null)
            {
                ShipBoardingRamp.CreateOnShip(balance);
                ShipBarrierSystem.CreateOnShip(balance);
            }
        }

        /// <summary>Seat the local player at the helm and lock their walking (look stays on).</summary>
        public void Seat(GameObject player)
        {
            if (player == null) return;

            // CharacterController resists direct transform writes; disable it for the move.
            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            savedParent = player.transform.parent;

            // Parent to the seat (a child of ShipVisualRoot) so the pilot rides AND tilts with the ship.
            player.transform.SetParent(pilotSeat, true);
            player.transform.localPosition = Vector3.zero;
            player.transform.localRotation = Quaternion.identity;

            // Don't keep cargo floating in hand while steering.
            var interaction = player.GetComponent<PlayerInteraction>();
            if (interaction != null && interaction.HeldItem != null)
                interaction.DropHeld();

            // Stops walking + disables the CharacterController, but keeps mouse-look running.
            var controller = player.GetComponent<FirstPersonController>();
            if (controller != null) controller.allowMovement = false;

            isOccupied = true;
        }

        /// <summary>Release the local player from the helm and stand them up beside the wheel.</summary>
        public void Unseat(GameObject player)
        {
            if (player == null) return;

            var cc = player.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;

            player.transform.SetParent(savedParent, true);

            Transform anchor = pilotSeat != null ? pilotSeat : transform;
            player.transform.position = anchor.position - anchor.forward * dismountDistance + Vector3.up * 0.1f;

            // Re-enables movement + the CharacterController on the next FirstPersonController.Update.
            var controller = player.GetComponent<FirstPersonController>();
            if (controller != null) controller.allowMovement = true;

            isOccupied = false;
        }
    }
}

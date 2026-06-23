using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// Placeholder flight model. Moves the ShipRoot forward and turns it, scaled
    /// by the handling modifiers from ShipBalanceController so the ship becomes
    /// slower and harder to steer as it loads up / goes off-balance.
    ///
    /// Pressing the TAB key toggles between walking around (first-person controller)
    /// and piloting the ship. When piloting, player movement and interaction are disabled,
    /// and the player is parented to the ship deck so they ride with it perfectly.
    ///
    /// SCENE SETUP:
    ///  - Add to ShipRoot (same object as ShipBalanceController).
    ///  - 'balance' auto-binds to the controller on the same object if left empty.
    ///  - Leave 'playerController' and 'playerInteraction' empty to auto-detect the player.
    /// </summary>
    public class ShipMovementController : MonoBehaviour
    {
        [Header("References")]
        public ShipBalanceController balance;

        [Header("Piloting Setup")]
        [Tooltip("The player walking controller. Automatically found if left empty.")]
        public FirstPersonController playerController;

        [Tooltip("The player interaction script. Automatically found if left empty.")]
        public PlayerInteraction playerInteraction;

        [Header("Base Handling")]
        public float baseSpeed = 4f;
        public float baseTurnSpeed = 30f;

        [Header("Runtime State (read-only)")]
        [Tooltip("Whether the ship is currently being piloted. Toggle with Tab key.")]
        public bool inputEnabled = false;

        private float throttle; // -1..1
        private float steer;    // -1..1
        private Transform originalPlayerParent;

        private void Awake()
        {
            if (balance == null)
                balance = GetComponent<ShipBalanceController>();
        }

        private void Start()
        {
            // Auto-detect player scripts if not manually assigned
            if (playerController == null)
                playerController = Object.FindAnyObjectByType<FirstPersonController>();
            if (playerInteraction == null)
                playerInteraction = Object.FindAnyObjectByType<PlayerInteraction>();

            // Sync initial state on start (e.g. if player is active, ship flight input is disabled)
            SyncControllerStates();
        }

        private void Update()
        {
            // Read Tab key to toggle between walking and piloting modes
            var k = Keyboard.current;
            if (k != null && k.tabKey.wasPressedThisFrame)
            {
                TogglePiloting();
            }

            if (inputEnabled)
                ReadTestInput();

            float speedMult = balance != null ? balance.speedMultiplier : 1f;
            float turnPull = balance != null ? balance.turnPull : 0f;

            // Forward/back movement, scaled by load.
            transform.position += transform.forward * (throttle * baseSpeed * speedMult * Time.deltaTime);

            // Steering = pilot steer (scaled by load) + automatic pull toward the heavy side (only when moving/powered).
            float yaw = steer * baseTurnSpeed * speedMult + (turnPull * Mathf.Abs(throttle));
            transform.Rotate(Vector3.up, yaw * Time.deltaTime, Space.World);
        }

        private void ReadTestInput()
        {
            throttle = 0f;
            steer = 0f;
            var k = Keyboard.current;
            if (k == null) return;
            if (k.wKey.isPressed) throttle += 1f;
            if (k.sKey.isPressed) throttle -= 1f;
            if (k.dKey.isPressed) steer += 1f;
            if (k.aKey.isPressed) steer -= 1f;
        }

        /// <summary>
        /// Switch between walk and pilot mode, updating component states and parenting.
        /// </summary>
        public void TogglePiloting()
        {
            inputEnabled = !inputEnabled;
            SyncControllerStates();
            Debug.Log($"[ShipMovementController] Mode toggled! Piloting: {inputEnabled}");
        }

        private void SyncControllerStates()
        {
            if (playerController != null)
            {
                // Disable walking/jumping but keep FirstPersonController enabled for mouse looking
                playerController.allowMovement = !inputEnabled;

                if (inputEnabled)
                {
                    // Save original player hierarchy parent
                    originalPlayerParent = playerController.transform.parent;

                    // Parent the player to the ship visual deck (or ship root) so they ride with it as it moves & tilts
                    Transform rideParent = balance != null && balance.shipVisualRoot != null 
                        ? balance.shipVisualRoot 
                        : transform;

                    playerController.transform.SetParent(rideParent);
                }
                else
                {
                    // Restore original player hierarchy parent
                    playerController.transform.SetParent(originalPlayerParent);
                }
            }

            if (playerInteraction != null)
            {
                // Drop any held cargo before piloting so it doesn't float in our hand while steering
                if (inputEnabled && playerInteraction.HeldItem != null)
                {
                    playerInteraction.DropHeld();
                }

                // Disable looking/picking up cargo while piloting
                playerInteraction.enabled = !inputEnabled;
            }

            // Always reset movement state when pilot controls are turned off
            if (!inputEnabled)
            {
                throttle = 0f;
                steer = 0f;
            }
        }

        // --- Public API for external seat / UI triggers ---
        public void SetThrottle(float value) => throttle = Mathf.Clamp(value, -1f, 1f);
        public void SetSteer(float value) => steer = Mathf.Clamp(value, -1f, 1f);
    }
}

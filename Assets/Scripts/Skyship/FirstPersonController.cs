using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// Simple, stable first-person controller using CharacterController and the
    /// new Input System (direct device polling for prototype simplicity, so no
    /// .inputactions asset is required). Kept deliberately minimal so it is easy
    /// to replace with a fancier controller later.
    ///
    /// SCENE SETUP:
    ///  - Create an empty GameObject "Player".
    ///  - Add a CharacterController (Center Y ~1, Height ~2, Radius ~0.4).
    ///  - Add this component.
    ///  - Create a child Camera named "PlayerCamera" at local pos (0, 1.7, 0) and
    ///    assign it to 'cameraTransform'. Tag it MainCamera (delete the default one).
    ///  - Create an empty child of the CAMERA named "HoldPoint" at local pos
    ///    (0, -0.3, 1.2); PlayerInteraction uses it as where held cargo sits.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class FirstPersonController : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("Child camera used for pitch (looking up/down). Required.")]
        public Transform cameraTransform;

        [Header("Movement")]
        public float moveSpeed = 5f;
        public float jumpForce = 5f;
        public float gravity = -20f;

        [Header("Look")]
        [Tooltip("Mouse look sensitivity (degrees per unit of mouse delta).")]
        public float lookSensitivity = 0.1f;
        public float minPitch = -89f;
        public float maxPitch = 89f;

        [Header("Options")]
        public bool enableJump = true;
        public bool lockCursor = true;

        [Header("Runtime Toggles")]
        [Tooltip("If false, disables CharacterController and movement inputs but preserves mouse looking.")]
        public bool allowMovement = true;

        private CharacterController controller;
        private float verticalVelocity;
        private float pitch;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            if (cameraTransform == null && Camera.main != null)
                cameraTransform = Camera.main.transform;
        }

        private void Start()
        {
            if (lockCursor)
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void Update()
        {
            HandleLook();

            if (allowMovement)
            {
                if (controller != null && !controller.enabled)
                    controller.enabled = true;
                HandleMove();
            }
            else
            {
                if (controller != null && controller.enabled)
                    controller.enabled = false;
                verticalVelocity = 0f;
            }
        }

        private void HandleLook()
        {
            if (Mouse.current == null || cameraTransform == null) return;

            Vector2 delta = Mouse.current.delta.ReadValue();

            // Yaw rotates the whole body; pitch rotates only the camera.
            transform.Rotate(Vector3.up, delta.x * lookSensitivity, Space.Self);

            pitch -= delta.y * lookSensitivity;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
            cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void HandleMove()
        {
            Vector2 input = ReadMoveInput();
            Vector3 move = transform.right * input.x + transform.forward * input.y;
            if (move.sqrMagnitude > 1f) move.Normalize();

            if (controller.isGrounded)
            {
                verticalVelocity = -1f; // small downward bias keeps us grounded
                if (enableJump && Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
                    verticalVelocity = jumpForce;
            }
            else
            {
                verticalVelocity += gravity * Time.deltaTime;
            }

            Vector3 velocity = move * moveSpeed + Vector3.up * verticalVelocity;
            controller.Move(velocity * Time.deltaTime);
        }

        private Vector2 ReadMoveInput()
        {
            Vector2 v = Vector2.zero;
            var k = Keyboard.current;
            if (k == null) return v;
            if (k.wKey.isPressed) v.y += 1f;
            if (k.sKey.isPressed) v.y -= 1f;
            if (k.dKey.isPressed) v.x += 1f;
            if (k.aKey.isPressed) v.x -= 1f;
            return v;
        }
    }
}

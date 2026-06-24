using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// In-game pause menu. Press Escape to toggle. While open it frees the cursor and
    /// suspends LOCAL control (look, walk, interact, piloting), then offers
    /// Resume / Return to Main Menu / Quit.
    ///
    /// Drawn with IMGUI so it needs zero scene UI wiring — just drop this component on a
    /// GameObject in the gameplay scene. It deliberately does NOT touch Time.timeScale,
    /// because the session is networked and other players keep playing.
    /// </summary>
    public class PauseMenuManager : MonoBehaviour
    {
        [Tooltip("Scene loaded when returning to the main menu.")]
        public string mainMenuSceneName = "MainMenuScene";

        [Header("Runtime State (read-only)")]
        public bool isPaused;

        private FirstPersonController playerController;
        private PlayerInteraction playerInteraction;
        private ShipMovementController shipMovement;
        private NetworkManagerP2P networkManager;

        private void Start()
        {
            playerController = Object.FindAnyObjectByType<FirstPersonController>();
            playerInteraction = Object.FindAnyObjectByType<PlayerInteraction>();
            shipMovement = Object.FindAnyObjectByType<ShipMovementController>();
            networkManager = Object.FindAnyObjectByType<NetworkManagerP2P>();
        }

        private void Update()
        {
            var k = Keyboard.current;
            if (k != null && k.escapeKey.wasPressedThisFrame)
                TogglePause();
        }

        public void TogglePause()
        {
            isPaused = !isPaused;

            // Suspend local control while paused. Disabling FirstPersonController also stops
            // mouse-look, so the camera doesn't spin while clicking the menu.
            if (playerController != null) playerController.enabled = !isPaused;
            if (playerInteraction != null) playerInteraction.enabled = !isPaused;
            if (shipMovement != null) shipMovement.enabled = !isPaused;

            // Free the cursor so the player can click the menu; re-lock on resume.
            Cursor.lockState = isPaused ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = isPaused;
        }

        private void OnGUI()
        {
            if (!isPaused) return;

            const float w = 320f, h = 240f;
            float x = (Screen.width - w) * 0.5f;
            float y = (Screen.height - h) * 0.5f;

            GUILayout.BeginArea(new Rect(x, y, w, h), "PAUSED", GUI.skin.box);
            GUILayout.Space(30);

            if (GUILayout.Button("Resume", GUILayout.Height(45)))
                TogglePause();

            GUILayout.Space(12);

            if (GUILayout.Button("Return to Main Menu", GUILayout.Height(45)))
                ReturnToMainMenu();

            GUILayout.Space(12);

            if (GUILayout.Button("Quit Game", GUILayout.Height(45)))
                QuitGame();

            GUILayout.EndArea();
        }

        public void ReturnToMainMenu()
        {
            isPaused = false;

            // The gameplay scene's network manager is torn down before we leave.
            if (networkManager != null && networkManager.isConnected)
                networkManager.ShutdownNetwork();

            // Restore a normal cursor for the menu (the menu scene doesn't lock it).
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            SceneManager.LoadScene(mainMenuSceneName);
        }

        public void QuitGame()
        {
            Debug.Log("[PauseMenu] Quitting game...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}

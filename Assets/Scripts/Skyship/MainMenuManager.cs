using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Skyship
{
    /// <summary>
    /// Coordinates the Canvas-based Main Menu, Lobby, Settings, and Join panels.
    /// Integrates directly with NetworkManagerP2P to support hosting, joining,
    /// and starting the multiplayer game over UDP!
    /// </summary>
    public class MainMenuManager : MonoBehaviour
    {
        [Header("UI Panels")]
        public GameObject mainMenuPanel;
        public GameObject lobbyPanel;
        public GameObject settingsPanel;
        public GameObject joinPanel;

        [Header("Lobby Panel Controls")]
        public Text lobbyStatusText;
        public Text playerListText;
        public Button startGameButton;

        [Header("Join Panel Controls")]
        public InputField ipInputField;

        [Header("Settings Controls")]
        public Slider volumeSlider;
        public Slider sensitivitySlider;

        [Header("Gameplay Transition")]
        [Tooltip("The name of the gameplay scene to load when starting.")]
        public string gameplaySceneName = "MainGameScene";

        private NetworkManagerP2P networkManager;
        private static MainMenuManager instance;

        public static MainMenuManager Instance => instance;

        private void Awake()
        {
            instance = this;

            // Find NetworkManagerP2P on the same GameObject or search the scene
            networkManager = GetComponent<NetworkManagerP2P>();
            if (networkManager == null)
            {
                networkManager = UnityEngine.Object.FindAnyObjectByType<NetworkManagerP2P>();
            }
            if (networkManager == null)
            {
                networkManager = gameObject.AddComponent<NetworkManagerP2P>();
            }
        }

        private void Start()
        {
            // Reset to main menu on launch
            ShowPanel(mainMenuPanel);

            // Load saved settings if any
            if (volumeSlider != null)
                volumeSlider.value = PlayerPrefs.GetFloat("Volume", 1.0f);
            if (sensitivitySlider != null)
                sensitivitySlider.value = PlayerPrefs.GetFloat("Sensitivity", 1.0f);

            // Hide start button by default; only active for host
            if (startGameButton != null)
                startGameButton.gameObject.SetActive(false);
        }

        private void Update()
        {
            // If we are currently inside the Lobby panel, keep status/players text updated
            if (lobbyPanel != null && lobbyPanel.activeSelf && networkManager != null)
            {
                UpdateLobbyUI();
            }
        }

        // ==========================================
        // PANEL NAVIGATION
        // ==========================================

        public void ShowPanel(GameObject targetPanel)
        {
            if (mainMenuPanel != null) mainMenuPanel.SetActive(mainMenuPanel == targetPanel);
            if (lobbyPanel != null) lobbyPanel.SetActive(lobbyPanel == targetPanel);
            if (settingsPanel != null) settingsPanel.SetActive(settingsPanel == targetPanel);
            if (joinPanel != null) joinPanel.SetActive(joinPanel == targetPanel);
        }

        public void OnClickHost()
        {
            if (networkManager != null)
            {
                networkManager.StartHost();
                ShowPanel(lobbyPanel);

                if (startGameButton != null)
                    startGameButton.gameObject.SetActive(true); // Host can start
            }
        }

        public void OnClickJoinMenu()
        {
            ShowPanel(joinPanel);
        }

        public void OnClickConnect()
        {
            if (networkManager != null)
            {
                string ip = "127.0.0.1";
                if (ipInputField != null && !string.IsNullOrEmpty(ipInputField.text))
                {
                    ip = ipInputField.text;
                }
                networkManager.remoteIp = ip;
                networkManager.StartClient();
                ShowPanel(lobbyPanel);

                if (startGameButton != null)
                    startGameButton.gameObject.SetActive(false); // Client cannot start
            }
        }

        public void OnClickSettings()
        {
            ShowPanel(settingsPanel);
        }

        public void OnClickBackToMain()
        {
            // Shut down network if we leave the lobby/join screen
            if (networkManager != null && networkManager.isConnected)
            {
                networkManager.ShutdownNetwork();
            }

            ShowPanel(mainMenuPanel);
        }

        public void OnClickExit()
        {
            Debug.Log("[MainMenu] Exiting game...");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ==========================================
        // LOBBY CONTROL
        // ==========================================

        private void UpdateLobbyUI()
        {
            if (networkManager == null) return;

            if (networkManager.isHost)
            {
                lobbyStatusText.text = "Lobby Status: HOSTING";

                var roster = new System.Text.StringBuilder();
                roster.AppendLine($"• {networkManager.localPlayerId} (You - Host)");

                // Show players that have actually joined (tracked by the network manager).
                foreach (string id in networkManager.connectedPlayerIds)
                    roster.AppendLine($"• {id}");

                // Fill the remaining slots up to the 4-player cap with waiting placeholders.
                int filledSlots = 1 + networkManager.connectedPlayerIds.Count;
                for (int slot = filledSlots + 1; slot <= 4; slot++)
                    roster.AppendLine($"• Slot {slot}: Waiting for player...");

                roster.AppendLine();
                roster.AppendLine("[Steam Invite Placeholder]");
                roster.Append("[Discord Invite Placeholder]");

                playerListText.text = roster.ToString();
            }
            else
            {
                lobbyStatusText.text = networkManager.isConnected ? "Lobby Status: CONNECTED" : "Lobby Status: CONNECTING...";
                playerListText.text = $"• {networkManager.localPlayerId} (You)\n• Connected to Host\n\n[Steam Invite Placeholder]\n[Discord Invite Placeholder]";
            }
        }

        public void OnClickStartGame()
        {
            if (networkManager != null && networkManager.isHost)
            {
                Debug.Log("[MainMenu] Host starting game...");
                
                // Notify all connected clients to load the gameplay scene too
                networkManager.SendStartGameNotice(gameplaySceneName);
                
                // Load gameplay scene directly
                SceneManager.LoadScene(gameplaySceneName);
            }
        }

        // ==========================================
        // SETTINGS CONTROL
        // ==========================================

        public void OnVolumeChanged()
        {
            if (volumeSlider != null)
            {
                PlayerPrefs.SetFloat("Volume", volumeSlider.value);
                AudioListener.volume = volumeSlider.value;
            }
        }

        public void OnSensitivityChanged()
        {
            if (sensitivitySlider != null)
            {
                PlayerPrefs.SetFloat("Sensitivity", sensitivitySlider.value);
            }
        }
    }
}
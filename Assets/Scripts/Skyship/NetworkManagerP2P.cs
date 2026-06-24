using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Pure C# UDP socket-based Peer-to-Peer network manager.
    /// ZERO external dependencies. Works in any plain Unity project out of the box.
    /// Handles syncing up to 4 players total (Host + 3 peers).
    /// Synchronizes:
    ///   - Player positions, rotations, and piloting states
    ///   - Cargo item pickup, dropping, and smooth physics positioning
    ///   - Ship root positions, rotations, and visual tilt angles
    ///
    /// Uses thread-safe queues to marshal received socket data safely to the Unity main thread.
    /// </summary>
    public class NetworkManagerP2P : MonoBehaviour
    {
        [System.Serializable]
        public class PlayerNetworkData
        {
            public string id;
            public Vector3 position;
            public Quaternion rotation;
            public bool isPiloting;
            public string heldCargoName; // name of cargo box currently held (empty if none)
        }

        [System.Serializable]
        public class CargoNetworkData
        {
            public string name;
            public Vector3 position;
            public Quaternion rotation;
        }

        [System.Serializable]
        public class ShipNetworkData
        {
            public Vector3 position;
            public Quaternion rotation;
            public Quaternion visualTilt;
        }

        [System.Serializable]
        public class NetworkPacket
        {
            public string senderId;
            public string packetType; // "Handshake", "Disconnect", "State"
            public List<PlayerNetworkData> players = new List<PlayerNetworkData>();
            public List<CargoNetworkData> cargo = new List<CargoNetworkData>();
            public ShipNetworkData ship = new ShipNetworkData();
        }

        [Header("Connection Tuning")]
        public int localPort = 7777;
        public string remoteIp = "127.0.0.1";
        public int remotePort = 7777;
        public float updateRate = 0.05f; // 20 updates per second
        [Tooltip("If true, draws the legacy IMGUI connection HUD in the corner of the screen.")]
        public bool showImguiDebugHUD = false;

        [Header("Puppet Prefab Placeholder")]
        [Tooltip("Material given to remote player puppet spheres.")]
        public Color puppetColor = Color.orange;

        [Header("Runtime Connection Info (read-only)")]
        public bool isHost;
        public bool isConnected;
        public string localPlayerId;

        // Host-side roster of connected client player IDs (for lobby UI; populated from incoming packets).
        [System.NonSerialized] public List<string> connectedPlayerIds = new List<string>();

        private UdpClient udpSocket;
        private Thread receiveThread;
        private bool isRunning;
        private float nextSendTime;

        // Thread-safe queue to pass received JSON data to Unity main thread
        private ConcurrentQueue<string> incomingPackets = new ConcurrentQueue<string>();

        // Connected peers (IP & Port endpoints) - Host only
        private HashSet<IPEndPoint> connectedPeers = new HashSet<IPEndPoint>();
        private IPEndPoint hostEndPoint; // Client only

        // Local states
        private GameObject localPlayer;
        private Transform shipTransform;
        private Transform shipVisualRoot;
        private ShipMovementController shipMovement;
        private ShipBalanceController shipBalance;

        // Remote representations in our local scene
        private Dictionary<string, GameObject> remotePuppets = new Dictionary<string, GameObject>();
        private Dictionary<string, CargoItem> localCargoItems = new Dictionary<string, CargoItem>();

        private void Awake()
        {
            localPlayerId = "Player_" + UnityEngine.Random.Range(1000, 9999);
            localPlayer = GameObject.Find("Player");
            
            var ship = GameObject.Find("ShipRoot");
            if (ship != null)
            {
                shipTransform = ship.transform;
                shipMovement = ship.GetComponent<ShipMovementController>();
                shipBalance = ship.GetComponent<ShipBalanceController>();
                if (shipBalance != null)
                    shipVisualRoot = shipBalance.shipVisualRoot;
            }

            // Cache cargo items by name for fast lookup
            var items = GameObject.FindObjectsByType<CargoItem>(FindObjectsInactive.Exclude);
            foreach (var item in items)
            {
                localCargoItems[item.name] = item;
            }
        }

        private void Update()
        {
            // 1. Process received data from queue on Unity main thread
            while (incomingPackets.TryDequeue(out string json))
            {
                try
                {
                    NetworkPacket packet = JsonUtility.FromJson<NetworkPacket>(json);
                    if (packet != null && packet.senderId != localPlayerId)
                    {
                        ProcessPacket(packet);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[NetworkManager] Packet processing error: " + e.Message);
                }
            }

            // 2. Periodically send local state to connected peers
            if (isConnected && Time.time >= nextSendTime)
            {
                nextSendTime = Time.time + updateRate;
                SendState();
            }
        }

        private void OnApplicationQuit()
        {
            ShutdownNetwork();
        }

        private void OnDestroy()
        {
            ShutdownNetwork();
        }

        // ==========================================
        // 1. NETWORK CONTROLS & MANAGEMENT
        // ==========================================

        public void StartHost()
        {
            ShutdownNetwork();
            try
            {
                udpSocket = new UdpClient(localPort);
                isHost = true;
                isConnected = true;
                isRunning = true;

                receiveThread = new Thread(ReceiveThreadLoop);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                Debug.Log($"[NetworkManager] Host started on port {localPort}. My ID is {localPlayerId}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetworkManager] Failed to start Host on port {localPort}: {e.Message}");
            }
        }

        public void StartClient()
        {
            ShutdownNetwork();
            try
            {
                // Let OS allocate a random free local port for client
                udpSocket = new UdpClient(0);
                isHost = false;
                isRunning = true;

                hostEndPoint = new IPEndPoint(IPAddress.Parse(remoteIp), remotePort);

                receiveThread = new Thread(ReceiveThreadLoop);
                receiveThread.IsBackground = true;
                receiveThread.Start();

                // Send Handshake
                NetworkPacket handshake = new NetworkPacket
                {
                    senderId = localPlayerId,
                    packetType = "Handshake"
                };
                SendPacketDirect(handshake, hostEndPoint);

                isConnected = true;
                Debug.Log($"[NetworkManager] Connecting to host {remoteIp}:{remotePort} as {localPlayerId}...");
            }
            catch (Exception e)
            {
                Debug.LogError($"[NetworkManager] Failed to initialize client socket: {e.Message}");
            }
        }

        public void ShutdownNetwork()
        {
            isRunning = false;

            if (udpSocket != null)
            {
                if (isConnected)
                {
                    // Send disconnect notice
                    NetworkPacket dc = new NetworkPacket { senderId = localPlayerId, packetType = "Disconnect" };
                    if (isHost)
                        BroadcastPacket(dc);
                    else if (hostEndPoint != null)
                        SendPacketDirect(dc, hostEndPoint);
                }
                udpSocket.Close();
                udpSocket = null;
            }

            if (receiveThread != null && receiveThread.IsAlive)
            {
                receiveThread.Join(500);
            }

            // Destroy puppets
            foreach (var kvp in remotePuppets)
            {
                if (kvp.Value != null)
                    Destroy(kvp.Value);
            }
            remotePuppets.Clear();
            connectedPeers.Clear();
            connectedPlayerIds.Clear();

            isHost = false;
            isConnected = false;
            Debug.Log("[NetworkManager] Network shut down.");
        }

        // ==========================================
        // 2. SENDING DATA & MULTICAST
        // ==========================================

        private void SendState()
        {
            NetworkPacket statePacket = new NetworkPacket
            {
                senderId = localPlayerId,
                packetType = "State"
            };

            // Include local player data
            if (localPlayer != null)
            {
                var pInteraction = localPlayer.GetComponent<PlayerInteraction>();
                bool isPiloting = shipMovement != null && shipMovement.inputEnabled;

                statePacket.players.Add(new PlayerNetworkData
                {
                    id = localPlayerId,
                    position = localPlayer.transform.position,
                    rotation = localPlayer.transform.rotation,
                    isPiloting = isPiloting,
                    heldCargoName = (pInteraction != null && pInteraction.HeldItem != null) ? pInteraction.HeldItem.name : ""
                });
            }

            // Host is responsible for syncing loose cargo and ship kinematics
            if (isHost)
            {
                // Sync ship positions & tilts
                if (shipTransform != null)
                {
                    statePacket.ship.position = shipTransform.position;
                    statePacket.ship.rotation = shipTransform.rotation;
                    if (shipVisualRoot != null)
                        statePacket.ship.visualTilt = shipVisualRoot.localRotation;
                }

                // Sync cargo box coordinates
                foreach (var kvp in localCargoItems)
                {
                    CargoItem item = kvp.Value;
                    if (item == null) continue;

                    // Only send sync for loose cargo (not currently held by clients/host)
                    if (!item.isHeld)
                    {
                        statePacket.cargo.Add(new CargoNetworkData
                        {
                            name = item.name,
                            position = item.transform.position,
                            rotation = item.transform.rotation
                        });
                    }
                }

                BroadcastPacket(statePacket);
            }
            else
            {
                // Clients send state only to Host
                if (hostEndPoint != null)
                {
                    SendPacketDirect(statePacket, hostEndPoint);
                }
            }
        }

        private void BroadcastPacket(NetworkPacket packet)
        {
            foreach (var peer in connectedPeers)
            {
                SendPacketDirect(packet, peer);
            }
        }

        /// <summary>
        /// Tells all connected clients to load the specified gameplay scene.
        /// </summary>
        public void SendStartGameNotice(string sceneName)
        {
            if (isHost && isConnected)
            {
                NetworkPacket startPacket = new NetworkPacket
                {
                    senderId = sceneName, // use senderId to pass the scene name
                    packetType = "StartGame"
                };
                BroadcastPacket(startPacket);
            }
        }

        private void SendPacketDirect(NetworkPacket packet, IPEndPoint target)
        {
            if (udpSocket == null || !isRunning) return;

            try
            {
                string json = JsonUtility.ToJson(packet);
                byte[] bytes = Encoding.UTF8.GetBytes(json);
                udpSocket.Send(bytes, bytes.Length, target);
            }
            catch (Exception)
            {
                // silent socket send failures during teardowns
            }
        }

        // ==========================================
        // 3. RECEIVE LOOP & PACKET DRAIN (Threaded)
        // ==========================================

        private void ReceiveThreadLoop()
        {
            IPEndPoint senderEP = new IPEndPoint(IPAddress.Any, 0);

            while (isRunning && udpSocket != null)
            {
                try
                {
                    byte[] bytes = udpSocket.Receive(ref senderEP);
                    if (bytes != null && bytes.Length > 0)
                    {
                        string json = Encoding.UTF8.GetString(bytes);

                        // Host tracks sender's endpoint for peer mapping
                        if (isHost)
                        {
                            connectedPeers.Add(senderEP);
                        }

                        incomingPackets.Enqueue(json);
                    }
                }
                catch (SocketException)
                {
                    // standard socket shutdown or timeout exception
                    break;
                }
                catch (Exception e)
                {
                    if (isRunning)
                        Debug.LogWarning("[NetworkManager] Receive thread error: " + e.Message);
                    break;
                }
            }
        }

        // ==========================================
        // 4. MAIN THREAD DATA PROCESSING & STATE SYNC
        // ==========================================

        private void ProcessPacket(NetworkPacket packet)
        {
            if (packet.packetType == "Disconnect")
            {
                connectedPlayerIds.Remove(packet.senderId);
                DestroyPuppet(packet.senderId);
                return;
            }

            if (packet.packetType == "StartGame")
            {
                Debug.Log("[NetworkClient] Received StartGame notice from Host. Loading gameplay scene...");
                // The host sends the scene name in the senderId field (or defaults to MainGameScene)
                string sceneName = string.IsNullOrEmpty(packet.senderId) ? "MainGameScene" : packet.senderId;
                UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
                return;
            }

            // Host tracks connected client IDs so the lobby roster updates the moment a player joins
            // (works off any packet's senderId, so it fires on the Handshake and every State update).
            if (isHost && !connectedPlayerIds.Contains(packet.senderId))
            {
                connectedPlayerIds.Add(packet.senderId);
                Debug.Log($"[NetworkManager] Player joined lobby: {packet.senderId}");
            }

            // Sync other player positions & pickups
            foreach (var pData in packet.players)
            {
                if (pData.id == localPlayerId) continue; // safety skip

                GameObject puppet = GetOrCreatePuppet(pData.id);
                
                // Interpolate or smoothly snap puppet position/rotation
                // If remote is piloting, they ride on the ship; let's snap them to the parent transform if parented
                puppet.transform.position = Vector3.Lerp(puppet.transform.position, pData.position, 0.4f);
                puppet.transform.rotation = Quaternion.Slerp(puppet.transform.rotation, pData.rotation, 0.4f);

                // Handle visual parent of the cargo if remote is holding it
                SyncRemoteCargoHold(pData.id, pData.heldCargoName);
            }

            // Client-only: sync ship transform and visual deck tilt from host
            if (!isHost && isConnected)
            {
                if (shipTransform != null)
                {
                    shipTransform.position = Vector3.Lerp(shipTransform.position, packet.ship.position, 0.4f);
                    shipTransform.rotation = Quaternion.Slerp(shipTransform.rotation, packet.ship.rotation, 0.4f);
                }

                if (shipVisualRoot != null)
                {
                    shipVisualRoot.localRotation = Quaternion.Slerp(shipVisualRoot.localRotation, packet.ship.visualTilt, 0.4f);
                }

                // Client-only: sync loose cargo physics coords from host
                foreach (var cData in packet.cargo)
                {
                    if (localCargoItems.TryGetValue(cData.name, out CargoItem item))
                    {
                        if (item != null && !item.isHeld)
                        {
                            // Temporarily set kinematic to let other players see clean physics positions on clients
                            Rigidbody rb = item.Body;
                            if (rb != null && !rb.isKinematic)
                            {
                                rb.isKinematic = true;
                            }

                            item.transform.position = Vector3.Lerp(item.transform.position, cData.position, 0.4f);
                            item.transform.rotation = Quaternion.Slerp(item.transform.rotation, cData.rotation, 0.4f);
                        }
                    }
                }
            }
        }

        private GameObject GetOrCreatePuppet(string id)
        {
            if (remotePuppets.TryGetValue(id, out GameObject puppet))
            {
                if (puppet != null) return puppet;
            }

            // Create a clean primitive sphere representing the remote player
            GameObject pGO = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pGO.name = "Puppet_" + id;
            
            // Remove collider so they don't push local players
            var col = pGO.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Add a smaller child cylinder representing a head visor for orientation direction
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visor.name = "Visor";
            visor.transform.SetParent(pGO.transform);
            visor.transform.localPosition = new Vector3(0f, 0.2f, 0.4f);
            visor.transform.localScale = new Vector3(0.2f, 0.2f, 0.4f);
            visor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var vCol = visor.GetComponent<Collider>();
            if (vCol != null) Destroy(vCol);

            // Paint puppet orange
            var renderer = pGO.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material m = new Material(Shader.Find("Standard"));
                m.color = puppetColor;
                renderer.sharedMaterial = m;
            }

            var visorRenderer = visor.GetComponent<Renderer>();
            if (visorRenderer != null)
            {
                Material m = new Material(Shader.Find("Standard"));
                m.color = Color.black;
                visorRenderer.sharedMaterial = m;
            }

            remotePuppets[id] = pGO;
            Debug.Log($"[NetworkManager] Spawned remote player puppet for client {id}.");
            return pGO;
        }

        private void DestroyPuppet(string id)
        {
            if (remotePuppets.TryGetValue(id, out GameObject puppet))
            {
                if (puppet != null) Destroy(puppet);
                remotePuppets.Remove(id);
                Debug.Log($"[NetworkManager] Removed client puppet {id}.");
            }
        }

        private void SyncRemoteCargoHold(string playerId, string heldCargoName)
        {
            if (string.IsNullOrEmpty(heldCargoName)) return;

            // Find the item
            if (localCargoItems.TryGetValue(heldCargoName, out CargoItem item))
            {
                if (item == null) return;

                if (remotePuppets.TryGetValue(playerId, out GameObject puppet))
                {
                    if (puppet != null)
                    {
                        // Set the cargo item kinematic so it follows the puppet hand
                        Rigidbody rb = item.Body;
                        if (rb != null)
                        {
                            rb.isKinematic = true;
                            rb.useGravity = false;
                        }

                        var itemCol = item.GetComponent<Collider>();
                        if (itemCol != null) itemCol.enabled = false;

                        item.transform.SetParent(puppet.transform);
                        item.transform.localPosition = new Vector3(0f, 0f, 1.2f); // hold position in front of puppet
                        item.transform.localRotation = Quaternion.identity;
                        item.isHeld = true;
                    }
                }
            }
        }

        // ==========================================
        // 5. IMGUI ON-SCREEN CONNECTION HUD Overlay
        // ==========================================

        private void OnGUI()
        {
            if (!showImguiDebugHUD) return;

            // Place network overlay top-right corner
            GUILayout.BeginArea(new Rect(Screen.width - 240, 10, 220, 250), "P2P Network Lobby", GUI.skin.box);
            GUILayout.Space(20);

            if (!isConnected)
            {
                GUILayout.Label($"My ID: {localPlayerId}");
                GUILayout.Space(5);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Port:", GUILayout.Width(50));
                string pStr = GUILayout.TextField(localPort.ToString(), 5);
                int.TryParse(pStr, out localPort);
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Host Session"))
                {
                    StartHost();
                }

                GUILayout.Space(10);
                GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
                GUILayout.Space(5);

                GUILayout.BeginHorizontal();
                GUILayout.Label("Host IP:", GUILayout.Width(60));
                remoteIp = GUILayout.TextField(remoteIp, 15);
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Join Session"))
                {
                    StartClient();
                }
            }
            else
            {
                GUILayout.Label($"Connected as: {(isHost ? "HOST" : "CLIENT")}");
                GUILayout.Label($"Network ID: {localPlayerId}");
                GUILayout.Label($"Peer Connections: {(isHost ? connectedPeers.Count : 1)}");
                GUILayout.Space(15);

                if (GUILayout.Button("Disconnect"))
                {
                    ShutdownNetwork();
                }
            }

            GUILayout.EndArea();
        }
    }
}
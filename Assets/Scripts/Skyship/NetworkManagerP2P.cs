using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

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
    // Anti-jitter execution chain (see ShipRider): runs FIRST so packet processing (which lerps
    // the ship transform on clients) and pilot-input feed land before ship movement (-100),
    // tilt (-90), the deck-carry (-50), and the player's own move (0).
    [DefaultExecutionOrder(-110)]
    public class NetworkManagerP2P : MonoBehaviour
    {
        [System.Serializable]
        public class PlayerNetworkData
        {
            public string id;
            public Vector3 position;       // world fallback (used when not aboard the ship)
            public Quaternion rotation;
            public bool isPiloting;
            public string heldCargoName;   // name of cargo box currently held (empty if none)

            // Ship-relative pose: when aboard, the receiver parents the puppet under its own
            // ShipVisualRoot and uses these so the puppet rides the moving deck smoothly.
            public bool onShip;
            public Vector3 localPosition;   // ShipVisualRoot-local
            public Quaternion localRotation;

            // The active pilot relays their steering input so the host can fly the ship.
            public float pilotThrottle;     // -1..1
            public float pilotTurn;         // -1..1
            public float pilotLift;         // -1..1 (+ = climb)
        }

        [System.Serializable]
        public class CargoNetworkData
        {
            public string name;
            public Vector3 position;
            public Quaternion rotation;
            public int category; // CargoCategory as int — lets clients spawn crates they missed (harvest drops)
        }

        [System.Serializable]
        public class NodeNetworkData
        {
            public string name;    // deterministic node id (WNode_0000)
            public int remaining;  // yield left — rides in every State packet for lost-packet self-healing
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
            public string packetType; // "Handshake", "Welcome", "Disconnect", "StartGame", "State", "ClaimHelm", "ReleaseHelm", "HarvestRequest", "HarvestResult"
            public int spawnSlot; // host->client: assigned spawn index (Welcome packet)
            public int worldSeed; // host->clients (StartGame): seed for deterministic world generation
            public string pilotId; // host->clients (State): id of the player currently at the helm ("" = nobody)
            public float hubCountdown = -1f; // host->clients (State): seconds left on the hub launch countdown (-1 = not counting)

            // Engine telegraph (client->host "SetThrottle" request; host->clients in State).
            // -1 = not included (client State packets never carry a stage).
            public int throttleStage = -1;

            // Lift lever (client->host "SetLift" request; host->clients in State). -1 = not included.
            public int liftStage = -1;

            // Boarding ramp (client->host "SetRamp" request; host->clients in State).
            // -1 = not included, 0 = stowed, 1 = deployed.
            public int rampDeployed = -1;

            // Resource-node harvesting (client->host "HarvestRequest", host->clients "HarvestResult").
            public string harvestNodeId;
            public Vector3 harvestPlayerPos;     // request: where the harvester stands (drop point is computed near them)
            public Vector3 harvestPlayerForward; // request: where they're looking
            public int harvestRemaining;         // result: node yield left after this harvest
            public string harvestCargoName;      // result: deterministic name of the spawned crate
            public int harvestCargoCategory;     // result: CargoCategory as int
            public Vector3 harvestSpawnPos;      // result: where the crate spawned

            public List<PlayerNetworkData> players = new List<PlayerNetworkData>();
            public List<CargoNetworkData> cargo = new List<CargoNetworkData>();
            public List<NodeNetworkData> nodes = new List<NodeNetworkData>(); // host State: node yields (self-healing)
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

        [Header("Testing")]
        [Tooltip("If the local player falls this far below the ship's deck, snap them back onto the mid " +
                 "deck. Temporary fall-safety while testing (becomes a death barrier later). 0 disables.")]
        public float fallRespawnDistance = 60f;

        [Header("Runtime Connection Info (read-only)")]
        public bool isHost;
        public bool isConnected;
        public string localPlayerId;

        [Tooltip("Seed for procedural world generation. Host picks it and broadcasts it on Start Game; clients receive it so every peer generates the identical world.")]
        public int worldSeed;

        // Host-side roster of connected client player IDs (for lobby UI; populated from incoming packets).
        [System.NonSerialized] public List<string> connectedPlayerIds = new List<string>();

        // Per-player spawn slot so players don't pile onto the same point in the gameplay scene.
        // Host is always slot 0; each joining client is assigned the next free slot and told via a
        // "Welcome" packet. Offsets are applied relative to the scene's authored Player spawn.
        // Spread along the deck's long (Z) axis so players stay on the central spine
        // instead of stepping off the narrow sides.
        private static readonly Vector3[] SpawnOffsets = new Vector3[]
        {
            new Vector3(0f, 0f, 0f),
            new Vector3(0f, 0f, -3f),
            new Vector3(0f, 0f, -6f),
            new Vector3(0f, 0f, -9f),
        };
        private int spawnIndex; // this peer's assigned slot (host = 0)

        // Transient on-screen notice (e.g. "Host disconnected"), drawn for a few seconds.
        private string statusBanner;
        private float statusBannerUntil;

        private UdpClient udpSocket;
        private Thread receiveThread;
        private bool isRunning;
        private float nextSendTime;

        // Thread-safe queue to pass received data (JSON + sender endpoint) to Unity main thread
        private struct IncomingData { public string json; public IPEndPoint endpoint; }
        private ConcurrentQueue<IncomingData> incomingPackets = new ConcurrentQueue<IncomingData>();

        // Connected peers (IP & Port endpoints) - Host only
        private HashSet<IPEndPoint> connectedPeers = new HashSet<IPEndPoint>();
        private IPEndPoint hostEndPoint; // Client only

        // Local states
        private GameObject localPlayer;
        private ShipRider localRider;
        private Transform shipTransform;
        private Transform shipVisualRoot;
        private ShipMovementController shipMovement;
        private ShipBalanceController shipBalance;
        private ShipPlatformArea shipPlatformArea;
        private ShipFailureMonitor shipFailureMonitor;
        private ShipHelm shipHelm;
        private ShipThrottleLever shipThrottleLever;
        private ShipLiftLever shipLiftLever;
        private ShipBoardingRamp shipBoardingRamp;

        /// <summary>The ship's telegraph (built at runtime by ShipHelm, so bind lazily).</summary>
        private ShipThrottleLever Lever
            => shipThrottleLever != null ? shipThrottleLever
             : (shipThrottleLever = UnityEngine.Object.FindAnyObjectByType<ShipThrottleLever>());

        /// <summary>The ship's lift lever (built at runtime by ShipHelm, so bind lazily).</summary>
        private ShipLiftLever LiftLever
            => shipLiftLever != null ? shipLiftLever
             : (shipLiftLever = UnityEngine.Object.FindAnyObjectByType<ShipLiftLever>());

        /// <summary>The ship's boarding ramp (built at runtime by ShipHelm, so bind lazily).</summary>
        private ShipBoardingRamp Ramp
            => shipBoardingRamp != null ? shipBoardingRamp
             : (shipBoardingRamp = UnityEngine.Object.FindAnyObjectByType<ShipBoardingRamp>());

        // Steering-wheel piloting. The host owns the authoritative pilot lock; clients learn it
        // from the host's State packets. Empty string = nobody is at the helm.
        private string currentPilotId = "";
        private bool localSeated;
        private float remotePilotThrottle; // latest relayed input from the current (remote) pilot
        private float remotePilotTurn;
        private float remotePilotLift;

        /// <summary>Set by the pause menu to stop the local player from driving while paused.</summary>
        [System.NonSerialized] public bool localInputSuspended;

        // Player hub launch state. The host owns the countdown; clients receive it in State packets
        // (for display) via HubController. -1 = no countdown running.
        [System.NonSerialized] public float hubCountdown = -1f;
        // Host-side: last-known "standing on the ship deck" flag for each connected client (by id).
        private readonly Dictionary<string, bool> playerAboard = new Dictionary<string, bool>();

        // Remote representations in our local scene
        private Dictionary<string, GameObject> remotePuppets = new Dictionary<string, GameObject>();
        private Dictionary<string, CargoItem> localCargoItems = new Dictionary<string, CargoItem>();
        private Dictionary<string, ResourceNode> localNodes = new Dictionary<string, ResourceNode>();
        private WorldGenerator worldGenerator; // spawns harvest crates identically on every peer

        /// <summary>The single persistent network manager that survives scene loads.</summary>
        public static NetworkManagerP2P Instance { get; private set; }

        /// <summary>
        /// True when this instance owns the ship simulation: the host, OR a standalone
        /// (not networked) session. Only a CONNECTED CLIENT is non-authoritative.
        /// </summary>
        private bool LocalAuthority => !isConnected || isHost;

        /// <summary>Public alias of authority for world/cargo systems (e.g. WorldGenerator).</summary>
        public bool IsWorldAuthority => LocalAuthority;

        private void Awake()
        {
            // Persistent singleton: the connection must survive the menu -> gameplay scene
            // load, otherwise the host stops hosting and clients disconnect on Start Game.
            // Any duplicate (e.g. the one placed in the gameplay scene) destroys itself.
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            localPlayerId = "Player_" + UnityEngine.Random.Range(1000, 9999);

            SceneManager.sceneLoaded += OnSceneLoaded;
            BindSceneObjects();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Puppets from the previous scene were destroyed with it; drop the stale
            // references and re-resolve this scene's Player / ship / cargo.
            ClearPuppets();
            BindSceneObjects();

            // Spread players out using their assigned spawn slot so nobody spawns stacked on the
            // same point (each player's puppet would otherwise sit on top of their own camera).
            // The offset is relative to the scene's authored Player spawn; host (slot 0) stays put.
            if (isConnected && localPlayer != null && scene.name != "MainMenuScene")
            {
                int slot = Mathf.Clamp(spawnIndex, 0, SpawnOffsets.Length - 1);
                TeleportLocalPlayer(localPlayer.transform.position + SpawnOffsets[slot]);
            }
        }

        private void TeleportLocalPlayer(Vector3 worldPos)
        {
            if (localPlayer == null) return;
            // A CharacterController resists direct transform writes; toggle it around the move.
            var cc = localPlayer.GetComponent<CharacterController>();
            if (cc != null) cc.enabled = false;
            localPlayer.transform.position = worldPos;
            if (cc != null) cc.enabled = true;
        }

        /// <summary>(Re)resolve per-scene references. Safe to call in any scene (fields go null if absent).</summary>
        private void BindSceneObjects()
        {
            localPlayer = GameObject.Find("Player");
            localRider = localPlayer != null ? localPlayer.GetComponent<ShipRider>() : null;

            var ship = GameObject.Find("ShipRoot");
            if (ship != null)
            {
                shipTransform = ship.transform;
                shipMovement = ship.GetComponent<ShipMovementController>();
                shipBalance = ship.GetComponent<ShipBalanceController>();
                shipPlatformArea = ship.GetComponent<ShipPlatformArea>();
                shipFailureMonitor = ship.GetComponent<ShipFailureMonitor>();
                shipVisualRoot = shipBalance != null ? shipBalance.shipVisualRoot : null;
            }
            else
            {
                shipTransform = null;
                shipMovement = null;
                shipBalance = null;
                shipPlatformArea = null;
                shipFailureMonitor = null;
                shipVisualRoot = null;
            }

            shipHelm = UnityEngine.Object.FindAnyObjectByType<ShipHelm>();
            shipThrottleLever = UnityEngine.Object.FindAnyObjectByType<ShipThrottleLever>();
            shipLiftLever = UnityEngine.Object.FindAnyObjectByType<ShipLiftLever>();
            shipBoardingRamp = UnityEngine.Object.FindAnyObjectByType<ShipBoardingRamp>();

            // Fresh scene: nobody is at the helm yet, and the local player isn't seated.
            currentPilotId = "";
            localSeated = false;

            // Fresh scene: no hub launch is in progress and no aboard flags carry over.
            hubCountdown = -1f;
            playerAboard.Clear();

            // Cache cargo items / resource nodes by name for fast lookup
            worldGenerator = UnityEngine.Object.FindAnyObjectByType<WorldGenerator>();
            RefreshCargoRegistry();

            ApplyShipAuthority();
        }

        /// <summary>
        /// The ship's simulation runs ONLY on the host; on clients the ship transform/tilt
        /// and cargo are network-synced, so the local controllers must be disabled or they
        /// would fight the synced state.
        /// </summary>
        private void ApplyShipAuthority()
        {
            bool authoritative = LocalAuthority;
            if (shipMovement != null) shipMovement.enabled = authoritative;
            if (shipBalance != null) shipBalance.enabled = authoritative;
            if (shipPlatformArea != null) shipPlatformArea.enabled = authoritative;
            if (shipFailureMonitor != null) shipFailureMonitor.enabled = authoritative;
        }

        /// <summary>
        /// Re-scan the scene for CargoItems and ResourceNodes and rebuild the by-name lookups. Call
        /// this after they're spawned at runtime (e.g. by WorldGenerator) so the host syncs them and
        /// clients can resolve them.
        /// </summary>
        public void RefreshCargoRegistry()
        {
            localCargoItems.Clear();
            var items = GameObject.FindObjectsByType<CargoItem>(FindObjectsInactive.Exclude);
            foreach (var item in items)
                localCargoItems[item.name] = item;

            localNodes.Clear();
            var nodes = GameObject.FindObjectsByType<ResourceNode>(FindObjectsInactive.Exclude);
            foreach (var node in nodes)
                localNodes[node.nodeId] = node;
        }

        // ==========================================
        // RESOURCE-NODE HARVESTING
        // ==========================================

        /// <summary>
        /// Called by PlayerInteraction when the local player presses E on a resource node.
        /// The authority (host/solo) harvests immediately; a connected client asks the host,
        /// and the crate appears when the HarvestResult comes back.
        /// </summary>
        public void RequestHarvest(ResourceNode node, Vector3 playerPos, Vector3 playerForward)
        {
            if (node == null || node.IsDepleted) return;

            if (LocalAuthority)
            {
                HandleHarvest(node, playerPos, playerForward);
            }
            else if (hostEndPoint != null)
            {
                SendPacketDirect(new NetworkPacket
                {
                    senderId = localPlayerId,
                    packetType = "HarvestRequest",
                    harvestNodeId = node.nodeId,
                    harvestPlayerPos = playerPos,
                    harvestPlayerForward = playerForward
                }, hostEndPoint);
            }
        }

        /// <summary>
        /// Authority path: consume one yield, spawn the crate at a clear spot in front of the
        /// harvester, and (when hosting) broadcast the result so every client spawns the identical
        /// crate. A lost result packet self-heals via the State sync (node yields + cargo list).
        /// </summary>
        private void HandleHarvest(ResourceNode node, Vector3 playerPos, Vector3 playerForward)
        {
            if (node == null || node.IsDepleted) return;

            string crateName = node.NextCargoName; // derive BEFORE consuming so the index matches
            Vector3 dropPos = ResourceNode.FindDropPoint(playerPos, playerForward, node.transform.position);
            if (!node.TryConsumeOne()) return;

            SpawnHarvestCrate(crateName, node.CargoCategory, dropPos);

            if (isHost && isConnected)
            {
                BroadcastPacket(new NetworkPacket
                {
                    senderId = localPlayerId,
                    packetType = "HarvestResult",
                    harvestNodeId = node.nodeId,
                    harvestRemaining = node.remainingYield,
                    harvestCargoName = crateName,
                    harvestCargoCategory = (int)node.CargoCategory,
                    harvestSpawnPos = dropPos
                });
            }
        }

        /// <summary>
        /// Spawn a harvest crate (idempotent by name) and register it for cargo sync. Runs on every
        /// peer: the authority from HandleHarvest, clients from HarvestResult packets or the
        /// unknown-cargo self-heal in the State sync.
        /// </summary>
        private CargoItem SpawnHarvestCrate(string crateName, CargoCategory category, Vector3 pos)
        {
            if (localCargoItems.TryGetValue(crateName, out CargoItem existing) && existing != null)
                return existing;

            if (worldGenerator == null)
                worldGenerator = UnityEngine.Object.FindAnyObjectByType<WorldGenerator>();
            if (worldGenerator == null) return null; // not in a generated world (e.g. still in the hub)

            CargoItem item = worldGenerator.SpawnCargoNamed(crateName, category, pos);
            localCargoItems[item.name] = item;
            return item;
        }

        /// <summary>
        /// Collect cargo currently HELD by players who are standing on the ship (the local player plus
        /// any remote puppets riding the deck). ShipBalanceController uses this so a carried crate still
        /// counts toward weight/tilt at the carrier's position — instead of being weightless until it's
        /// dropped. Host/solo only in practice (balance runs on the authority).
        /// </summary>
        public void CollectAboardHeldCargo(List<CargoItem> results)
        {
            if (results == null) return;
            results.Clear();

            // Local player counts as aboard when its ShipRider is riding the deck.
            if (localPlayer != null && localRider != null && localRider.IsRiding)
            {
                var pi = localPlayer.GetComponent<PlayerInteraction>();
                if (pi != null && pi.HeldItem != null) results.Add(pi.HeldItem);
            }

            // Remote players: an aboard puppet is parented under shipVisualRoot, and its held cargo is a
            // child of the puppet (see SyncRemoteCargoHold).
            if (shipVisualRoot != null)
            {
                foreach (var kvp in remotePuppets)
                {
                    var puppet = kvp.Value;
                    if (puppet == null || !puppet.transform.IsChildOf(shipVisualRoot)) continue;
                    var held = puppet.GetComponentInChildren<CargoItem>();
                    if (held != null && held.isHeld) results.Add(held);
                }
            }
        }

        private void Update()
        {
            // 1. Process received data from queue on Unity main thread
            while (incomingPackets.TryDequeue(out IncomingData data))
            {
                try
                {
                    NetworkPacket packet = JsonUtility.FromJson<NetworkPacket>(data.json);
                    if (packet != null && packet.senderId != localPlayerId)
                    {
                        ProcessPacket(packet, data.endpoint);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[NetworkManager] Packet processing error: " + e.Message);
                }
            }

            // 2. The authoritative instance (host or standalone) integrates the active pilot's
            //    steering input into the ship every frame.
            if (LocalAuthority)
                DriveShipFromPilot();

            // 3. Periodically send local state to connected peers
            if (isConnected && Time.time >= nextSendTime)
            {
                nextSendTime = Time.time + updateRate;
                SendState();
            }
        }

        /// <summary>Host-only: feed the pilot's steering/lift and the telegraph's throttle into the
        /// ship. The physical lever owns the throttle when it exists (the wheel only steers/lifts,
        /// and the ship holds its set speed with nobody at the helm); W/S remain the fallback
        /// throttle in scenes without a lever.</summary>
        private void DriveShipFromPilot()
        {
            if (shipMovement == null) return;

            float throttle = 0f, turn = 0f, lift = 0f;
            bool hasPilot = !string.IsNullOrEmpty(currentPilotId);
            if (hasPilot)
            {
                if (currentPilotId == localPlayerId)
                    ReadLocalPilotInput(out throttle, out turn, out lift); // host is the pilot
                else
                {
                    throttle = remotePilotThrottle;               // a client is the pilot (relayed)
                    turn = remotePilotTurn;
                    lift = remotePilotLift;
                }
            }

            var lever = Lever;
            if (lever != null)
                throttle = lever.CurrentThrottle;

            // The spring-loaded lift lever owns climb/descend when it exists (the wheel's
            // Space/Shift remain the fallback in scenes without one).
            var liftLever = LiftLever;
            if (liftLever != null)
                lift = liftLever.CurrentLift;

            shipMovement.inputEnabled = hasPilot
                || (lever != null && Mathf.Abs(lever.CurrentThrottle) > 0.01f)
                || (liftLever != null && Mathf.Abs(liftLever.CurrentLift) > 0.01f);
            shipMovement.SetThrottle(throttle);
            shipMovement.SetSteer(turn);
            shipMovement.SetLift(lift);
        }

        /// <summary>
        /// Called by the lever when the local player clicks it into a new detent. The authority
        /// applies it directly; a client applies optimistically and asks the host, whose State
        /// packets are the authoritative stage for everyone.
        /// </summary>
        public void RequestThrottleStage(int stageIndex)
        {
            stageIndex = Mathf.Clamp(stageIndex, 0, 4);
            if (Lever != null) Lever.ApplyStage(stageIndex);

            if (!LocalAuthority && hostEndPoint != null)
            {
                SendPacketDirect(new NetworkPacket
                {
                    senderId = localPlayerId,
                    packetType = "SetThrottle",
                    throttleStage = stageIndex
                }, hostEndPoint);
            }
        }

        /// <summary>Lift-lever twin of RequestThrottleStage (3 stages, spring-returned by the lever).</summary>
        public void RequestLiftStage(int stageIndex)
        {
            stageIndex = Mathf.Clamp(stageIndex, 0, 2);
            if (LiftLever != null) LiftLever.ApplyStage(stageIndex);

            if (!LocalAuthority && hostEndPoint != null)
            {
                SendPacketDirect(new NetworkPacket
                {
                    senderId = localPlayerId,
                    packetType = "SetLift",
                    liftStage = stageIndex
                }, hostEndPoint);
            }
        }

        /// <summary>
        /// Called by the ramp button. The authority flips the deployed flag directly; a client
        /// flips optimistically and asks the host, whose State packets are authoritative.
        /// </summary>
        public void RequestRampToggle()
        {
            if (Ramp == null) return;
            bool target = !Ramp.DeployedTarget;
            Ramp.SetDeployedTarget(target);

            if (!LocalAuthority && hostEndPoint != null)
            {
                SendPacketDirect(new NetworkPacket
                {
                    senderId = localPlayerId,
                    packetType = "SetRamp",
                    rampDeployed = target ? 1 : 0
                }, hostEndPoint);
            }
        }

        /// <summary>
        /// Read flight controls for whoever is locally at the helm: WASD = throttle/turn,
        /// Space = climb, Left Shift = descend.
        /// </summary>
        private void ReadLocalPilotInput(out float throttle, out float turn, out float lift)
        {
            throttle = 0f;
            turn = 0f;
            lift = 0f;
            if (localInputSuspended) return;
            var k = Keyboard.current;
            if (k == null) return;
            if (k.wKey.isPressed) throttle += 1f;
            if (k.sKey.isPressed) throttle -= 1f;
            if (k.dKey.isPressed) turn += 1f;
            if (k.aKey.isPressed) turn -= 1f;
            if (k.spaceKey.isPressed) lift += 1f;
            if (k.leftShiftKey.isPressed) lift -= 1f;
        }

        // ==========================================
        // STEERING-WHEEL / HELM PILOTING
        // ==========================================

        /// <summary>
        /// Called by PlayerInteraction when the local player presses E at the steering wheel.
        /// Takes the helm if it's free, or releases it if we already hold it. The host decides
        /// authoritatively; clients send a request and seat once the host confirms.
        /// </summary>
        public void ToggleLocalHelm()
        {
            if (shipHelm == null || localPlayer == null) return;

            bool iAmPilot = currentPilotId == localPlayerId;

            if (LocalAuthority)
            {
                // Host or standalone: decide locally.
                if (iAmPilot) SetPilot("");
                else if (string.IsNullOrEmpty(currentPilotId)) SetPilot(localPlayerId);
                // else occupied by someone else -> ignore
            }
            else
            {
                if (iAmPilot)
                {
                    // Optimistically stand up; the host will confirm the freed helm.
                    SendHelmRequest("ReleaseHelm");
                    currentPilotId = "";
                    ApplyLocalSeat(false);
                }
                else
                {
                    // Ask the host; we seat when its next State packet names us as pilot.
                    SendHelmRequest("ClaimHelm");
                }
            }
        }

        private void SendHelmRequest(string type)
        {
            if (hostEndPoint == null) return;
            SendPacketDirect(new NetworkPacket { senderId = localPlayerId, packetType = type }, hostEndPoint);
        }

        /// <summary>Host-authoritative: set who holds the helm and seat/unseat our local player.</summary>
        private void SetPilot(string id)
        {
            currentPilotId = id ?? "";
            remotePilotThrottle = 0f;
            remotePilotTurn = 0f;
            remotePilotLift = 0f;
            ApplyLocalSeat(currentPilotId == localPlayerId);
            // Clients are told via the next State broadcast (statePacket.pilotId).
        }

        /// <summary>Seat or unseat the LOCAL player at the helm (idempotent).</summary>
        private void ApplyLocalSeat(bool seated)
        {
            if (shipHelm == null || localPlayer == null) return;
            if (seated == localSeated) return;
            localSeated = seated;
            if (seated) shipHelm.Seat(localPlayer);
            else shipHelm.Unseat(localPlayer);
        }

        private void OnApplicationQuit()
        {
            ShutdownNetwork();
        }

        private void OnDestroy()
        {
            // A duplicate that destroyed itself in Awake never started anything; skip teardown.
            if (Instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            ShutdownNetwork();
            Instance = null;
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
                spawnIndex = 0; // host always occupies slot 0

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

            ClearPuppets();
            connectedPeers.Clear();
            connectedPlayerIds.Clear();
            spawnIndex = 0;

            isHost = false;
            isConnected = false;
            Debug.Log("[NetworkManager] Network shut down.");
        }

        /// <summary>
        /// Client-side: the host has gone away. Tear down the connection, show a notice,
        /// and return to the main menu.
        /// </summary>
        private void HandleHostDisconnected()
        {
            Debug.Log("[NetworkManager] Host disconnected. Returning to main menu.");
            ShutdownNetwork();

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            ShowBanner("Host disconnected. Returned to main menu.", 6f);

            if (SceneManager.GetActiveScene().name != "MainMenuScene")
                SceneManager.LoadScene("MainMenuScene");
        }

        /// <summary>Show a transient on-screen notice (drawn by OnGUI for <paramref name="seconds"/>).</summary>
        public void ShowBanner(string message, float seconds = 5f)
        {
            statusBanner = message;
            statusBannerUntil = Time.time + seconds;
        }

        /// <summary>Destroy all remote player puppets and forget them.</summary>
        private void ClearPuppets()
        {
            foreach (var kvp in remotePuppets)
            {
                if (kvp.Value != null)
                    Destroy(kvp.Value);
            }
            remotePuppets.Clear();
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
                bool amPilot = currentPilotId == localPlayerId;

                var pData = new PlayerNetworkData
                {
                    id = localPlayerId,
                    position = localPlayer.transform.position,
                    rotation = localPlayer.transform.rotation,
                    isPiloting = amPilot,
                    heldCargoName = (pInteraction != null && pInteraction.HeldItem != null) ? pInteraction.HeldItem.name : ""
                };

                // If aboard the deck (walking or seated), send a ship-relative pose so remote
                // puppets ride the moving deck smoothly instead of chasing world coords at 20 Hz.
                bool aboard = shipVisualRoot != null && (amPilot || (localRider != null && localRider.IsRiding));
                if (aboard)
                {
                    pData.onShip = true;
                    pData.localPosition = shipVisualRoot.InverseTransformPoint(localPlayer.transform.position);
                    pData.localRotation = Quaternion.Inverse(shipVisualRoot.rotation) * localPlayer.transform.rotation;
                }

                // The pilot relays their steering input so the host can fly the ship.
                if (amPilot)
                    ReadLocalPilotInput(out pData.pilotThrottle, out pData.pilotTurn, out pData.pilotLift);

                statePacket.players.Add(pData);
            }

            // Host is responsible for syncing loose cargo, ship kinematics, and the pilot lock
            if (isHost)
            {
                statePacket.pilotId = currentPilotId;
                statePacket.hubCountdown = hubCountdown; // relay the hub launch countdown to clients
                if (Lever != null) statePacket.throttleStage = Lever.Stage;        // authoritative telegraph
                if (LiftLever != null) statePacket.liftStage = LiftLever.Stage;    // authoritative lift lever
                if (Ramp != null) statePacket.rampDeployed = Ramp.DeployedTarget ? 1 : 0;

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
                            rotation = item.transform.rotation,
                            category = (int)item.category
                        });
                    }
                }

                // Node yields ride along every tick (tiny), so a client that missed a
                // HarvestResult converges on the next State packet.
                foreach (var kvp in localNodes)
                {
                    ResourceNode node = kvp.Value;
                    if (node == null) continue;
                    statePacket.nodes.Add(new NodeNetworkData
                    {
                        name = node.nodeId,
                        remaining = node.remainingYield
                    });
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
            // Host picks the world seed for this session; every peer generates the same world from it.
            worldSeed = UnityEngine.Random.Range(1, int.MaxValue);

            if (isHost && isConnected)
            {
                NetworkPacket startPacket = new NetworkPacket
                {
                    senderId = sceneName, // use senderId to pass the scene name
                    packetType = "StartGame",
                    worldSeed = worldSeed
                };
                BroadcastPacket(startPacket);
            }
        }

        // ==========================================
        // PLAYER HUB → WORLD LAUNCH
        // ==========================================

        /// <summary>
        /// Host/solo check: is every player currently standing on the ship deck? Used by HubController
        /// to decide when to start the launch countdown. The local (host) player is checked via its
        /// ShipRider; each connected client via the onShip flag from its latest State packet.
        /// </summary>
        public bool AreAllPlayersAboard()
        {
            if (!LocalAuthority) return false; // only the authority decides launches

            bool localAboard = localRider != null && localRider.IsRiding;
            if (!localAboard) return false;

            foreach (string id in connectedPlayerIds)
            {
                if (!playerAboard.TryGetValue(id, out bool aboard) || !aboard)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Host/solo: leave the hub and load the procedural world, picking + broadcasting a fresh world
        /// seed so every peer generates the identical world (clients load it via the StartGame packet).
        /// </summary>
        public void LaunchToWorld(string sceneName)
        {
            if (!LocalAuthority) return;
            SendStartGameNotice(sceneName); // picks the world seed and (if connected) broadcasts StartGame
            hubCountdown = -1f;
            SceneManager.LoadScene(sceneName);
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

                        // Clone the endpoint: senderEP is reused by the next Receive call.
                        IPEndPoint from = new IPEndPoint(senderEP.Address, senderEP.Port);

                        // Host tracks sender's endpoint for peer mapping
                        if (isHost)
                        {
                            connectedPeers.Add(from);
                        }

                        incomingPackets.Enqueue(new IncomingData { json = json, endpoint = from });
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

        private void ProcessPacket(NetworkPacket packet, IPEndPoint from)
        {
            if (packet.packetType == "Disconnect")
            {
                if (isHost)
                {
                    // A client left: drop it from the roster and remove its puppet.
                    connectedPlayerIds.Remove(packet.senderId);
                    playerAboard.Remove(packet.senderId);
                    DestroyPuppet(packet.senderId);
                    // If they were piloting, free the helm.
                    if (currentPilotId == packet.senderId)
                        SetPilot("");
                }
                else
                {
                    // In this star topology a client only ever hears from the host, so any
                    // Disconnect means the host quit — kick everyone back to the main menu.
                    HandleHostDisconnected();
                }
                return;
            }

            // Telegraph/lift/ramp requests are host-authoritative (clients ask; State confirms).
            if (packet.packetType == "SetThrottle")
            {
                if (isHost && Lever != null)
                    Lever.ApplyStage(Mathf.Clamp(packet.throttleStage, 0, 4));
                return;
            }
            if (packet.packetType == "SetLift")
            {
                if (isHost && LiftLever != null)
                    LiftLever.ApplyStage(Mathf.Clamp(packet.liftStage, 0, 2));
                return;
            }
            if (packet.packetType == "SetRamp")
            {
                if (isHost && Ramp != null && packet.rampDeployed >= 0)
                    Ramp.SetDeployedTarget(packet.rampDeployed == 1);
                return;
            }

            // Harvest requests are host-authoritative (clients ask, host validates + broadcasts).
            if (packet.packetType == "HarvestRequest")
            {
                if (isHost && localNodes.TryGetValue(packet.harvestNodeId ?? "", out ResourceNode reqNode))
                    HandleHarvest(reqNode, packet.harvestPlayerPos, packet.harvestPlayerForward);
                return;
            }

            // A harvest happened on the host: mirror the node's yield and spawn the same crate.
            if (packet.packetType == "HarvestResult")
            {
                if (!isHost)
                {
                    if (localNodes.TryGetValue(packet.harvestNodeId ?? "", out ResourceNode resNode))
                        resNode.ApplyRemoteYield(packet.harvestRemaining);
                    if (!string.IsNullOrEmpty(packet.harvestCargoName))
                        SpawnHarvestCrate(packet.harvestCargoName,
                                          (CargoCategory)packet.harvestCargoCategory,
                                          packet.harvestSpawnPos);
                }
                return;
            }

            // Helm claim/release requests are host-authoritative (clients ask, host decides).
            if (packet.packetType == "ClaimHelm")
            {
                if (isHost && string.IsNullOrEmpty(currentPilotId))
                    SetPilot(packet.senderId);
                return;
            }
            if (packet.packetType == "ReleaseHelm")
            {
                if (isHost && currentPilotId == packet.senderId)
                    SetPilot("");
                return;
            }

            if (packet.packetType == "Welcome")
            {
                // Host assigned us a spawn slot. Store it (and apply now if we're already in-game).
                spawnIndex = packet.spawnSlot;
                Debug.Log($"[NetworkClient] Host assigned spawn slot {spawnIndex}.");
                if (localPlayer != null && SceneManager.GetActiveScene().name != "MainMenuScene")
                {
                    int slot = Mathf.Clamp(spawnIndex, 0, SpawnOffsets.Length - 1);
                    // Re-place relative to the authored spawn origin (slot 0 offset is the origin).
                    TeleportLocalPlayer(new Vector3(0f, localPlayer.transform.position.y, 0f) + SpawnOffsets[slot]);
                }
                return;
            }

            if (packet.packetType == "StartGame")
            {
                // The HOST must never act on a StartGame packet — it already loaded the scene
                // itself, and reloading here would tear the just-loaded gameplay scene back down.
                if (isHost)
                {
                    Debug.LogWarning($"[NetworkManager] HOST received a StartGame packet (from '{packet.senderId}') and IGNORED it. This would have reloaded the scene.");
                    return;
                }
                Debug.Log("[NetworkClient] Received StartGame notice from Host. Loading gameplay scene...");
                // Adopt the host's world seed BEFORE loading so our WorldGenerator builds the same world.
                worldSeed = packet.worldSeed;
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
                // Assign the next free spawn slot (host is 0; first client 1, etc.) and tell them.
                int assignedSlot = Mathf.Clamp(connectedPlayerIds.Count, 0, SpawnOffsets.Length - 1);
                if (from != null)
                {
                    SendPacketDirect(new NetworkPacket
                    {
                        senderId = localPlayerId,
                        packetType = "Welcome",
                        spawnSlot = assignedSlot
                    }, from);
                }
                Debug.Log($"[NetworkManager] Player joined lobby: {packet.senderId} (spawn slot {assignedSlot})");
            }

            // Client: learn the authoritative pilot lock from the host and seat/unseat locally.
            if (!isHost)
            {
                string newPilot = packet.pilotId ?? "";
                if (newPilot != currentPilotId)
                {
                    currentPilotId = newPilot;
                    ApplyLocalSeat(currentPilotId == localPlayerId);
                }

                // Mirror the host's hub launch countdown so the client HUD can show it.
                hubCountdown = packet.hubCountdown;

                // Mirror the host's deck stations (visuals on this client).
                if (packet.throttleStage >= 0 && Lever != null)
                    Lever.ApplyRemoteStage(packet.throttleStage);
                if (packet.liftStage >= 0 && LiftLever != null)
                    LiftLever.ApplyRemoteStage(packet.liftStage);
                if (packet.rampDeployed >= 0 && Ramp != null)
                    Ramp.ApplyRemoteDeployed(packet.rampDeployed == 1);
            }

            // Sync other player positions & pickups
            foreach (var pData in packet.players)
            {
                if (pData.id == localPlayerId) continue; // safety skip

                // Host: remember the current (remote) pilot's relayed steering input.
                if (isHost && pData.id == currentPilotId)
                {
                    remotePilotThrottle = pData.pilotThrottle;
                    remotePilotTurn = pData.pilotTurn;
                    remotePilotLift = pData.pilotLift;
                }

                // Host: track whether each client is standing on the ship deck (for the hub launch).
                if (isHost)
                    playerAboard[pData.id] = pData.onShip;

                GameObject puppet = GetOrCreatePuppet(pData.id);

                if (pData.onShip && shipVisualRoot != null)
                {
                    // Ride the deck: parent under our ShipVisualRoot and interpolate only the small
                    // local offset, so the deck's own motion carries the puppet for free.
                    if (puppet.transform.parent != shipVisualRoot)
                        puppet.transform.SetParent(shipVisualRoot, true);
                    puppet.transform.localPosition = Vector3.Lerp(puppet.transform.localPosition, pData.localPosition, 0.4f);
                    puppet.transform.localRotation = Quaternion.Slerp(puppet.transform.localRotation, pData.localRotation, 0.4f);
                }
                else
                {
                    // Off the ship (or we have no ship): plain world-space interpolation.
                    if (puppet.transform.parent != null)
                        puppet.transform.SetParent(null, true);
                    puppet.transform.position = Vector3.Lerp(puppet.transform.position, pData.position, 0.4f);
                    puppet.transform.rotation = Quaternion.Slerp(puppet.transform.rotation, pData.rotation, 0.4f);
                }

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
                    else
                    {
                        // Unknown crate: we missed its HarvestResult packet. Spawn it from the
                        // State data so the world converges (idempotent by name).
                        SpawnHarvestCrate(cData.name, (CargoCategory)cData.category, cData.position);
                    }
                }

                // Client-only: reconcile node yields with the host (covers lost HarvestResults).
                foreach (var nData in packet.nodes)
                {
                    if (localNodes.TryGetValue(nData.name, out ResourceNode node) && node != null)
                        node.ApplyRemoteYield(nData.remaining);
                }
            }
        }

        private GameObject GetOrCreatePuppet(string id)
        {
            if (remotePuppets.TryGetValue(id, out GameObject puppet))
            {
                if (puppet != null) return puppet;
            }

            // Create a player-shaped capsule body representing the remote player
            GameObject pGO = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            pGO.name = "Puppet_" + id;

            // Remove collider so they don't push local players
            var col = pGO.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Add a small child cylinder near the head as a facing/orientation indicator
            GameObject visor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            visor.name = "Visor";
            visor.transform.SetParent(pGO.transform);
            visor.transform.localPosition = new Vector3(0f, 0.6f, 0.4f);
            visor.transform.localScale = new Vector3(0.2f, 0.2f, 0.4f);
            visor.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            var vCol = visor.GetComponent<Collider>();
            if (vCol != null) Destroy(vCol);

            // Paint puppet orange
            var renderer = pGO.GetComponent<Renderer>();
            if (renderer != null)
                renderer.sharedMaterial = MakePuppetMaterial(puppetColor);

            var visorRenderer = visor.GetComponent<Renderer>();
            if (visorRenderer != null)
                visorRenderer.sharedMaterial = MakePuppetMaterial(Color.black);

            remotePuppets[id] = pGO;
            Debug.Log($"[NetworkManager] Spawned remote player puppet for client {id}.");
            return pGO;
        }

        /// <summary>Creates a solid-colored material that works under URP (falls back to built-in if needed).</summary>
        private static Material MakePuppetMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard"); // built-in RP fallback
            Material m = new Material(shader);
            m.color = color;
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color); // URP main color
            return m;
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
            // Transient notice banner (e.g. "Host disconnected"), shown regardless of debug HUD.
            if (!string.IsNullOrEmpty(statusBanner) && Time.time < statusBannerUntil)
            {
                const float bw = 460f, bh = 40f;
                var prev = GUI.skin.box.alignment;
                var prevFont = GUI.skin.box.fontSize;
                GUI.skin.box.alignment = TextAnchor.MiddleCenter;
                GUI.skin.box.fontSize = 16;
                GUI.Box(new Rect((Screen.width - bw) * 0.5f, 24f, bw, bh), statusBanner);
                GUI.skin.box.alignment = prev;
                GUI.skin.box.fontSize = prevFont;
            }

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
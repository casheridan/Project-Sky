using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Skyship
{
    /// <summary>
    /// Owns the vertical-slice gameplay loop: expedition selection → launch → host-authoritative
    /// objective tracking → return to port → reward resolution → campaign progress.
    ///
    /// One persistent instance (created by VerticalSliceBootstrap, DontDestroyOnLoad) lives across
    /// hub/world scene loads, mirroring how NetworkManagerP2P persists.
    ///
    /// AUTHORITY MODEL (peer-to-peer, host = authority):
    ///  - The HOST owns PlayerProgressState (its save is the campaign), the runtime expedition
    ///    state, objective completion, and reward resolution.
    ///  - Clients receive everything they display through ExpeditionNetState, a JSON blob the host
    ///    embeds in its 20 Hz State packets (see NetworkManagerP2P.SendState / ProcessPacket).
    ///  - Clients REQUEST actions (e.g. Return to Port) via packets; the host validates.
    /// </summary>
    public class ExpeditionManager : MonoBehaviour
    {
        public static ExpeditionManager Instance { get; private set; }

        [Header("Scenes")]
        public string hubSceneName = "HubScene";

        [Header("Hub Economy")]
        [Tooltip("Scrap cost of one hull repair action at the Sky Chart.")]
        public int repairScrapCost = 10;
        [Tooltip("Hull damage removed per repair action.")]
        public float repairAmount = 25f;
        [Tooltip("Money cost of one fuel purchase at the Sky Chart.")]
        public int fuelMoneyCost = 20;
        [Tooltip("Fuel gained per purchase.")]
        public float fuelPurchaseAmount = 2f;

        [Header("Runtime (read-only)")]
        public PlayerProgressState progress = new PlayerProgressState();
        public ExpeditionRuntimeState runtime = new ExpeditionRuntimeState();
        [Tooltip("Set after a return to port; the HUD shows it as the results screen in the hub.")]
        public ExpeditionResults lastResults;
        public bool resultsPending;
        [Tooltip("Total corruptionValue of cargo currently on the ship (host-computed; threat input).")]
        public float corruptionAboard;

        private float nextObjectivePoll;
        private ShipPlatformArea platformArea;
        private bool returnInProgress; // guards double return (bell spam / request while loading)

        /// <summary>True when this instance owns expedition state: host or solo (mirrors NetworkManagerP2P).</summary>
        public bool IsAuthority
        {
            get
            {
                var nm = NetworkManagerP2P.Instance;
                return nm == null || nm.IsWorldAuthority;
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            progress = PlayerProgressState.Load();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (Instance != this) return;
            SceneManager.sceneLoaded -= OnSceneLoaded;
            Instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            platformArea = null; // rebind lazily in the new scene
            returnInProgress = false;

            // Multiplayer debug: every peer prints the expedition + seed it is playing.
            if (runtime.phase == ExpeditionPhase.Active || runtime.phase == ExpeditionPhase.ReturnReady)
            {
                Debug.Log($"[ExpeditionManager] Scene '{scene.name}' — expedition '{runtime.selectedExpeditionId}' " +
                          $"seed={runtime.worldSeed} phase={runtime.phase} (authority={IsAuthority})");
            }

            // Back in the hub with nothing active: fall back to a clean Preparing/None state so a
            // stale Active phase can't leak in (e.g. host quit to menu mid-run).
            if (scene.name == hubSceneName &&
                (runtime.phase == ExpeditionPhase.Active || runtime.phase == ExpeditionPhase.ReturnReady) &&
                !returnInProgress)
            {
                runtime.ResetToHub();
            }
        }

        private void Update()
        {
            if (!IsAuthority) return;
            if (runtime.phase != ExpeditionPhase.Active && runtime.phase != ExpeditionPhase.ReturnReady) return;

            runtime.elapsedSeconds += Time.deltaTime;

            if (Time.time >= nextObjectivePoll)
            {
                nextObjectivePoll = Time.time + 0.5f;
                PollObjective();
            }
        }

        // ========================================================================================
        // SELECTION + LAUNCH (hub)
        // ========================================================================================

        /// <summary>Host: select an expedition at the Sky Chart. Clients see it via the sync blob.</summary>
        public void SelectExpedition(string expeditionId)
        {
            if (!IsAuthority) return;
            if (runtime.phase == ExpeditionPhase.Active || runtime.phase == ExpeditionPhase.ReturnReady) return;

            var def = ExpeditionDatabase.GetExpedition(expeditionId);
            if (def == null || !progress.IsUnlocked(expeditionId) || def.isPlaceholder)
            {
                Debug.LogWarning($"[ExpeditionManager] SelectExpedition rejected: '{expeditionId}'");
                return;
            }
            runtime.selectedExpeditionId = expeditionId;
            runtime.phase = ExpeditionPhase.Preparing;
            Debug.Log($"[ExpeditionManager] Expedition selected: {def.title}");
        }

        public void ClearSelection()
        {
            if (!IsAuthority) return;
            if (runtime.phase != ExpeditionPhase.Preparing) return;
            runtime.selectedExpeditionId = "";
            runtime.phase = ExpeditionPhase.None;
        }

        /// <summary>Can the crew launch right now? (expedition selected, launchable, fuel covers it)</summary>
        public bool CanLaunch(out string reason)
        {
            var def = ExpeditionDatabase.GetExpedition(runtime.selectedExpeditionId);
            if (runtime.phase != ExpeditionPhase.Preparing || def == null)
            {
                reason = "Select an expedition at the Sky Chart";
                return false;
            }
            if (def.isPlaceholder)
            {
                reason = "That lead can't be followed yet";
                return false;
            }
            if (progress.fuel < def.fuelCost)
            {
                reason = $"Not enough fuel ({progress.fuel:0.#}/{def.fuelCost:0.#}) — buy fuel at the Sky Chart";
                return false;
            }
            reason = "";
            return true;
        }

        /// <summary>
        /// Host: commit the launch — deduct fuel and flip to Active. Called by HubController right
        /// before NetworkManagerP2P.LaunchToWorld (which picks the seed and broadcasts StartGame
        /// with our expedition id so clients enter the same state before the scene loads).
        /// </summary>
        public void HostBeginExpedition()
        {
            if (!IsAuthority || !CanLaunch(out _)) return;

            var def = ExpeditionDatabase.GetExpedition(runtime.selectedExpeditionId);
            progress.fuel -= def.fuelCost;
            progress.Save();
            EnterActivePhase(def);
        }

        /// <summary>Client: adopt the host's expedition (from the StartGame packet) before the world loads.</summary>
        public void OnRemoteExpeditionStart(string expeditionId, int worldSeed)
        {
            var def = ExpeditionDatabase.GetExpedition(expeditionId);
            if (def == null)
            {
                runtime.ResetToHub();
                return;
            }
            runtime.selectedExpeditionId = expeditionId;
            EnterActivePhase(def);
            runtime.worldSeed = worldSeed;
        }

        private void EnterActivePhase(ExpeditionDefinition def)
        {
            runtime.phase = ExpeditionPhase.Active;
            runtime.elapsedSeconds = 0f;
            runtime.objectiveCount = 0;
            runtime.objectiveRequired = def.objective != null ? def.objective.requiredCount : 1;
            runtime.objectiveItemPickedUp = false;
            runtime.objectiveCargoRecovered = false;
            runtime.threatLevel = 0;
            var nm = NetworkManagerP2P.Instance;
            runtime.worldSeed = nm != null ? nm.worldSeed : 0;
        }

        /// <summary>
        /// The generation request the procedural generator consumes. Built identically on every
        /// peer from synced state (expedition id + seed), keeping generation deterministic.
        /// Null when no expedition is active (legacy free-roam generation).
        /// </summary>
        public ExpeditionGenerationRequest BuildGenerationRequest(int seed)
        {
            var def = ExpeditionDatabase.GetExpedition(runtime.selectedExpeditionId);
            if (def == null || runtime.phase != ExpeditionPhase.Active && runtime.phase != ExpeditionPhase.ReturnReady)
                return null;

            return new ExpeditionGenerationRequest
            {
                seed = seed,
                expedition = def,
                region = def.Region,
                dangerRating = def.dangerRating,
                requiredSiteType = def.requiredSiteType,
                modifiers = new List<string>(def.modifiers)
            };
        }

        // ========================================================================================
        // OBJECTIVE TRACKING (host)
        // ========================================================================================

        /// <summary>
        /// Host: scan the ship's deck for objective cargo. "On the ship" = tracked by the platform
        /// trigger, not held, and parented under the ride parent (the parenting check filters any
        /// stale trigger entries from items carried off the deck while held).
        /// </summary>
        private void PollObjective()
        {
            var def = ExpeditionDatabase.GetExpedition(runtime.selectedExpeditionId);
            if (def == null || def.objective == null) return;

            if (platformArea == null)
            {
                platformArea = FindAnyObjectByType<ShipPlatformArea>();
                if (platformArea == null) return; // no player ship in this scene
            }

            string targetDef = def.objective.targetCargoDefinitionId;
            int count = 0;
            float corruption = 0f;

            var items = platformArea.itemsInPlatform;
            Transform rideParent = platformArea.rideParent;
            for (int i = 0; i < items.Count; i++)
            {
                CargoItem item = items[i];
                if (item == null || item.isHeld) continue;
                if (rideParent != null && !item.transform.IsChildOf(rideParent)) continue;

                corruption += item.corruptionValue;
                if (item.definitionId == targetDef) count++;
            }
            corruptionAboard = corruption;
            runtime.objectiveCount = count;

            // "Picked up at least once" — feeds the threat director's alarm event. Checked over all
            // scene cargo because the held item leaves the deck lists while carried.
            if (!runtime.objectiveItemPickedUp)
            {
                var all = FindObjectsByType<CargoItem>(FindObjectsInactive.Exclude);
                for (int i = 0; i < all.Length; i++)
                {
                    if (all[i].isHeld && all[i].definitionId == targetDef)
                    {
                        runtime.objectiveItemPickedUp = true;
                        Debug.Log("[ExpeditionManager] Objective cargo picked up.");
                        break;
                    }
                }
            }

            if (!runtime.objectiveCargoRecovered && count >= runtime.objectiveRequired)
            {
                runtime.objectiveCargoRecovered = true;
                runtime.phase = ExpeditionPhase.ReturnReady;
                Debug.Log("[ExpeditionManager] OBJECTIVE COMPLETE — return to port available.");
                BroadcastEvent("OBJECTIVE SECURED — return to port when ready.");
            }
            else if (runtime.objectiveCargoRecovered && count < runtime.objectiveRequired)
            {
                // Objective cargo slid/was carried off the ship again: revoke completion.
                runtime.objectiveCargoRecovered = false;
                runtime.phase = ExpeditionPhase.Active;
                BroadcastEvent("Objective cargo lost from the ship!");
            }
        }

        // ========================================================================================
        // RETURN TO PORT + REWARDS (host)
        // ========================================================================================

        /// <summary>
        /// Route a local "return to port" press: the authority executes; a client sends a request
        /// packet for the host to validate.
        /// </summary>
        public void RequestReturnToPort()
        {
            if (IsAuthority)
            {
                HostExecuteReturn();
            }
            else
            {
                var nm = NetworkManagerP2P.Instance;
                if (nm != null) nm.SendReturnRequest();
            }
        }

        /// <summary>
        /// Host: validate + execute the return. Determines success/abandoned, converts the cargo
        /// aboard into resources, applies expedition rewards + unlocks, saves the campaign,
        /// broadcasts the results, and brings everyone back to the hub.
        /// </summary>
        public void HostExecuteReturn()
        {
            if (!IsAuthority || returnInProgress) return;
            if (runtime.phase != ExpeditionPhase.Active && runtime.phase != ExpeditionPhase.ReturnReady) return;
            returnInProgress = true;

            var def = ExpeditionDatabase.GetExpedition(runtime.selectedExpeditionId);
            bool success = runtime.objectiveCargoRecovered;

            var results = new ExpeditionResults
            {
                expeditionId = runtime.selectedExpeditionId,
                title = def != null ? def.title : "Expedition",
                success = success,
                outcome = success ? "Success" : "Abandoned"
            };

            // 1. Convert the cargo physically on the deck into resources.
            TallyCargoAboard(results);

            // 2. Expedition reward on success (money, scrap, fuel, chart progress, new leads).
            if (success && def != null && def.reward != null)
            {
                results.moneyEarned += def.reward.money;
                results.scrapEarned += def.reward.scrap;
                results.fuelRecovered += def.reward.fuel;
                results.chartFragmentsGained += def.reward.chartFragments;
                progress.MarkCompleted(def.expeditionId);
                foreach (string unlockId in def.reward.unlockExpeditionIds)
                {
                    if (!progress.IsUnlocked(unlockId))
                    {
                        progress.Unlock(unlockId);
                        var lead = ExpeditionDatabase.GetExpedition(unlockId);
                        results.newLeads.Add(lead != null ? lead.title : unlockId);
                    }
                }
            }

            // 3. Consequences carried home (hull damage from capsizes etc. persists until repaired).
            if (progress.hullDamage > 0.5f)
                results.consequences = $"Hull damage: {progress.hullDamage:0} (repair with scrap at the Sky Chart)";

            // 4. Apply + persist.
            progress.money += results.moneyEarned;
            progress.scrap += results.scrapEarned;
            progress.fuel += results.fuelRecovered;
            progress.chartFragments += results.chartFragmentsGained;
            progress.Save();

            lastResults = results;
            resultsPending = true;
            runtime.ResetToHub();

            Debug.Log($"[ExpeditionManager] Return to port: {results.outcome} — " +
                      $"+{results.moneyEarned} money, +{results.scrapEarned} scrap, +{results.fuelRecovered:0.#} fuel.");

            // 5. Bring every peer home together (packet first, then our own scene load).
            var nm = NetworkManagerP2P.Instance;
            if (nm != null) nm.BroadcastReturnToPort(JsonUtility.ToJson(results));
            SceneManager.LoadScene(hubSceneName);
        }

        /// <summary>Sum up everything on the deck: money for sellables, scrap by weight, fuel cells as fuel.</summary>
        private void TallyCargoAboard(ExpeditionResults results)
        {
            if (platformArea == null) platformArea = FindAnyObjectByType<ShipPlatformArea>();
            if (platformArea == null) return;

            var items = platformArea.itemsInPlatform;
            Transform rideParent = platformArea.rideParent;
            for (int i = 0; i < items.Count; i++)
            {
                CargoItem item = items[i];
                if (item == null || item.isHeld) continue;
                if (rideParent != null && !item.transform.IsChildOf(rideParent)) continue;

                results.cargoRecovered++;

                var cdef = CargoDatabase.Get(item.definitionId) ?? CargoDatabase.ForLegacyCategory(item.category);
                bool sellable = cdef == null || cdef.canBeSold;

                if (item.category == CargoCategory.Fuel)
                    results.fuelRecovered += 1f;                       // each fuel cell = 1 fuel unit
                else if (sellable)
                    results.moneyEarned += Mathf.RoundToInt(item.value);

                // Scrap-flavored cargo also yields scrap by weight.
                if (item.category == CargoCategory.RawResource || item.category == CargoCategory.RepairCargo ||
                    item.category == CargoCategory.Stone || item.category == CargoCategory.Ore)
                    results.scrapEarned += Mathf.RoundToInt(item.weight * 0.5f);
            }
        }

        /// <summary>Client: the host returned everyone to port — adopt the results and follow.</summary>
        public void ClientApplyReturn(string resultsJson)
        {
            if (!string.IsNullOrEmpty(resultsJson))
            {
                try { lastResults = JsonUtility.FromJson<ExpeditionResults>(resultsJson); }
                catch { lastResults = null; }
            }
            resultsPending = lastResults != null;
            runtime.ResetToHub();
            SceneManager.LoadScene(hubSceneName);
        }

        // ========================================================================================
        // HUB SERVICES (host-validated economy)
        // ========================================================================================

        public bool HostRepairHull()
        {
            if (!IsAuthority || progress.scrap < repairScrapCost || progress.hullDamage <= 0f) return false;
            progress.scrap -= repairScrapCost;
            progress.hullDamage = Mathf.Max(0f, progress.hullDamage - repairAmount);
            progress.Save();
            return true;
        }

        public bool HostBuyFuel()
        {
            if (!IsAuthority || progress.money < fuelMoneyCost) return false;
            progress.money -= fuelMoneyCost;
            progress.fuel += fuelPurchaseAmount;
            progress.Save();
            return true;
        }

        // ========================================================================================
        // NETWORK SYNC (host serializes; clients apply)
        // ========================================================================================

        /// <summary>Host: build the sync blob embedded in every State packet.</summary>
        public string SerializeNetState()
        {
            var s = new ExpeditionNetState
            {
                selectedExpeditionId = runtime.selectedExpeditionId,
                phase = (int)runtime.phase,
                elapsedSeconds = runtime.elapsedSeconds,
                objectiveCount = runtime.objectiveCount,
                objectiveRequired = runtime.objectiveRequired,
                objectiveItemPickedUp = runtime.objectiveItemPickedUp,
                objectiveCargoRecovered = runtime.objectiveCargoRecovered,
                threatLevel = runtime.threatLevel,
                tiltState = (int)runtime.tiltState,
                weightState = (int)runtime.weightState,
                stormActive = runtime.stormActive,
                stormCenter = runtime.stormCenter,
                stormRadius = runtime.stormRadius,
                screamActive = runtime.screamActive,
                leviathanState = runtime.leviathanState,
                barnaclesCsv = runtime.barnaclesCsv,
                money = progress.money,
                scrap = progress.scrap,
                fuel = progress.fuel,
                chartFragments = progress.chartFragments,
                hullDamage = progress.hullDamage,
                unlockedCsv = string.Join(",", progress.unlockedExpeditionIds),
                completedCsv = string.Join(",", progress.completedExpeditionIds)
            };
            return JsonUtility.ToJson(s);
        }

        /// <summary>Client: mirror the host's expedition + progress state (display only).</summary>
        public void ApplyNetState(string json)
        {
            if (IsAuthority || string.IsNullOrEmpty(json)) return;

            ExpeditionNetState s;
            try { s = JsonUtility.FromJson<ExpeditionNetState>(json); }
            catch { return; }
            if (s == null) return;

            runtime.selectedExpeditionId = s.selectedExpeditionId ?? "";
            runtime.phase = (ExpeditionPhase)s.phase;
            runtime.elapsedSeconds = s.elapsedSeconds;
            runtime.objectiveCount = s.objectiveCount;
            runtime.objectiveRequired = s.objectiveRequired;
            runtime.objectiveItemPickedUp = s.objectiveItemPickedUp;
            runtime.objectiveCargoRecovered = s.objectiveCargoRecovered;
            runtime.threatLevel = s.threatLevel;
            runtime.tiltState = (ShipTiltState)s.tiltState;
            runtime.weightState = (ShipWeightState)s.weightState;
            runtime.stormActive = s.stormActive;
            runtime.stormCenter = s.stormCenter;
            runtime.stormRadius = s.stormRadius;
            runtime.screamActive = s.screamActive;
            runtime.leviathanState = s.leviathanState;
            runtime.barnaclesCsv = s.barnaclesCsv ?? "";

            progress.money = s.money;
            progress.scrap = s.scrap;
            progress.fuel = s.fuel;
            progress.chartFragments = s.chartFragments;
            progress.hullDamage = s.hullDamage;
            progress.unlockedExpeditionIds = SplitCsv(s.unlockedCsv);
            progress.completedExpeditionIds = SplitCsv(s.completedCsv);
        }

        private static List<string> SplitCsv(string csv)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(csv)) return list;
            foreach (string part in csv.Split(','))
                if (!string.IsNullOrEmpty(part)) list.Add(part);
            return list;
        }

        /// <summary>Show a banner on every peer (host broadcasts; solo just shows locally).</summary>
        public void BroadcastEvent(string message)
        {
            var nm = NetworkManagerP2P.Instance;
            if (nm != null) nm.BroadcastExpeditionEvent(message);
            else Debug.Log("[ExpeditionEvent] " + message);
        }
    }
}

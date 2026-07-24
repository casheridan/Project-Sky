using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Skyship
{
    /// <summary>
    /// The slice's programmer HUD (IMGUI, zero scene wiring — created by VerticalSliceBootstrap,
    /// persistent). Draws, depending on scene/phase:
    ///  - WORLD: objective tracker + threat level (top-left), ship status panel (bottom-left),
    ///    Return-to-Port prompt when the objective is secured.
    ///  - HUB:   shared resource readout (top-right) and the expedition RESULTS screen after a
    ///    return (dismissed with Enter/Escape so the locked cursor never has to be freed).
    ///  - F3 toggles a multiplayer debug overlay (expedition id/seed, phases, cargo holders).
    /// All values come from ExpeditionManager, which is host-fed on clients — every peer sees the
    /// same authoritative numbers.
    /// </summary>
    public class GameplayHUD : MonoBehaviour
    {
        [Header("Runtime (read-only)")]
        public bool showDebugOverlay;

        private static readonly string[] TiltNames = { "STABLE", "UNSTABLE", "CRITICAL TILT", "CAPSIZING" };
        private static readonly string[] WeightNames = { "NORMAL LOAD", "HEAVY LOAD", "OVERLOADED", "CRITICAL OVERLOAD" };
        private static readonly string[] ThreatNames = { "Calm", "Uneasy", "Hunted", "Storm", "WRATH" };

        private readonly StringBuilder sb = new StringBuilder(256);
        private GUIStyle panelStyle, warnStyle;

        private void Update()
        {
            var k = Keyboard.current;
            if (k == null) return;
            if (k.f3Key.wasPressedThisFrame) showDebugOverlay = !showDebugOverlay;

            // Dismiss the results screen with Enter/Escape (keyboard-only: the hub cursor stays locked).
            var manager = ExpeditionManager.Instance;
            if (manager != null && manager.resultsPending &&
                (k.enterKey.wasPressedThisFrame || k.numpadEnterKey.wasPressedThisFrame || k.escapeKey.wasPressedThisFrame))
                manager.resultsPending = false;
        }

        private void OnGUI()
        {
            var manager = ExpeditionManager.Instance;
            if (manager == null) return;
            string scene = SceneManager.GetActiveScene().name;
            if (scene == "MainMenuScene") return;

            EnsureStyles();

            bool inMission = manager.runtime.phase == ExpeditionPhase.Active ||
                             manager.runtime.phase == ExpeditionPhase.ReturnReady;

            if (inMission && scene != "HubScene")
            {
                DrawObjectiveTracker(manager);
                DrawShipStatus(manager);
                DrawReturnPrompt(manager);
            }

            if (scene == "HubScene")
            {
                DrawHubResources(manager);
                if (manager.resultsPending && manager.lastResults != null)
                    DrawResultsScreen(manager.lastResults);
            }

            if (showDebugOverlay)
                DrawDebugOverlay(manager);
        }

        // ----------------------------------------------------------------------------------------

        private void DrawObjectiveTracker(ExpeditionManager manager)
        {
            var rt = manager.runtime;
            var def = rt.Definition;

            sb.Length = 0;
            sb.AppendLine(def != null ? def.title.ToUpperInvariant() : "EXPEDITION");
            if (def != null && def.objective != null)
                sb.AppendLine(def.objective.description);

            if (rt.objectiveCargoRecovered)
                sb.AppendLine("Objective: SECURED — return to port!");
            else if (def != null && def.objective != null && def.objective.kind == ObjectiveKind.CollectCargoCount)
                sb.AppendLine($"Objective: {rt.objectiveCount} / {rt.objectiveRequired} aboard");
            else
                sb.AppendLine(rt.objectiveItemPickedUp ? "Objective: item found — get it to the ship!"
                                                       : "Objective: locate the target");

            int threat = Mathf.Clamp(rt.threatLevel, 0, ThreatNames.Length - 1);
            sb.AppendLine($"Threat: [{new string('#', threat)}{new string('-', 4 - threat)}] {ThreatNames[threat]}");

            // The traveling storm cell: how far its edge is from the ship (chart shows its path).
            if (rt.stormActive)
            {
                var ship = GameObject.Find("ShipRoot");
                if (ship != null)
                {
                    Vector3 p = ship.transform.position;
                    Vector2 toStorm = new Vector2(rt.stormCenter.x - p.x, rt.stormCenter.z - p.z);
                    float edge = toStorm.magnitude - rt.stormRadius;
                    sb.AppendLine(edge <= 0f
                        ? ">> INSIDE THE STORM <<"
                        : $"Storm cell: {edge / 1000f:0.0} km {Compass(toStorm)} (see chart)");
                }
            }

            int mins = Mathf.FloorToInt(rt.elapsedSeconds / 60f);
            sb.Append($"Elapsed: {mins:00}:{Mathf.FloorToInt(rt.elapsedSeconds % 60f):00}");

            // Inside a whisper fog bank the instruments lie (WhisperFogSystem garbles readouts).
            GUI.Box(new Rect(12f, 12f, 340f, 134f), WhisperFogSystem.Garble(sb.ToString()), panelStyle);
        }

        /// <summary>World-axis compass label for a horizontal offset (+Z = N, +X = E).</summary>
        private static string Compass(Vector2 dir)
        {
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            float ang = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg; // 0 = +Z
            int idx = Mathf.RoundToInt(((ang + 360f) % 360f) / 45f) % 8;
            return names[idx];
        }

        private void DrawShipStatus(ExpeditionManager manager)
        {
            var rt = manager.runtime;
            var balance = FindAnyObjectByType<ShipBalanceController>();

            sb.Length = 0;
            sb.AppendLine("— SHIP —");
            if (balance != null)
            {
                sb.AppendLine($"Cargo weight: {balance.totalWeight:0} / {balance.maxCapacity:0}  ({balance.loadPercent:P0})");
                float tilt = Mathf.Max(Mathf.Abs(balance.rollImbalance), Mathf.Abs(balance.pitchImbalance));
                sb.AppendLine($"Tilt: {tilt:P0} of max  (roll {balance.rollImbalance:+0.00;-0.00}, pitch {balance.pitchImbalance:+0.00;-0.00})");
            }
            int ti = Mathf.Clamp((int)rt.tiltState, 0, TiltNames.Length - 1);
            int wi = Mathf.Clamp((int)rt.weightState, 0, WeightNames.Length - 1);
            sb.AppendLine($"Trim: {TiltNames[ti]}    Load: {WeightNames[wi]}");
            sb.Append($"Fuel: {manager.progress.fuel:0.0}    Hull damage: {manager.progress.hullDamage:0}");

            bool alarmed = rt.tiltState >= ShipTiltState.Critical || rt.weightState >= ShipWeightState.Overloaded;
            GUI.Box(new Rect(12f, Screen.height - 112f, 400f, 100f),
                    WhisperFogSystem.Garble(sb.ToString()), alarmed ? warnStyle : panelStyle);
        }

        private void DrawReturnPrompt(ExpeditionManager manager)
        {
            string prompt = null;
            if (manager.runtime.phase == ExpeditionPhase.ReturnReady)
                prompt = "OBJECTIVE SECURED — ring the Return to Port bell by the helm to head home.";
            else
            {
                var station = FindAnyObjectByType<ShipReturnStation>();
                if (station != null && station.ConfirmArmed)
                    prompt = "Ring the bell again to ABANDON the expedition.";
            }
            if (prompt == null) return;

            GUI.Box(new Rect((Screen.width - 560f) * 0.5f, Screen.height - 160f, 560f, 34f), prompt, warnStyle);
        }

        private void DrawHubResources(ExpeditionManager manager)
        {
            var p = manager.progress;
            sb.Length = 0;
            sb.AppendLine("— PORT LEDGER —");
            sb.AppendLine($"Money: {p.money}    Scrap: {p.scrap}");
            sb.AppendLine($"Fuel: {p.fuel:0.0}    Chart fragments: {p.chartFragments}");
            sb.Append($"Hull damage: {p.hullDamage:0}");
            GUI.Box(new Rect(Screen.width - 292f, 12f, 280f, 86f), sb.ToString(), panelStyle);
        }

        private void DrawResultsScreen(ExpeditionResults r)
        {
            sb.Length = 0;
            sb.AppendLine($"EXPEDITION REPORT — {r.title}");
            sb.AppendLine(r.success ? ">> SUCCESS <<" : $">> {r.outcome.ToUpperInvariant()} <<");
            sb.AppendLine();
            sb.AppendLine($"Cargo recovered: {r.cargoRecovered}");
            sb.AppendLine($"Money earned:    {r.moneyEarned}");
            sb.AppendLine($"Scrap earned:    {r.scrapEarned}");
            sb.AppendLine($"Fuel recovered:  {r.fuelRecovered:0.#}");
            sb.AppendLine($"Chart fragments: {r.chartFragmentsGained}");
            if (r.newLeads.Count > 0)
                sb.AppendLine("NEW LEADS: " + string.Join(", ", r.newLeads));
            if (!string.IsNullOrEmpty(r.consequences))
                sb.AppendLine("Consequences: " + r.consequences);
            sb.AppendLine();
            sb.Append("[ Enter to close ]");

            float w = 460f, h = 300f;
            GUI.Box(new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h), sb.ToString(), panelStyle);
        }

        private void DrawDebugOverlay(ExpeditionManager manager)
        {
            var rt = manager.runtime;
            var nm = NetworkManagerP2P.Instance;

            sb.Length = 0;
            sb.AppendLine("— DEBUG (F3) —");
            sb.AppendLine($"Role: {(nm == null ? "no-net" : nm.isHost ? "HOST" : nm.isConnected ? "CLIENT" : "solo")}   Id: {(nm != null ? nm.localPlayerId : "-")}");
            sb.AppendLine($"Expedition: '{rt.selectedExpeditionId}'  seed={rt.worldSeed}  phase={rt.phase}");
            sb.AppendLine($"Objective: {rt.objectiveCount}/{rt.objectiveRequired}  pickedUp={rt.objectiveItemPickedUp}  recovered={rt.objectiveCargoRecovered}");
            sb.AppendLine($"Threat={rt.threatLevel}  tilt={rt.tiltState}  weight={rt.weightState}  corruption={manager.corruptionAboard:0.#}");

            // Who is holding what (local + remote, from the network manager's last-known state).
            sb.AppendLine("Held cargo:");
            var localPi = FindAnyObjectByType<PlayerInteraction>();
            if (localPi != null && localPi.HeldItem != null)
                sb.AppendLine($"  {(nm != null ? nm.localPlayerId : "local")} -> {localPi.HeldItem.name}");
            if (nm != null)
            {
                foreach (var kvp in nm.GetHeldCargoByPlayer())
                    if (!string.IsNullOrEmpty(kvp.Value))
                        sb.AppendLine($"  {kvp.Key} -> {kvp.Value}");
            }

            sb.Append("Keys: F6 complete obj | F7 return | F8 spawn cargo | F9 threat+ | F10 force overload");
            GUI.Box(new Rect(Screen.width - 560f, Screen.height - 190f, 548f, 178f), sb.ToString(), panelStyle);
        }

        private void EnsureStyles()
        {
            if (panelStyle == null)
            {
                panelStyle = new GUIStyle(GUI.skin.box)
                {
                    alignment = TextAnchor.UpperLeft,
                    fontSize = 13,
                    padding = new RectOffset(10, 10, 8, 8)
                };
                panelStyle.normal.textColor = Color.white;
            }
            if (warnStyle == null)
            {
                warnStyle = new GUIStyle(panelStyle);
                warnStyle.normal.textColor = new Color(1f, 0.55f, 0.35f);
                warnStyle.fontStyle = FontStyle.Bold;
            }
        }
    }
}

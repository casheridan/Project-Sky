using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// The hub's Sky Chart: the station where the crew picks the next expedition. Attached at
    /// runtime to the hub's authored menu board (Board_Panel) by VerticalSliceBootstrap — pressing
    /// F while looking at the board (PlayerInteraction routes it here) opens the chart UI.
    ///
    /// The UI is deliberately simple IMGUI (matching PauseMenuManager): while open it frees the
    /// cursor and suspends the local controller/interaction. The HOST selects an expedition and
    /// runs port services (repair/fuel); CLIENTS open the same screen read-only and watch the
    /// host's selection live (synced via ExpeditionNetState). Launch itself stays on the existing
    /// all-aboard countdown (HubController) — boarding the ship IS the ready check.
    /// </summary>
    public class SkyChartTable : MonoBehaviour
    {
        [Header("Runtime (read-only)")]
        public bool isOpen;

        private FirstPersonController viewerController;
        private PlayerInteraction viewerInteraction;
        private Vector2 scroll;
        private int exitedFrame = -1;

        /// <summary>
        /// Install the chart on the hub's menu board. Idempotent. Falls back to a runtime-built
        /// pedestal in front of the player spawn if the authored board is missing.
        /// </summary>
        public static SkyChartTable CreateInHub()
        {
            var existing = FindAnyObjectByType<SkyChartTable>();
            if (existing != null) return existing;

            GameObject anchor = GameObject.Find("Board_Panel");
            if (anchor == null)
                anchor = BuildFallbackPedestal();
            if (anchor == null) return null;

            var table = anchor.AddComponent<SkyChartTable>();

            // Make sure the interaction ray can find the board: add a slightly proud trigger if
            // the anchor has no collider of its own.
            if (anchor.GetComponent<Collider>() == null)
            {
                var box = anchor.AddComponent<BoxCollider>();
                box.isTrigger = true;
                box.size = Vector3.one * 1.2f;
            }
            Debug.Log("[SkyChartTable] Sky Chart installed on " + anchor.name);
            return table;
        }

        private static GameObject BuildFallbackPedestal()
        {
            var player = GameObject.Find("Player");
            if (player == null) return null;

            Vector3 pos = player.transform.position + player.transform.forward * 2.5f;
            if (Physics.Raycast(pos + Vector3.up * 2f, Vector3.down, out RaycastHit hit, 10f))
                pos = hit.point;

            var pedestal = GameObject.CreatePrimitive(PrimitiveType.Cube);
            pedestal.name = "SkyChartPedestal";
            pedestal.transform.position = pos + Vector3.up * 0.6f;
            pedestal.transform.localScale = new Vector3(0.9f, 1.2f, 0.6f);
            return pedestal;
        }

        /// <summary>Called by PlayerInteraction when the local player presses F at the board.</summary>
        public void ToggleView(GameObject player)
        {
            if (isOpen) Close();
            else if (Time.frameCount != exitedFrame) Open(player);
        }

        private void Open(GameObject player)
        {
            if (player == null) return;
            viewerController = player.GetComponent<FirstPersonController>();
            viewerInteraction = player.GetComponent<PlayerInteraction>();

            if (viewerInteraction != null)
            {
                viewerInteraction.DropHeld();
                viewerInteraction.enabled = false;
            }
            if (viewerController != null) viewerController.enabled = false;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            isOpen = true;
        }

        private void Close()
        {
            isOpen = false;
            exitedFrame = Time.frameCount;
            if (viewerController != null) viewerController.enabled = true;
            if (viewerInteraction != null) viewerInteraction.enabled = true;
            viewerController = null;
            viewerInteraction = null;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            if (!isOpen) return;
            var k = Keyboard.current;
            if (k != null && (k.fKey.wasPressedThisFrame || k.escapeKey.wasPressedThisFrame))
                Close();
        }

        private void OnGUI()
        {
            if (!isOpen) return;
            var manager = ExpeditionManager.Instance;
            if (manager == null) return;
            bool host = manager.IsAuthority;

            float w = Mathf.Min(760f, Screen.width - 60f);
            float h = Mathf.Min(560f, Screen.height - 60f);
            Rect area = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);

            GUILayout.BeginArea(area, "SKY CHART — TRADE WINDS", GUI.skin.window);
            GUILayout.Space(6);

            DrawResourceRow(manager);
            GUILayout.Space(6);

            GUILayout.BeginHorizontal();

            // ---- Left column: expedition list ----
            GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(w * 0.42f));
            GUILayout.Label(host ? "Choose an expedition:" : "Expeditions (host selects):");
            scroll = GUILayout.BeginScrollView(scroll);
            foreach (var def in ExpeditionDatabase.AllExpeditions)
            {
                if (!manager.progress.IsUnlocked(def.expeditionId)) continue;

                bool selected = manager.runtime.selectedExpeditionId == def.expeditionId;
                string label = (selected ? "> " : "") + def.title
                             + (def.isPlaceholder ? "  [LOCKED LEAD]" : "")
                             + (manager.progress.IsCompleted(def.expeditionId) ? "  (done)" : "");

                GUI.enabled = host && !def.isPlaceholder;
                if (GUILayout.Button(label, GUILayout.Height(34)) && host && !def.isPlaceholder)
                    manager.SelectExpedition(def.expeditionId);
                GUI.enabled = true;
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            // ---- Right column: details + port services ----
            GUILayout.BeginVertical(GUI.skin.box);
            var sel = ExpeditionDatabase.GetExpedition(manager.runtime.selectedExpeditionId);
            if (sel != null)
            {
                var region = sel.Region;
                GUILayout.Label($"<b>{sel.title}</b>", RichLabel());
                GUILayout.Label($"Region: {(region != null ? region.displayName : sel.regionId)}");
                GUILayout.Label($"Danger: {new string('!', sel.dangerRating)}   Fuel cost: {sel.fuelCost:0.#}");
                GUILayout.Space(4);
                GUILayout.Label(sel.description, GUILayout.ExpandHeight(false));
                GUILayout.Space(4);
                if (sel.objective != null)
                    GUILayout.Label("Objective: " + sel.objective.description);
                if (sel.reward != null)
                    GUILayout.Label($"Reward: {sel.reward.money} money, {sel.reward.scrap} scrap" +
                                    (sel.reward.fuel > 0 ? $", {sel.reward.fuel:0.#} fuel" : "") +
                                    (sel.reward.chartFragments > 0 ? $", {sel.reward.chartFragments} chart fragment(s)" : ""));

                GUILayout.Space(8);
                if (manager.CanLaunch(out string reason))
                    GUILayout.Label("READY — board the ship with your whole crew to launch.");
                else
                    GUILayout.Label(reason);

                if (host && GUILayout.Button("Clear selection"))
                    manager.ClearSelection();
            }
            else
            {
                GUILayout.Label(host ? "Select an expedition on the left."
                                     : "Waiting for the host to choose an expedition...");
            }

            GUILayout.FlexibleSpace();

            // Port services (host validates; buttons disabled for clients).
            GUILayout.Label("— PORT SERVICES —");
            GUI.enabled = host;
            if (GUILayout.Button($"Repair hull  (-{manager.repairScrapCost} scrap, hull damage {manager.progress.hullDamage:0})"))
            {
                if (!manager.HostRepairHull())
                    NetworkManagerP2P.Instance?.ShowBanner("Can't repair: need scrap and damage to fix.", 3f);
            }
            if (GUILayout.Button($"Buy fuel  (-{manager.fuelMoneyCost} money → +{manager.fuelPurchaseAmount:0.#} fuel)"))
            {
                if (!manager.HostBuyFuel())
                    NetworkManagerP2P.Instance?.ShowBanner("Can't buy fuel: not enough money.", 3f);
            }
            GUI.enabled = true;

            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            if (GUILayout.Button("Close  (F / Esc)", GUILayout.Height(30)))
                Close();

            GUILayout.EndArea();
        }

        private void DrawResourceRow(ExpeditionManager manager)
        {
            var p = manager.progress;
            GUILayout.Label($"Money: {p.money}    Scrap: {p.scrap}    Fuel: {p.fuel:0.#}    " +
                            $"Chart fragments: {p.chartFragments}    Hull damage: {p.hullDamage:0}");
        }

        private static GUIStyle richLabel;
        private static GUIStyle RichLabel()
        {
            if (richLabel == null)
                richLabel = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 16 };
            return richLabel;
        }
    }
}

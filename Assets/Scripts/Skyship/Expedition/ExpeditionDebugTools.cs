using UnityEngine;
using UnityEngine.InputSystem;

namespace Skyship
{
    /// <summary>
    /// Multiplayer test hotkeys for the vertical slice (created by VerticalSliceBootstrap,
    /// persistent). All commands run on the AUTHORITY only (host/solo) — the resulting state
    /// reaches clients through the normal sync paths, which is exactly what we want to test.
    ///
    ///   F3  — HUD debug overlay (handled by GameplayHUD)
    ///   F6  — force-complete the current objective
    ///   F7  — return to hub immediately
    ///   F8  — spawn a test cargo crate in front of the local player (cycles definitions)
    ///   F9  — force threat escalation (+1 level)
    ///   F10 — cycle forced weight state on the stress system (off → CriticalOverload → off...)
    ///   F11 — cycle forced tilt state (off → Critical → off...)
    /// </summary>
    public class ExpeditionDebugTools : MonoBehaviour
    {
        private static readonly string[] SpawnCycle =
        {
            CargoDatabase.SmallScrapCrate,
            CargoDatabase.MediumScrapCrate,
            CargoDatabase.FuelCell,
            CargoDatabase.HeavyCursedStatue,
            CargoDatabase.BlackNavigationBox
        };
        private int spawnIndex;
        private int debugCargoCounter;

        private void Update()
        {
            var k = Keyboard.current;
            var manager = ExpeditionManager.Instance;
            if (k == null || manager == null || !manager.IsAuthority) return;

            if (k.f6Key.wasPressedThisFrame) ForceCompleteObjective(manager);
            if (k.f7Key.wasPressedThisFrame) ForceReturn(manager);
            if (k.f8Key.wasPressedThisFrame) SpawnTestCargo();
            if (k.f9Key.wasPressedThisFrame) ForceThreat();
            if (k.f10Key.wasPressedThisFrame) CycleForcedWeight();
            if (k.f11Key.wasPressedThisFrame) CycleForcedTilt();
        }

        private void ForceCompleteObjective(ExpeditionManager manager)
        {
            if (manager.runtime.phase != ExpeditionPhase.Active) return;
            manager.runtime.objectiveCount = manager.runtime.objectiveRequired;
            manager.runtime.objectiveItemPickedUp = true;
            manager.runtime.objectiveCargoRecovered = true;
            manager.runtime.phase = ExpeditionPhase.ReturnReady;
            manager.BroadcastEvent("[debug] Objective force-completed.");
        }

        private void ForceReturn(ExpeditionManager manager)
        {
            if (manager.runtime.phase != ExpeditionPhase.Active &&
                manager.runtime.phase != ExpeditionPhase.ReturnReady) return;
            Debug.Log("[ExpeditionDebugTools] Forcing return to hub.");
            manager.HostExecuteReturn();
        }

        private void SpawnTestCargo()
        {
            var player = GameObject.Find("Player");
            if (player == null) return;

            var def = CargoDatabase.Get(SpawnCycle[spawnIndex % SpawnCycle.Length]);
            spawnIndex++;
            Vector3 pos = player.transform.position + player.transform.forward * 2f + Vector3.up * 1.2f;

            // In a generated world go through WorldGenerator so the crate is host-synced by name;
            // elsewhere (hub) build the cube directly — local-only, debug is fine with that.
            var gen = FindAnyObjectByType<WorldGenerator>();
            if (gen != null)
            {
                var item = gen.SpawnDefinedCargoNamed($"DbgCargo_{debugCargoCounter++:000}", def, pos);
                NetworkManagerP2P.Instance?.RefreshCargoRegistry();
                Debug.Log($"[ExpeditionDebugTools] Spawned '{item.name}' ({def.displayName}).");
            }
            else
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
                go.name = $"DbgCargo_{debugCargoCounter++:000}";
                go.transform.position = pos;
                go.transform.localScale = Vector3.one * def.visualScale;
                var rend = go.GetComponent<Renderer>();
                Shader shader = Shader.Find("Universal Render Pipeline/Lit");
                if (shader == null) shader = Shader.Find("Standard");
                var mat = new Material(shader) { color = def.color };
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", def.color);
                rend.sharedMaterial = mat;
                var item = go.AddComponent<CargoItem>();
                def.ApplyTo(item);
                if (def.isObjectiveItem) ObjectiveBeacon.AttachTo(go);
                NetworkManagerP2P.Instance?.RefreshCargoRegistry();
                Debug.Log($"[ExpeditionDebugTools] Spawned local '{go.name}' ({def.displayName}).");
            }
        }

        private void ForceThreat()
        {
            var director = FindAnyObjectByType<ExpeditionThreatDirector>();
            if (director != null) director.ForceEscalate();
            else Debug.Log("[ExpeditionDebugTools] No threat director in this scene.");
        }

        private void CycleForcedWeight()
        {
            var stress = FindAnyObjectByType<ShipStressSystem>();
            if (stress == null) { Debug.Log("[ExpeditionDebugTools] No stress system in this scene."); return; }
            stress.forcedWeightState = stress.forcedWeightState < 0 ? (int)ShipWeightState.CriticalOverload : -1;
            Debug.Log($"[ExpeditionDebugTools] forcedWeightState = {stress.forcedWeightState}");
        }

        private void CycleForcedTilt()
        {
            var stress = FindAnyObjectByType<ShipStressSystem>();
            if (stress == null) { Debug.Log("[ExpeditionDebugTools] No stress system in this scene."); return; }
            stress.forcedTiltState = stress.forcedTiltState < 0 ? (int)ShipTiltState.Critical : -1;
            Debug.Log($"[ExpeditionDebugTools] forcedTiltState = {stress.forcedTiltState}");
        }
    }
}

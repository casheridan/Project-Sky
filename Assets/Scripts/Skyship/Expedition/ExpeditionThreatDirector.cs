using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// The expedition's escalation clock. HOST-OWNED: the host computes the threat level from
    /// elapsed time, objective progress, and corruption aboard, and fires one-shot events
    /// (banner + log for now — audio/effects hooks later). The level itself reaches clients in
    /// ExpeditionNetState; every peer applies the same ambience (fog closing in) locally from
    /// that synced level, so the world darkens for the whole crew together.
    ///
    /// Slice escalation:
    ///   L1  after a few minutes adrift (the region notices you)
    ///   L2  the objective item has been picked up (alarm/horror beat)
    ///   L3  the objective item is aboard the ship (escape pressure — storm rising)
    ///   L4  overstaying (or hauling heavy corruption) keeps squeezing
    ///
    /// Created in the world scene by VerticalSliceBootstrap.
    /// </summary>
    public class ExpeditionThreatDirector : MonoBehaviour
    {
        [Header("Escalation timing")]
        [Tooltip("Seconds before ambient danger starts rising on its own.")]
        public float ambientEscalationTime = 180f;
        [Tooltip("Seconds of total expedition time before the overstay penalty level kicks in.")]
        public float overstayTime = 600f;
        [Tooltip("Corruption aboard that adds a threat level on its own.")]
        public float corruptionThreshold = 8f;

        [Header("Fog per threat level (lerped smoothly)")]
        [Tooltip("Multiplier on the base fog distances at max threat (fog closes in).")]
        public float maxThreatFogScale = 0.35f;
        public Color stormFogColor = new Color(0.32f, 0.30f, 0.40f);

        [Header("Runtime (read-only)")]
        public int threatLevel;

        // Host-side one-shot event flags.
        private bool firedAmbient, firedAlarm, firedEscape, firedOverstay;

        // Base ambience captured after WorldGenerator applies it, so we can scale from it.
        private float baseFogStart = -1f, baseFogEnd;
        private Color baseFogColor;

        private void Update()
        {
            var manager = ExpeditionManager.Instance;
            if (manager == null) return;
            var rt = manager.runtime;
            if (rt.phase != ExpeditionPhase.Active && rt.phase != ExpeditionPhase.ReturnReady)
                return;

            if (manager.IsAuthority)
                EvaluateThreat(manager);

            threatLevel = rt.threatLevel;
            ApplyAmbience(threatLevel);
        }

        /// <summary>Host: compute the level and fire one-shot escalation events.</summary>
        private void EvaluateThreat(ExpeditionManager manager)
        {
            var rt = manager.runtime;
            int level = 0;

            if (rt.elapsedSeconds >= ambientEscalationTime) level = Mathf.Max(level, 1);
            if (rt.objectiveItemPickedUp) level = Mathf.Max(level, 2);
            if (rt.objectiveCargoRecovered) level = Mathf.Max(level, 3);
            if (rt.elapsedSeconds >= overstayTime) level = Mathf.Min(level + 1, 4);
            if (manager.corruptionAboard >= corruptionThreshold) level = Mathf.Min(level + 1, 4);

            // Threat only ratchets upward within an expedition (debug escalation included).
            if (level > rt.threatLevel) rt.threatLevel = level;

            // One-shot beats.
            if (!firedAmbient && rt.threatLevel >= 1)
            {
                firedAmbient = true;
                manager.BroadcastEvent("The air is changing. Something knows you're here.");
            }
            if (!firedAlarm && rt.objectiveItemPickedUp && rt.threatLevel >= 2)
            {
                firedAlarm = true;
                manager.BroadcastEvent("!! The dead signal SCREAMS — every speaker on the wreck is live !!");
            }
            if (!firedEscape && rt.objectiveCargoRecovered && rt.threatLevel >= 3)
            {
                firedEscape = true;
                manager.BroadcastEvent("STORM RISING — get her home. NOW.");
            }
            if (!firedOverstay && rt.elapsedSeconds >= overstayTime)
            {
                firedOverstay = true;
                manager.BroadcastEvent("You have stayed too long.");
            }
        }

        /// <summary>Debug hook: bump the threat level by one (host only; see ExpeditionDebugTools).</summary>
        public void ForceEscalate()
        {
            var manager = ExpeditionManager.Instance;
            if (manager == null || !manager.IsAuthority) return;
            manager.runtime.threatLevel = Mathf.Min(manager.runtime.threatLevel + 1, 4);
            manager.BroadcastEvent($"[debug] Threat escalated to level {manager.runtime.threatLevel}.");
        }

        /// <summary>
        /// Every peer: close the fog in as danger rises. This is the SINGLE owner of
        /// RenderSettings.fog — it folds in both the synced threat level and how deep the local
        /// player currently is inside the physical storm cell (StormSystem.LocalStormProximity),
        /// whichever is worse, so the two systems never fight over the same values.
        /// </summary>
        private void ApplyAmbience(int level)
        {
            // Capture the generator's baseline the first time we run after generation.
            if (baseFogStart < 0f)
            {
                if (!RenderSettings.fog) return; // generator hasn't applied ambience yet
                baseFogStart = RenderSettings.fogStartDistance;
                baseFogEnd = RenderSettings.fogEndDistance;
                baseFogColor = RenderSettings.fogColor;
            }

            float t = Mathf.Clamp01(Mathf.Max(level / 4f, StormSystem.LocalStormProximity));
            float scale = Mathf.Lerp(1f, maxThreatFogScale, t);
            float lerpSpeed = 0.4f * Time.deltaTime; // ease so escalation feels like weather, not a switch

            RenderSettings.fogStartDistance = Mathf.Lerp(RenderSettings.fogStartDistance, baseFogStart * scale, lerpSpeed);
            RenderSettings.fogEndDistance = Mathf.Lerp(RenderSettings.fogEndDistance, baseFogEnd * scale, lerpSpeed);
            RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor, Color.Lerp(baseFogColor, stormFogColor, t), lerpSpeed);
        }
    }
}

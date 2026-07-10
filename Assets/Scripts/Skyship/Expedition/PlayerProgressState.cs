using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Shared campaign progress: resources, ship condition, and which expeditions are
    /// unlocked/completed. HOST-AUTHORITATIVE — the host's save IS the campaign; clients keep a
    /// display-only mirror fed by the expedition sync blob (see NetworkManagerP2P State packets).
    /// Persisted as JSON in PlayerPrefs on the host.
    /// </summary>
    [Serializable]
    public class PlayerProgressState
    {
        private const string PrefsKey = "Skyjackers.Progress.v1";

        public int money = 100;
        public int scrap = 20;
        public float fuel = 6f;
        public int chartFragments = 0;
        [Tooltip("Accumulated hull damage (0 = pristine). Repaired with scrap in the hub.")]
        public float hullDamage = 0f;

        public List<string> unlockedExpeditionIds = new List<string>();
        public List<string> completedExpeditionIds = new List<string>();

        public bool IsUnlocked(string expeditionId)
        {
            var def = ExpeditionDatabase.GetExpedition(expeditionId);
            if (def != null && def.unlockedByDefault) return true;
            return unlockedExpeditionIds.Contains(expeditionId);
        }

        public bool IsCompleted(string expeditionId) => completedExpeditionIds.Contains(expeditionId);

        public void Unlock(string expeditionId)
        {
            if (!string.IsNullOrEmpty(expeditionId) && !unlockedExpeditionIds.Contains(expeditionId))
                unlockedExpeditionIds.Add(expeditionId);
        }

        public void MarkCompleted(string expeditionId)
        {
            if (!string.IsNullOrEmpty(expeditionId) && !completedExpeditionIds.Contains(expeditionId))
                completedExpeditionIds.Add(expeditionId);
        }

        public void Save()
        {
            PlayerPrefs.SetString(PrefsKey, JsonUtility.ToJson(this));
            PlayerPrefs.Save();
        }

        public static PlayerProgressState Load()
        {
            string json = PlayerPrefs.GetString(PrefsKey, "");
            if (!string.IsNullOrEmpty(json))
            {
                try
                {
                    var state = JsonUtility.FromJson<PlayerProgressState>(json);
                    if (state != null) return state;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[PlayerProgressState] Corrupt save, starting fresh: " + e.Message);
                }
            }
            return new PlayerProgressState();
        }
    }
}

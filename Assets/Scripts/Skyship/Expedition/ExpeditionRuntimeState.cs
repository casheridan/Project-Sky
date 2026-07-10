using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>Where the crew is in the expedition loop.</summary>
    public enum ExpeditionPhase
    {
        /// <summary>In the hub, nothing selected/active.</summary>
        None = 0,
        /// <summary>In the hub with an expedition selected on the Sky Chart (pre-launch).</summary>
        Preparing = 1,
        /// <summary>Out in the generated mission world, objective not yet complete.</summary>
        Active = 2,
        /// <summary>Objective complete — Return to Port is available for full success.</summary>
        ReturnReady = 3
    }

    /// <summary>Escalating tilt consequences (see ShipStressSystem).</summary>
    public enum ShipTiltState { Stable = 0, Unstable = 1, Critical = 2, Capsizing = 3 }

    /// <summary>Escalating total-load consequences (see ShipStressSystem).</summary>
    public enum ShipWeightState { Normal = 0, Heavy = 1, Overloaded = 2, CriticalOverload = 3 }

    /// <summary>
    /// Live, host-authoritative state of the current expedition. The host mutates it; every field
    /// clients need for UI rides to them inside ExpeditionNetState (the sync blob in State packets).
    /// </summary>
    [Serializable]
    public class ExpeditionRuntimeState
    {
        public ExpeditionPhase phase = ExpeditionPhase.None;
        public string selectedExpeditionId = "";
        public int worldSeed;
        public float elapsedSeconds;

        // Objective tracking (host-written).
        public int objectiveCount;          // matching items currently on the ship
        public int objectiveRequired;
        public bool objectiveItemPickedUp;  // the objective cargo has been held at least once
        public bool objectiveCargoRecovered;

        // Threat director + ship stress (host-written, mirrored for HUD/FX on clients).
        public int threatLevel;
        public ShipTiltState tiltState = ShipTiltState.Stable;
        public ShipWeightState weightState = ShipWeightState.Normal;

        // The physical storm cell (host-simulated by StormSystem; clients place the visual and
        // compute local proximity FX from these).
        public bool stormActive;
        public Vector3 stormCenter;
        public float stormRadius;

        // Eldritch threats (host-simulated; clients mirror for local FX).
        public bool screamActive;      // StaticScreamSystem: the nav box is screaming right now
        public int leviathanState;     // LeviathanSystem: 0 dormant, 1 restless, 2 shadowing, 3 breaching
        public string barnaclesCsv = ""; // BarnacleSystem: "id,x,y,z,w;..." hull growths

        public ExpeditionDefinition Definition => ExpeditionDatabase.GetExpedition(selectedExpeditionId);

        public void ResetToHub()
        {
            phase = ExpeditionPhase.None;
            selectedExpeditionId = "";
            worldSeed = 0;
            elapsedSeconds = 0f;
            objectiveCount = 0;
            objectiveRequired = 0;
            objectiveItemPickedUp = false;
            objectiveCargoRecovered = false;
            threatLevel = 0;
            tiltState = ShipTiltState.Stable;
            weightState = ShipWeightState.Normal;
            stormActive = false;
            stormCenter = Vector3.zero;
            stormRadius = 0f;
            screamActive = false;
            leviathanState = 0;
            barnaclesCsv = "";
        }
    }

    /// <summary>Outcome summary shown on the results screen back in the hub.</summary>
    [Serializable]
    public class ExpeditionResults
    {
        public string expeditionId;
        public string title;
        public bool success;
        public string outcome;          // "Success" / "Abandoned" / "Partial"
        public int cargoRecovered;
        public int moneyEarned;
        public int scrapEarned;
        public float fuelRecovered;
        public int chartFragmentsGained;
        public List<string> newLeads = new List<string>();
        public string consequences = "";
    }

    /// <summary>
    /// The host→client sync blob. Serialized with JsonUtility into a single string field of the
    /// 20 Hz State packet, so extending it never touches the packet schema again. Carries both
    /// the live expedition state and the shared-progress mirror for client UI.
    /// </summary>
    [Serializable]
    public class ExpeditionNetState
    {
        public string selectedExpeditionId = "";
        public int phase;
        public float elapsedSeconds;
        public int objectiveCount;
        public int objectiveRequired;
        public bool objectiveItemPickedUp;
        public bool objectiveCargoRecovered;
        public int threatLevel;
        public int tiltState;
        public int weightState;

        // Storm cell (host-simulated).
        public bool stormActive;
        public Vector3 stormCenter;
        public float stormRadius;

        // Eldritch threats (host-simulated).
        public bool screamActive;
        public int leviathanState;
        public string barnaclesCsv = "";

        // Shared campaign progress mirror (host save → client display).
        public int money;
        public int scrap;
        public float fuel;
        public int chartFragments;
        public float hullDamage;
        public string unlockedCsv = "";
        public string completedCsv = "";
    }
}

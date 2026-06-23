using UnityEngine;

namespace Skyship
{
    /// <summary>Staged warning levels for the ship.</summary>
    public enum ShipState
    {
        Normal,
        Strained,
        Dangerous,
        Critical
    }

    /// <summary>
    /// Watches load + imbalance from ShipBalanceController and reports a staged
    /// warning state. Does NOT damage or destroy the ship yet — it only logs
    /// state changes and exposes the state/text for future UI.
    ///
    /// SCENE SETUP:
    ///  - Add to ShipRoot.
    ///  - 'balance' auto-binds to the controller on the same object if left empty.
    /// </summary>
    public class ShipFailureMonitor : MonoBehaviour
    {
        [Header("References")]
        public ShipBalanceController balance;

        [Header("Load thresholds (fraction of capacity)")]
        public float strainedLoad = 0.7f;
        public float dangerousLoad = 0.9f;
        public float criticalLoad = 1.1f;

        [Header("Imbalance thresholds (0..1, uses the larger of roll/pitch)")]
        public float strainedImbalance = 0.4f;
        public float dangerousImbalance = 0.7f;
        public float criticalImbalance = 0.9f;

        [Header("Runtime (read-only)")]
        [SerializeField] private ShipState state = ShipState.Normal;
        [SerializeField] private string warningText = "All systems nominal.";

        public ShipState State => state;
        public string WarningText => warningText;

        private void Awake()
        {
            if (balance == null)
                balance = GetComponent<ShipBalanceController>();
        }

        private void Update()
        {
            if (balance == null) return;

            float load = balance.loadPercent;
            float imbalance = Mathf.Max(Mathf.Abs(balance.rollImbalance), Mathf.Abs(balance.pitchImbalance));

            ShipState newState = Evaluate(load, imbalance);
            if (newState != state)
            {
                state = newState;
                warningText = BuildWarning(state);
                Debug.Log($"[ShipFailureMonitor] State -> {state} (load {load:P0}, imbalance {imbalance:0.00})");
            }
        }

        private ShipState Evaluate(float load, float imbalance)
        {
            if (load >= criticalLoad || imbalance >= criticalImbalance) return ShipState.Critical;
            if (load >= dangerousLoad || imbalance >= dangerousImbalance) return ShipState.Dangerous;
            if (load >= strainedLoad || imbalance >= strainedImbalance) return ShipState.Strained;
            return ShipState.Normal;
        }

        private string BuildWarning(ShipState s)
        {
            switch (s)
            {
                case ShipState.Strained: return "Load increasing — watch your balance.";
                case ShipState.Dangerous: return "WARNING: dangerous load / imbalance!";
                case ShipState.Critical: return "CRITICAL: ship overloaded / tipping!";
                default: return "All systems nominal.";
            }
        }
    }
}

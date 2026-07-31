using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Weather-aware deck traction for loose cargo. Unity's stock collider friction is replaced
    /// with a deterministic Coulomb-friction step so dry, rain, ice, and sand can materially
    /// change when each cargo type starts sliding. Only the world authority drives rigidbodies.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public class ShipDeckSurfaceController : MonoBehaviour
    {
        public enum SurfaceCondition
        {
            Dry,
            Rain,
            Ice,
            Sand
        }

        [Header("References")]
        public ShipPlatformArea platformArea;
        public Transform shipVisualRoot;

        [Header("Environment")]
        [Tooltip("Condition used when no storm is wetting the deck. Ice and Sand are never replaced by automatic rain.")]
        public SurfaceCondition baseCondition = SurfaceCondition.Dry;
        [Tooltip("Automatically make a Dry deck rainy while the ship is inside a StormSystem cell.")]
        public bool rainFromStorm = true;
        [Range(0f, 1f)] public float stormWetThreshold = 0.08f;

        [Header("Grip coefficients")]
        [Tooltip("Dry timber/metal. Slides only once the deck is meaningfully tilted.")]
        [Range(0f, 1.5f)] public float dryGrip = 0.28f;
        [Tooltip("Rain reduces traction and increases load transferred to a railing.")]
        [Range(0f, 1.5f)] public float rainGrip = 0.14f;
        [Tooltip("Ice is nearly frictionless.")]
        [Range(0f, 1.5f)] public float iceGrip = 0.025f;
        [Tooltip("Sand and grit are beneficial: they substantially increase traction.")]
        [Range(0f, 1.5f)] public float sandGrip = 0.65f;

        [Header("Barrier coupling")]
        [Tooltip("How strongly deck grip subtracts from sustained load reaching a barrier.")]
        [Range(0f, 1f)] public float pressureGripScale = 0.65f;
        [Tooltip("Minimum fraction of tilted cargo weight that can reach a barrier even on a very grippy deck.")]
        [Range(0f, 1f)] public float minimumLoadTransmission = 0.2f;

        [Header("Runtime (read-only)")]
        [SerializeField] private SurfaceCondition currentCondition;
        [SerializeField] private float currentGrip;

        private StormSystem storm;
        private PhysicsMaterial zeroFrictionMaterial;
        private readonly Dictionary<CargoItem, PhysicsMaterial> originalCargoMaterials =
            new Dictionary<CargoItem, PhysicsMaterial>();
        private readonly HashSet<CargoItem> aboardNow = new HashSet<CargoItem>();
        private readonly List<CargoItem> restoreBuffer = new List<CargoItem>();

        public SurfaceCondition CurrentCondition => currentCondition;
        public float CurrentGrip => currentGrip;

        public void Configure(ShipPlatformArea platform, Transform visualRoot)
        {
            platformArea = platform;
            shipVisualRoot = visualRoot;
        }

        private void Awake()
        {
            if (platformArea == null) platformArea = GetComponent<ShipPlatformArea>();
            if (shipVisualRoot == null)
            {
                var balance = GetComponent<ShipBalanceController>();
                if (balance != null) shipVisualRoot = balance.shipVisualRoot;
            }
            currentCondition = baseCondition;
        }

        private void Start()
        {
            BuildZeroFrictionMaterial();
            AssignDeckMaterials();
            RefreshCondition();
        }

        private void Update()
        {
            RefreshCondition();
        }

        private void FixedUpdate()
        {
            var nm = NetworkManagerP2P.Instance;
            if (nm != null && !nm.IsWorldAuthority) return;
            if (platformArea == null || shipVisualRoot == null) return;

            if (zeroFrictionMaterial == null)
            {
                BuildZeroFrictionMaterial();
                AssignDeckMaterials();
            }

            aboardNow.Clear();
            var items = platformArea.itemsInPlatform;
            for (int i = 0; i < items.Count; i++)
            {
                CargoItem item = items[i];
                if (item == null || item.isHeld) continue;
                Rigidbody body = item.Body;
                Collider cargoCollider = item.GetComponent<Collider>();
                if (body == null || body.isKinematic || cargoCollider == null) continue;

                aboardNow.Add(item);
                if (!originalCargoMaterials.ContainsKey(item))
                    originalCargoMaterials[item] = cargoCollider.sharedMaterial;
                if (cargoCollider.sharedMaterial != zeroFrictionMaterial)
                    cargoCollider.sharedMaterial = zeroFrictionMaterial;

                ApplyTraction(item, body);
            }

            restoreBuffer.Clear();
            foreach (var pair in originalCargoMaterials)
            {
                if (pair.Key == null || !aboardNow.Contains(pair.Key))
                    restoreBuffer.Add(pair.Key);
            }
            for (int i = 0; i < restoreBuffer.Count; i++)
            {
                CargoItem item = restoreBuffer[i];
                if (item != null)
                {
                    Collider c = item.GetComponent<Collider>();
                    if (c != null) c.sharedMaterial = originalCargoMaterials[item];
                }
                originalCargoMaterials.Remove(item);
            }
        }

        private void ApplyTraction(CargoItem item, Rigidbody body)
        {
            Vector3 normal = shipVisualRoot.up;
            Vector3 relativeVelocity = body.linearVelocity - platformArea.CurrentShipVelocity;
            Vector3 tangentVelocity = Vector3.ProjectOnPlane(relativeVelocity, normal);
            if (tangentVelocity.sqrMagnitude < 0.000001f) return;

            float normalAcceleration = Mathf.Abs(Vector3.Dot(Physics.gravity, normal));
            float grip = GetGripCoefficient(item);
            float maxVelocityChange = grip * normalAcceleration * Time.fixedDeltaTime;
            float speed = tangentVelocity.magnitude;
            Vector3 frictionDelta = -tangentVelocity / speed * Mathf.Min(speed, maxVelocityChange);
            body.AddForce(frictionDelta, ForceMode.VelocityChange);
        }

        public float GetGripCoefficient(CargoItem item)
        {
            float cargoGrip = item != null ? item.deckGripMultiplier : 1f;
            return Mathf.Max(0f, GripFor(currentCondition) * cargoGrip);
        }

        /// <summary>
        /// Portion of the normalized downhill weight that survives deck drag and reaches a rail.
        /// The cargo's own grip participates, so a smooth crystal and a rough stone do not load
        /// the same railing equally in identical weather.
        /// </summary>
        public float GetBarrierLoadTransmission(CargoItem item)
        {
            float resistance = GetGripCoefficient(item) * pressureGripScale;
            return Mathf.Clamp(1f - resistance, minimumLoadTransmission, 1f);
        }

        public void SetBaseCondition(SurfaceCondition condition)
        {
            baseCondition = condition;
            RefreshCondition();
        }

        /// <summary>Client-side mirror of the host's selected condition.</summary>
        public void ApplyRemoteCondition(int condition)
        {
            currentCondition = (SurfaceCondition)Mathf.Clamp(condition, 0, 3);
            currentGrip = GripFor(currentCondition);
        }

        private void RefreshCondition()
        {
            var nm = NetworkManagerP2P.Instance;
            if (nm != null && nm.isConnected && !nm.IsWorldAuthority)
                return; // clients retain the host's ApplyRemoteCondition value

            SurfaceCondition next = baseCondition;
            if (rainFromStorm && baseCondition == SurfaceCondition.Dry)
            {
                if (storm == null) storm = FindAnyObjectByType<StormSystem>();
                if (storm != null && storm.shipDepth >= stormWetThreshold)
                    next = SurfaceCondition.Rain;
            }
            currentCondition = next;
            currentGrip = GripFor(currentCondition);
        }

        private float GripFor(SurfaceCondition condition)
        {
            switch (condition)
            {
                case SurfaceCondition.Rain: return rainGrip;
                case SurfaceCondition.Ice: return iceGrip;
                case SurfaceCondition.Sand: return sandGrip;
                default: return dryGrip;
            }
        }

        private void BuildZeroFrictionMaterial()
        {
            if (zeroFrictionMaterial != null) return;
            zeroFrictionMaterial = new PhysicsMaterial("Skyship_CustomDeckFriction")
            {
                dynamicFriction = 0f,
                staticFriction = 0f,
                bounciness = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
        }

        private void AssignDeckMaterials()
        {
            if (shipVisualRoot == null || zeroFrictionMaterial == null) return;
            Collider[] colliders = shipVisualRoot.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider c = colliders[i];
                string n = c.gameObject.name;
                if (n.StartsWith("UpperDeck") || n == "LowerDeck_Floor" ||
                    n.StartsWith("Stairs") || n == "Plank")
                    c.sharedMaterial = zeroFrictionMaterial;
            }
        }

        private void OnDestroy()
        {
            foreach (var pair in originalCargoMaterials)
            {
                if (pair.Key == null) continue;
                Collider c = pair.Key.GetComponent<Collider>();
                if (c != null) c.sharedMaterial = pair.Value;
            }
            originalCargoMaterials.Clear();
            if (zeroFrictionMaterial != null) Destroy(zeroFrictionMaterial);
        }
    }
}

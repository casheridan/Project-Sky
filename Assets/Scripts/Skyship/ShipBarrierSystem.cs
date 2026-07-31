using System;
using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    public enum ShipBarrierSide
    {
        Port,
        Starboard,
        Bow,
        Stern
    }

    /// <summary>
    /// One independently breakable railing module. Its solid collider protects players/cargo
    /// while intact; a larger trigger remains after failure so the broken rail can still be
    /// targeted for repair.
    /// </summary>
    public class ShipBarrierSegment : MonoBehaviour
    {
        [Header("Identity")]
        public string barrierId;
        public ShipBarrierSide side;

        [Header("Runtime (read-only)")]
        [Range(0f, 1f)] public float integrity = 1f;
        public bool broken;
        public float currentAppliedLoad;
        public float lastImpactDamage;

        private ShipBarrierSystem system;
        private BoxCollider solidCollider;
        private BoxCollider interactionCollider;
        private Transform horizontalVisual;
        private Transform leftPost;
        private Transform rightPost;
        private readonly List<Renderer> renderers = new List<Renderer>();
        private readonly HashSet<CargoItem> directContacts = new HashSet<CargoItem>();
        private readonly HashSet<CargoItem> proximityContacts = new HashSet<CargoItem>();
        private readonly List<CargoItem> contactPruneBuffer = new List<CargoItem>();
        private float intactPostHeight;
        private float pendingImpactDamage;

        internal HashSet<CargoItem> DirectContacts => directContacts;
        internal HashSet<CargoItem> ProximityContacts => proximityContacts;
        internal Collider InteractionCollider => interactionCollider;
        public bool NeedsRepair => broken || integrity < 0.999f;
        public string DisplayName => side + " railing";

        internal void Initialize(ShipBarrierSystem owner, string id, ShipBarrierSide barrierSide, float length)
        {
            system = owner;
            barrierId = id;
            side = barrierSide;
            Build(length);
            ApplyVisualState();
        }

        private void Build(float length)
        {
            float height = system.railHeight;
            intactPostHeight = height;

            solidCollider = gameObject.AddComponent<BoxCollider>();
            solidCollider.center = new Vector3(0f, height * 0.5f, 0f);
            solidCollider.size = new Vector3(length, height, system.colliderDepth);

            interactionCollider = gameObject.AddComponent<BoxCollider>();
            interactionCollider.isTrigger = true;
            interactionCollider.center = new Vector3(0f, height * 0.55f, 0f);
            interactionCollider.size = new Vector3(length + 0.15f, height * 1.35f, 0.5f);

            leftPost = CreateCube("Post_Left",
                new Vector3(-length * 0.5f + system.postWidth * 0.5f, height * 0.5f, 0f),
                new Vector3(system.postWidth, height, system.postDepth)).transform;
            rightPost = CreateCube("Post_Right",
                new Vector3(length * 0.5f - system.postWidth * 0.5f, height * 0.5f, 0f),
                new Vector3(system.postWidth, height, system.postDepth)).transform;

            horizontalVisual = new GameObject("HorizontalRails").transform;
            horizontalVisual.SetParent(transform, false);
            CreateCube("Rail_Lower",
                new Vector3(0f, height * 0.38f, 0f),
                new Vector3(length, system.railThickness, system.railDepth),
                horizontalVisual);
            CreateCube("Rail_Upper",
                new Vector3(0f, height * 0.82f, 0f),
                new Vector3(length, system.railThickness, system.railDepth),
                horizontalVisual);
        }

        private GameObject CreateCube(string pieceName, Vector3 localPosition, Vector3 localScale,
                                      Transform parent = null)
        {
            GameObject piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.name = pieceName;
            piece.transform.SetParent(parent != null ? parent : transform, false);
            piece.transform.localPosition = localPosition;
            piece.transform.localScale = localScale;
            Collider primitiveCollider = piece.GetComponent<Collider>();
            if (primitiveCollider != null) Destroy(primitiveCollider);
            Renderer r = piece.GetComponent<Renderer>();
            if (r != null)
            {
                r.sharedMaterial = system.intactMaterial;
                renderers.Add(r);
            }
            return piece;
        }

        private void OnCollisionEnter(Collision collision)
        {
            RegisterContact(collision, true);
            RecordImpact(collision);
        }

        private void OnCollisionStay(Collision collision)
        {
            RegisterContact(collision, true);
            RecordImpact(collision);
        }

        private void OnCollisionExit(Collision collision)
        {
            RegisterContact(collision, false);
        }

        private void RegisterContact(Collision collision, bool touching)
        {
            if (broken || collision == null || collision.collider == null) return;
            CargoItem item = collision.collider.GetComponentInParent<CargoItem>();
            if (item == null) return;
            if (touching) directContacts.Add(item);
            else directContacts.Remove(item);
        }

        private void RecordImpact(Collision collision)
        {
            if (broken || system == null || !system.IsAuthority ||
                collision == null || collision.collider == null) return;
            CargoItem item = collision.collider.GetComponentInParent<CargoItem>();
            if (item == null || item.isHeld) return;

            float damage = system.CalculateImpactDamage(this, item, collision);
            if (damage > 0f)
                pendingImpactDamage = Mathf.Clamp01(pendingImpactDamage + damage);
        }

        internal float ConsumeImpactDamage()
        {
            float damage = pendingImpactDamage;
            pendingImpactDamage = 0f;
            if (damage > 0f) lastImpactDamage = damage;
            return damage;
        }

        internal void PruneContacts(HashSet<CargoItem> activeCargo)
        {
            contactPruneBuffer.Clear();
            foreach (CargoItem item in directContacts)
            {
                if (item == null || item.isHeld || !activeCargo.Contains(item))
                    contactPruneBuffer.Add(item);
            }
            for (int i = 0; i < contactPruneBuffer.Count; i++)
                directContacts.Remove(contactPruneBuffer[i]);
        }

        internal void RefreshProximityContacts(HashSet<CargoItem> activeCargo, float contactSkin)
        {
            proximityContacts.Clear();
            if (broken || solidCollider == null) return;

            Bounds railBounds = solidCollider.bounds;
            railBounds.Expand(contactSkin * 2f);
            foreach (CargoItem item in activeCargo)
            {
                if (item == null || item.isHeld || item.CollisionShape == null) continue;
                if (railBounds.Intersects(item.CollisionShape.bounds))
                    proximityContacts.Add(item);
            }
        }

        internal void ApplyDamage(float normalizedDamage, string reason)
        {
            if (broken || normalizedDamage <= 0f) return;
            integrity = Mathf.Clamp01(integrity - normalizedDamage);
            if (integrity <= 0.0001f)
            {
                integrity = 0f;
                broken = true;
                directContacts.Clear();
                proximityContacts.Clear();
                pendingImpactDamage = 0f;
                system.NotifyBroken(this, reason);
            }
            ApplyVisualState();
        }

        internal void RepairFull()
        {
            integrity = 1f;
            broken = false;
            currentAppliedLoad = 0f;
            lastImpactDamage = 0f;
            pendingImpactDamage = 0f;
            directContacts.Clear();
            proximityContacts.Clear();
            ApplyVisualState();
        }

        public void ApplyRemoteState(float remoteIntegrity, bool remoteBroken)
        {
            integrity = Mathf.Clamp01(remoteIntegrity);
            broken = remoteBroken || integrity <= 0.0001f;
            if (broken)
            {
                directContacts.Clear();
                proximityContacts.Clear();
            }
            ApplyVisualState();
        }

        private void ApplyVisualState()
        {
            if (solidCollider != null) solidCollider.enabled = !broken;
            if (horizontalVisual != null) horizontalVisual.gameObject.SetActive(!broken);

            float postHeight = broken ? system.brokenPostHeight : intactPostHeight;
            SetPostHeight(leftPost, postHeight);
            SetPostHeight(rightPost, postHeight);

            Material stateMaterial = broken
                ? system.brokenMaterial
                : (integrity < system.damagedVisualThreshold ? system.damagedMaterial : system.intactMaterial);
            for (int i = 0; i < renderers.Count; i++)
            {
                if (renderers[i] != null) renderers[i].sharedMaterial = stateMaterial;
            }
        }

        private static void SetPostHeight(Transform post, float height)
        {
            if (post == null) return;
            Vector3 scale = post.localScale;
            scale.y = height;
            post.localScale = scale;
            Vector3 pos = post.localPosition;
            pos.y = height * 0.5f;
            post.localPosition = pos;
        }

        private void OnDisable()
        {
            directContacts.Clear();
            proximityContacts.Clear();
            pendingImpactDamage = 0f;
        }
    }

    /// <summary>
    /// Builds and simulates the modular upper-deck railings.
    ///
    /// LOAD MODEL:
    ///  - ShipBalanceController's normalized roll/pitch gives the fraction of cargo weight acting
    ///    downhill (20% lean means 20% weight before grip, matching the design target).
    ///  - Dry/rain/ice/sand plus per-cargo grip reduce how much load reaches the rail.
    ///  - Cargo touching cargo forms a pressure graph. Every connected item's weight reaches the
    ///    contacted segment with a small per-link attenuation, including items not touching it.
    ///  - Sustained pressure permanently consumes integrity. At ratedLoad, ratedHoldSeconds is the
    ///    exact life. No passive recovery occurs; only repair restores the lost time/integrity.
    ///  - Collision impulse and closing velocity add immediate impact damage, including impulses
    ///    Unity transmits through a chain of rigidbodies.
    ///
    /// The host/solo instance is authoritative. Stable segment IDs and integrity values are
    /// mirrored in NetworkManagerP2P State packets so missed updates self-heal.
    /// </summary>
    [DefaultExecutionOrder(-40)]
    public class ShipBarrierSystem : MonoBehaviour
    {
        [Header("References")]
        public ShipBalanceController balance;
        public ShipPlatformArea platformArea;
        public ShipDeckSurfaceController deckSurface;
        public Transform shipVisualRoot;

        [Header("Sustained load")]
        [Tooltip("Applied load that consumes one full integrity bar in ratedHoldSeconds.")]
        public float ratedLoad = 50f;
        [Tooltip("At ratedLoad, an undamaged stock railing lasts exactly this many seconds.")]
        public float ratedHoldSeconds = 300f;
        [Tooltip("Applied load at or above this value breaks the segment immediately.")]
        public float instantBreakLoad = 140f;
        [Tooltip("1 = linear life (50 load for 300 s; 25 load for 600 s). Higher values forgive light loads.")]
        [Range(0.5f, 3f)] public float fatigueExponent = 1f;
        [Tooltip("Weight transmission retained for every cargo-to-cargo link in a push chain.")]
        [Range(0.5f, 1f)] public float chainTransmission = 0.9f;
        [Tooltip("Small bounds expansion that treats resting/near-touching physics bodies as a stable contact.")]
        [Range(0.01f, 0.2f)] public float contactSkin = 0.08f;

        [Header("Impact damage")]
        [Tooltip("Closing speed below this is treated as pressure, not an impact.")]
        public float impactSpeedThreshold = 1.2f;
        [Tooltip("Abstract impulse below this does not damage the railing.")]
        public float impactMomentumThreshold = 25f;
        [Tooltip("Additional momentum required to consume one full integrity bar.")]
        public float impactMomentumToBreak = 450f;
        [Tooltip("Kinetic energy required to consume one full integrity bar.")]
        public float impactEnergyToBreak = 900f;

        [Header("Repair and upgrades")]
        public float repairDistance = 4f;
        [Tooltip("Stock/upgrade multiplier for sustained-load capacity.")]
        [Min(0.1f)] public float loadCapacityMultiplier = 1f;
        [Tooltip("Stock/upgrade multiplier for total durability/time-to-failure.")]
        [Min(0.1f)] public float durabilityMultiplier = 1f;
        [Tooltip("Stock/upgrade multiplier for impact resistance.")]
        [Min(0.1f)] public float impactResistanceMultiplier = 1f;
        [Tooltip("Reserved for repair-resource economy: how effective each repair action is.")]
        [Min(0.1f)] public float repairEfficiencyMultiplier = 1f;

        [Header("Generated geometry")]
        public float targetSegmentLength = 2.35f;
        public float railHeight = 1.25f;
        public float colliderDepth = 0.18f;
        public float railThickness = 0.12f;
        public float railDepth = 0.12f;
        public float postWidth = 0.16f;
        public float postDepth = 0.16f;
        public float brokenPostHeight = 0.32f;
        public float rampOpeningWidth = 3.3f;
        public float cornerMargin = 0.12f;
        [Range(0f, 1f)] public float damagedVisualThreshold = 0.45f;

        [Header("Runtime (read-only)")]
        [SerializeField] private List<ShipBarrierSegment> segments = new List<ShipBarrierSegment>();

        [NonSerialized] public Material intactMaterial;
        [NonSerialized] public Material damagedMaterial;
        [NonSerialized] public Material brokenMaterial;

        private readonly Dictionary<string, ShipBarrierSegment> byId =
            new Dictionary<string, ShipBarrierSegment>();
        private readonly HashSet<CargoItem> activeCargo = new HashSet<CargoItem>();
        private readonly Dictionary<CargoItem, int> graphDepth = new Dictionary<CargoItem, int>();
        private readonly Queue<CargoItem> graphQueue = new Queue<CargoItem>();

        public IReadOnlyList<ShipBarrierSegment> Segments => segments;
        public ShipDeckSurfaceController DeckSurface => deckSurface;
        public bool IsAuthority
        {
            get
            {
                var nm = NetworkManagerP2P.Instance;
                return nm == null || nm.IsWorldAuthority;
            }
        }

        public static ShipBarrierSystem CreateOnShip(ShipBalanceController balanceController)
        {
            if (balanceController == null || balanceController.shipVisualRoot == null) return null;
            Transform visual = balanceController.shipVisualRoot;
            ShipBarrierSystem existing = visual.GetComponentInChildren<ShipBarrierSystem>(true);
            if (existing != null) return existing;

            GameObject go = new GameObject("ShipBarriers");
            go.transform.SetParent(visual, false);
            ShipBarrierSystem system = go.AddComponent<ShipBarrierSystem>();
            system.Initialize(balanceController);
            return system;
        }

        private void Initialize(ShipBalanceController balanceController)
        {
            balance = balanceController;
            shipVisualRoot = balanceController.shipVisualRoot;
            platformArea = balanceController.platformArea != null
                ? balanceController.platformArea
                : balanceController.GetComponent<ShipPlatformArea>();

            deckSurface = balanceController.GetComponent<ShipDeckSurfaceController>();
            if (deckSurface == null)
                deckSurface = balanceController.gameObject.AddComponent<ShipDeckSurfaceController>();
            deckSurface.Configure(platformArea, shipVisualRoot);

            BuildMaterials();
            BuildRailings();
        }

        private void FixedUpdate()
        {
            if (!IsAuthority || balance == null || platformArea == null || shipVisualRoot == null)
                return;

            activeCargo.Clear();
            var cargo = platformArea.itemsInPlatform;
            for (int i = 0; i < cargo.Count; i++)
            {
                CargoItem item = cargo[i];
                if (item != null && !item.isHeld) activeCargo.Add(item);
            }

            for (int i = 0; i < segments.Count; i++)
            {
                ShipBarrierSegment segment = segments[i];
                if (segment == null || segment.broken) continue;
                segment.PruneContacts(activeCargo);
                segment.RefreshProximityContacts(activeCargo, contactSkin);

                float impactDamage = segment.ConsumeImpactDamage();
                if (impactDamage > 0f)
                    segment.ApplyDamage(impactDamage / Mathf.Max(0.1f, impactResistanceMultiplier), "impact");
                if (segment.broken) continue;

                float load = CalculateSustainedLoad(segment);
                segment.currentAppliedLoad = load;

                float upgradedInstantBreak = instantBreakLoad * loadCapacityMultiplier;
                if (load >= upgradedInstantBreak)
                {
                    segment.ApplyDamage(1f, "overload");
                    continue;
                }

                if (load <= 0.001f) continue;
                float upgradedRatedLoad = Mathf.Max(0.01f, ratedLoad * loadCapacityMultiplier);
                float upgradedHoldTime = Mathf.Max(0.1f, ratedHoldSeconds * durabilityMultiplier);
                float normalizedRate = Mathf.Pow(load / upgradedRatedLoad, fatigueExponent);
                segment.ApplyDamage(normalizedRate * Time.fixedDeltaTime / upgradedHoldTime, "sustained load");
            }
        }

        private float CalculateSustainedLoad(ShipBarrierSegment segment)
        {
            if (segment.DirectContacts.Count == 0 && segment.ProximityContacts.Count == 0) return 0f;

            Vector3 downhill = new Vector3(balance.rollImbalance, 0f, balance.pitchImbalance);
            Vector3 outward = shipVisualRoot.InverseTransformDirection(segment.transform.forward);
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.001f) return 0f;
            outward.Normalize();

            float downhillFraction = Mathf.Max(0f, Vector3.Dot(downhill, outward));
            if (downhillFraction <= 0.0001f) return 0f;

            graphDepth.Clear();
            graphQueue.Clear();
            foreach (CargoItem direct in segment.DirectContacts)
            {
                if (direct == null || !activeCargo.Contains(direct) || graphDepth.ContainsKey(direct))
                    continue;
                graphDepth[direct] = 0;
                graphQueue.Enqueue(direct);
            }
            foreach (CargoItem nearby in segment.ProximityContacts)
            {
                if (nearby == null || !activeCargo.Contains(nearby) || graphDepth.ContainsKey(nearby))
                    continue;
                graphDepth[nearby] = 0;
                graphQueue.Enqueue(nearby);
            }

            float load = 0f;
            while (graphQueue.Count > 0)
            {
                CargoItem item = graphQueue.Dequeue();
                int depth = graphDepth[item];
                float gripTransmission = deckSurface != null
                    ? deckSurface.GetBarrierLoadTransmission(item)
                    : 1f;
                float chain = Mathf.Pow(chainTransmission, depth);
                load += Mathf.Max(0f, item.weight) * downhillFraction * gripTransmission * chain;

                foreach (CargoItem neighbor in activeCargo)
                {
                    if (neighbor == null || neighbor == item || neighbor.isHeld ||
                        graphDepth.ContainsKey(neighbor) || !CargoAreTouching(item, neighbor))
                        continue;
                    graphDepth[neighbor] = depth + 1;
                    graphQueue.Enqueue(neighbor);
                }
            }
            return load;
        }

        private bool CargoAreTouching(CargoItem a, CargoItem b)
        {
            if (a.TouchingCargo.Contains(b) || b.TouchingCargo.Contains(a)) return true;
            if (a.CollisionShape == null || b.CollisionShape == null) return false;
            Bounds bounds = a.CollisionShape.bounds;
            bounds.Expand(contactSkin * 2f);
            return bounds.Intersects(b.CollisionShape.bounds);
        }

        internal float CalculateImpactDamage(ShipBarrierSegment segment, CargoItem item, Collision collision)
        {
            Rigidbody body = item.Body;
            float outwardSpeed = 0f;
            if (body != null)
            {
                Vector3 shipVelocity = platformArea != null ? platformArea.CurrentShipVelocity : Vector3.zero;
                outwardSpeed = Mathf.Max(0f, Vector3.Dot(body.linearVelocity - shipVelocity,
                                                        segment.transform.forward));
            }

            float collisionSpeed = Mathf.Max(outwardSpeed, collision.relativeVelocity.magnitude);
            if (collisionSpeed <= impactSpeedThreshold)
                return 0f; // resting/depenetration impulses are sustained load, never an "impact"

            float excessSpeedSquared = Mathf.Max(0f,
                collisionSpeed * collisionSpeed - impactSpeedThreshold * impactSpeedThreshold);
            float energyDamage = impactEnergyToBreak > 0.01f
                ? 0.5f * Mathf.Max(0f, item.weight) * excessSpeedSquared / impactEnergyToBreak
                : 0f;

            float massScale = Mathf.Max(0.005f, item.physicsMassPerWeight);
            float abstractImpulse = collision.impulse.magnitude / massScale;
            float momentumDamage = impactMomentumToBreak > 0.01f
                ? Mathf.Max(0f, abstractImpulse - impactMomentumThreshold) / impactMomentumToBreak
                : 0f;

            return Mathf.Clamp01(Mathf.Max(energyDamage, momentumDamage) *
                                 Mathf.Max(0.1f, item.barrierImpactMultiplier));
        }

        public ShipBarrierSegment FindSegment(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            byId.TryGetValue(id, out ShipBarrierSegment segment);
            return segment;
        }

        public bool TryRepair(string id, Vector3 playerPosition)
        {
            if (!IsAuthority) return false;
            ShipBarrierSegment segment = FindSegment(id);
            if (segment == null || !segment.NeedsRepair) return false;

            Vector3 nearest = segment.InteractionCollider != null
                ? segment.InteractionCollider.ClosestPoint(playerPosition)
                : segment.transform.position;
            if (Vector3.Distance(playerPosition, nearest) > repairDistance) return false;

            segment.RepairFull();
            NotifyRepaired(segment);
            return true;
        }

        public void ApplyRemoteState(string id, float integrity, bool broken)
        {
            ShipBarrierSegment segment = FindSegment(id);
            if (segment != null) segment.ApplyRemoteState(integrity, broken);
        }

        public void ApplyUpgradeMultipliers(float loadCapacity, float durability,
                                            float impactResistance, float repairEfficiency)
        {
            loadCapacityMultiplier = Mathf.Max(0.1f, loadCapacity);
            durabilityMultiplier = Mathf.Max(0.1f, durability);
            impactResistanceMultiplier = Mathf.Max(0.1f, impactResistance);
            repairEfficiencyMultiplier = Mathf.Max(0.1f, repairEfficiency);
        }

        internal void NotifyBroken(ShipBarrierSegment segment, string reason)
        {
            string message = $"{segment.DisplayName} broke from {reason}.";
            Debug.LogWarning($"[ShipBarrierSystem] {segment.barrierId}: {message}");
            NetworkManagerP2P.Instance?.ShowBanner(message, 4f);
        }

        private void NotifyRepaired(ShipBarrierSegment segment)
        {
            string message = $"{segment.DisplayName} repaired.";
            Debug.Log($"[ShipBarrierSystem] {segment.barrierId}: {message}");
            NetworkManagerP2P.Instance?.ShowBanner(message, 3f);
        }

        private void BuildRailings()
        {
            segments.Clear();
            byId.Clear();

            float port = float.PositiveInfinity;
            float starboard = float.NegativeInfinity;
            float bow = float.NegativeInfinity;
            float stern = float.PositiveInfinity;
            float deckTop = float.NegativeInfinity;
            float frontWingMin = float.PositiveInfinity, frontWingMax = float.NegativeInfinity;
            float rearWingMin = float.PositiveInfinity, rearWingMax = float.NegativeInfinity;
            bool foundDeck = false;

            foreach (Transform child in shipVisualRoot)
            {
                if (!child.name.StartsWith("UpperDeck")) continue;
                foundDeck = true;
                float halfX = Mathf.Abs(child.localScale.x) * 0.5f;
                float halfZ = Mathf.Abs(child.localScale.z) * 0.5f;
                port = Mathf.Min(port, child.localPosition.x - halfX);
                starboard = Mathf.Max(starboard, child.localPosition.x + halfX);
                stern = Mathf.Min(stern, child.localPosition.z - halfZ);
                bow = Mathf.Max(bow, child.localPosition.z + halfZ);
                deckTop = Mathf.Max(deckTop, child.localPosition.y + Mathf.Abs(child.localScale.y) * 0.5f);

                if (child.name.Contains("RightWing_Front"))
                {
                    frontWingMin = child.localPosition.z - halfZ;
                    frontWingMax = child.localPosition.z + halfZ;
                }
                else if (child.name.Contains("RightWing_Rear"))
                {
                    rearWingMin = child.localPosition.z - halfZ;
                    rearWingMax = child.localPosition.z + halfZ;
                }
            }

            if (!foundDeck)
            {
                port = -4f; starboard = 4f; stern = -10f; bow = 10f; deckTop = 0f;
            }

            float halfOpening = rampOpeningWidth * 0.5f;
            AddAlongZ("Rail_Port_A", ShipBarrierSide.Port, port, stern + cornerMargin,
                      -halfOpening, deckTop, -90f);
            AddAlongZ("Rail_Port_B", ShipBarrierSide.Port, port, halfOpening,
                      bow - cornerMargin, deckTop, -90f);

            if (rearWingMax > rearWingMin)
                AddAlongZ("Rail_Starboard_Rear", ShipBarrierSide.Starboard, starboard,
                          rearWingMin + cornerMargin, rearWingMax, deckTop, 90f);
            if (frontWingMax > frontWingMin)
                AddAlongZ("Rail_Starboard_Front", ShipBarrierSide.Starboard, starboard,
                          frontWingMin, frontWingMax - cornerMargin, deckTop, 90f);
            if (rearWingMax <= rearWingMin && frontWingMax <= frontWingMin)
                AddAlongZ("Rail_Starboard", ShipBarrierSide.Starboard, starboard,
                          stern + cornerMargin, bow - cornerMargin, deckTop, 90f);

            AddAlongX("Rail_Bow", ShipBarrierSide.Bow, bow, port + cornerMargin,
                      starboard - cornerMargin, deckTop, 0f);
            AddAlongX("Rail_Stern", ShipBarrierSide.Stern, stern, port + cornerMargin,
                      starboard - cornerMargin, deckTop, 180f);
        }

        private void AddAlongX(string prefix, ShipBarrierSide side, float z, float minX,
                               float maxX, float y, float yaw)
        {
            float total = maxX - minX;
            if (total <= 0.1f) return;
            int count = Mathf.Max(1, Mathf.CeilToInt(total / Mathf.Max(0.5f, targetSegmentLength)));
            float length = total / count;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = new Vector3(minX + length * (i + 0.5f), y, z);
                CreateSegment($"{prefix}_{i:00}", side, pos, yaw, length);
            }
        }

        private void AddAlongZ(string prefix, ShipBarrierSide side, float x, float minZ,
                               float maxZ, float y, float yaw)
        {
            float total = maxZ - minZ;
            if (total <= 0.1f) return;
            int count = Mathf.Max(1, Mathf.CeilToInt(total / Mathf.Max(0.5f, targetSegmentLength)));
            float length = total / count;
            for (int i = 0; i < count; i++)
            {
                Vector3 pos = new Vector3(x, y, minZ + length * (i + 0.5f));
                CreateSegment($"{prefix}_{i:00}", side, pos, yaw, length);
            }
        }

        private void CreateSegment(string id, ShipBarrierSide side, Vector3 localPosition,
                                   float localYaw, float length)
        {
            GameObject go = new GameObject(id);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = localPosition;
            go.transform.localRotation = Quaternion.Euler(0f, localYaw, 0f);
            ShipBarrierSegment segment = go.AddComponent<ShipBarrierSegment>();
            segment.Initialize(this, id, side, length);
            segments.Add(segment);
            byId[id] = segment;
        }

        private void BuildMaterials()
        {
            intactMaterial = MakeMaterial(new Color(0.30f, 0.24f, 0.17f));
            damagedMaterial = MakeMaterial(new Color(0.65f, 0.34f, 0.12f));
            brokenMaterial = MakeMaterial(new Color(0.22f, 0.13f, 0.09f));
        }

        private static Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            Material material = new Material(shader) { color = color };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        private void OnDestroy()
        {
            if (intactMaterial != null) Destroy(intactMaterial);
            if (damagedMaterial != null) Destroy(damagedMaterial);
            if (brokenMaterial != null) Destroy(brokenMaterial);
        }
    }
}

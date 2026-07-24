using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>Which raw resource a node yields. Each maps to its own CargoCategory.</summary>
    public enum ResourceNodeType
    {
        Stone,
        Ore,
        Crystal
    }

    /// <summary>
    /// A harvestable resource rock on an island. Press E on it (PlayerInteraction) to break off one
    /// crate of its resource, which drops at a clear spot in front of the player. Every node has a
    /// finite total yield (rolled deterministically from the world seed at generation time); its
    /// type-colored veins shrink as it depletes and disappear when it's spent.
    ///
    /// NETWORKING: nodes are static, deterministically-named world geometry (WNode_0000), so they
    /// need no spawn sync. Only 'remainingYield' is dynamic — the host is authoritative over it
    /// (TryConsumeOne runs on the authority; clients receive updates via ApplyRemoteYield).
    /// Harvested crates are named nodeId + "_c" + harvestIndex, so every peer derives the same
    /// crate name for the same harvest.
    /// </summary>
    public class ResourceNode : MonoBehaviour
    {
        [Header("Identity (set by WorldGenerator)")]
        public string nodeId;
        public ResourceNodeType nodeType;

        [Header("Yield (read-only at runtime)")]
        public int maxYield;
        public int remainingYield;

        private readonly List<Transform> veins = new List<Transform>();
        private readonly List<Vector3> veinBaseScales = new List<Vector3>();

        public bool IsDepleted => remainingYield <= 0;

        public CargoCategory CargoCategory
        {
            get
            {
                switch (nodeType)
                {
                    case ResourceNodeType.Ore: return CargoCategory.Ore;
                    case ResourceNodeType.Crystal: return CargoCategory.Crystal;
                    default: return CargoCategory.Stone;
                }
            }
        }

        /// <summary>Deterministic crate name for the NEXT harvest off this node (same on all peers).</summary>
        public string NextCargoName => nodeId + "_c" + (maxYield - remainingYield);

        /// <summary>Called by WorldGenerator right after building the node's rock cluster.</summary>
        public void Initialize(string id, ResourceNodeType type, int yield, List<Transform> veinTransforms)
        {
            nodeId = id;
            nodeType = type;
            maxYield = yield;
            remainingYield = yield;
            veins.Clear();
            veinBaseScales.Clear();
            if (veinTransforms != null)
            {
                foreach (var v in veinTransforms)
                {
                    veins.Add(v);
                    veinBaseScales.Add(v.localScale);
                }
            }
        }

        /// <summary>Authority path: consume one yield. Returns false if the node is already spent.</summary>
        public bool TryConsumeOne()
        {
            if (remainingYield <= 0) return false;
            remainingYield--;
            UpdateVisuals();
            return true;
        }

        /// <summary>Client path: reconcile to the authority's remaining count (harvest packets / state sync).</summary>
        public void ApplyRemoteYield(int remaining)
        {
            remaining = Mathf.Clamp(remaining, 0, maxYield);
            if (remaining == remainingYield) return;
            remainingYield = remaining;
            UpdateVisuals();
        }

        /// <summary>Veins shrink as the node depletes; a spent node loses them entirely (grey husk).</summary>
        private void UpdateVisuals()
        {
            float t = maxYield > 0 ? remainingYield / (float)maxYield : 0f;
            for (int i = 0; i < veins.Count; i++)
            {
                if (veins[i] == null) continue;
                if (IsDepleted)
                {
                    veins[i].gameObject.SetActive(false);
                }
                else
                {
                    veins[i].gameObject.SetActive(true);
                    veins[i].localScale = veinBaseScales[i] * Mathf.Lerp(0.35f, 1f, t);
                }
            }
        }

        /// <summary>
        /// Find a clear, player-visible drop point for a harvested crate: candidates fan out on an
        /// arc in front of the player (so the crate lands in view), each checked for overlap against
        /// world geometry and other cargo, plus a line-of-sight check from the player's head so the
        /// crate never materializes behind a wall or inside a rock. Falls back to just above the
        /// node if everything is blocked.
        /// </summary>
        public static Vector3 FindDropPoint(Vector3 playerPos, Vector3 playerForward, Vector3 nodePosition)
        {
            Vector3 fwd = playerForward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude < 0.001f ? Vector3.forward : fwd.normalized;
            Vector3 head = playerPos + Vector3.up * 1.6f;

            // Crate is a 0.8 cube; use a slightly padded half-extent for the overlap test.
            Vector3 halfExtents = Vector3.one * 0.55f;
            int mask = ~0; // world geometry AND cargo — anything solid blocks a candidate

            float[] angles = { 0f, -20f, 20f, -40f, 40f };
            float[] distances = { 1.7f, 2.4f };
            foreach (float dist in distances)
            {
                foreach (float ang in angles)
                {
                    Vector3 dir = Quaternion.Euler(0f, ang, 0f) * fwd;
                    Vector3 candidate = playerPos + dir * dist + Vector3.up * 1.0f;

                    if (Physics.CheckBox(candidate, halfExtents, Quaternion.identity, mask,
                                         QueryTriggerInteraction.Ignore))
                        continue;
                    // Must be visible: nothing between the player's head and the drop spot.
                    if (Physics.Linecast(head, candidate, out RaycastHit block, mask,
                                         QueryTriggerInteraction.Ignore)
                        && block.collider.GetComponentInParent<FirstPersonController>() == null)
                        continue;

                    return candidate;
                }
            }
            return nodePosition + Vector3.up * 1.6f;
        }
    }
}

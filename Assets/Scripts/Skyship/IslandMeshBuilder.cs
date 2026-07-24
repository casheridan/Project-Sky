using System.Collections.Generic;
using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Procedural mesh construction for floating islands: a gently noise-displaced walkable top
    /// welded at the rim to a craggy underside cone that tapers to a point, plus a small crystal
    /// mesh used for underside/node decoration.
    ///
    /// DETERMINISM: everything is a pure function of the caller-supplied System.Random and params.
    /// Mathf.PerlinNoise is deterministic for given coordinates; every random offset that feeds it
    /// is drawn from the caller's rng in a fixed order, so networked peers sharing a world seed
    /// build identical meshes (see WorldGenerator's determinism notes).
    /// </summary>
    public static class IslandMeshBuilder
    {
        public struct Params
        {
            public float radius;          // nominal top radius (meters)
            public int topRings;          // radial resolution of the walkable top
            public int segments;          // angular resolution (shared by top + underside)
            public int underRings;        // vertical resolution of the underside cone
            public float heightAmp;       // top bump amplitude (total swing, meters)
            public float rimDip;          // how far the outer rim sags below the mean top (meters)
            public float underDepth;      // depth of the underside tip below the rim (meters)
            public float outlineWobble;   // 0..~0.3 — how blobby (non-circular) the outline is
            public float underJag;        // 0..~0.3 — radial raggedness of the underside rings
        }

        /// <summary>
        /// Recomputes the exact top-surface height/outline the mesh used, so props (rocks, nodes,
        /// cargo) can be placed flush on the terrain without physics raycasts at generation time.
        /// </summary>
        public struct Surface
        {
            public float radius;
            internal float ox1, oz1, ox2, oz2;   // top-noise offsets
            internal float freq1, freq2;
            internal float amp;
            internal float rimDip;
            internal float outOx, outOz, wobble; // outline-noise offsets

            /// <summary>Distance from island center to the rim at the given angle (radians).</summary>
            public float OutlineRadius(float angle)
            {
                float n = Mathf.PerlinNoise(Mathf.Cos(angle) * 1.1f + outOx, Mathf.Sin(angle) * 1.1f + outOz);
                return radius * (1f + wobble * (n - 0.5f) * 2f);
            }

            /// <summary>Top-surface height at island-local (x, z). Valid inside the outline.</summary>
            public float HeightAt(float x, float z)
            {
                float n1 = Mathf.PerlinNoise(x * freq1 + ox1, z * freq1 + oz1) - 0.5f;
                float n2 = Mathf.PerlinNoise(x * freq2 + ox2, z * freq2 + oz2) - 0.5f;
                float h = amp * (0.65f * n1 + 0.35f * n2) * 2f;

                float d = Mathf.Sqrt(x * x + z * z);
                float rim = Mathf.Max(OutlineRadius(Mathf.Atan2(z, x)), 0.001f);
                float edge = Mathf.InverseLerp(0.78f, 1f, d / rim);
                return h - rimDip * edge * edge;
            }
        }

        /// <summary>
        /// Build one island body mesh. Submesh 0 = walkable top, submesh 1 = underside cone
        /// (assign two materials on the renderer). The top surface sits around localY 0 so the
        /// island's transform altitude is its walkable height, matching the old graybox islands.
        /// </summary>
        public static Mesh Build(System.Random rng, Params p, out Surface surface)
        {
            surface = new Surface
            {
                radius = p.radius,
                ox1 = NoiseOffset(rng), oz1 = NoiseOffset(rng),
                ox2 = NoiseOffset(rng), oz2 = NoiseOffset(rng),
                freq1 = 2.5f / p.radius,
                freq2 = 6f / p.radius,
                amp = p.heightAmp,
                rimDip = p.rimDip,
                outOx = NoiseOffset(rng), outOz = NoiseOffset(rng),
                wobble = p.outlineWobble
            };
            float jagOx = NoiseOffset(rng), jagOz = NoiseOffset(rng);
            var tip = new Vector3(
                Range(rng, -0.06f, 0.06f) * p.radius,
                -p.underDepth,
                Range(rng, -0.06f, 0.06f) * p.radius);

            int seg = p.segments, topRings = p.topRings, underRings = p.underRings;
            var verts = new List<Vector3>(1 + (topRings + underRings) * seg + 2);
            var topTris = new List<int>(topRings * seg * 6);
            var underTris = new List<int>(underRings * seg * 6);

            // ---- Top: center vertex + concentric rings out to the rim ----
            verts.Add(new Vector3(0f, surface.HeightAt(0f, 0f), 0f)); // index 0
            for (int i = 1; i <= topRings; i++)
            {
                float t = i / (float)topRings;
                for (int j = 0; j < seg; j++)
                {
                    float a = j * Mathf.PI * 2f / seg;
                    float r = t * surface.OutlineRadius(a);
                    float x = Mathf.Cos(a) * r, z = Mathf.Sin(a) * r;
                    verts.Add(new Vector3(x, surface.HeightAt(x, z), z));
                }
            }
            int Top(int ring, int j) => 1 + (ring - 1) * seg + ((j % seg + seg) % seg);

            for (int j = 0; j < seg; j++)
            {
                topTris.Add(0); topTris.Add(Top(1, j + 1)); topTris.Add(Top(1, j));
            }
            for (int i = 1; i < topRings; i++)
            {
                for (int j = 0; j < seg; j++)
                {
                    int a1 = Top(i, j + 1), a0 = Top(i, j);
                    int b1 = Top(i + 1, j + 1), b0 = Top(i + 1, j);
                    topTris.Add(a1); topTris.Add(b1); topTris.Add(b0);
                    topTris.Add(a1); topTris.Add(b0); topTris.Add(a0);
                }
            }

            // ---- Underside: duplicated rim ring (hard edge) shrinking down to a tip ----
            int underStart = verts.Count;
            for (int j = 0; j < seg; j++)
                verts.Add(verts[Top(topRings, j)]);
            for (int k = 1; k < underRings; k++)
            {
                float u = k / (float)underRings;
                float shrink = Mathf.Pow(1f - u, 1.35f);
                float y = -p.underDepth * Mathf.Pow(u, 1.15f);
                for (int j = 0; j < seg; j++)
                {
                    float a = j * Mathf.PI * 2f / seg;
                    float jag = 1f + p.underJag * (Mathf.PerlinNoise(
                        Mathf.Cos(a) * 1.7f + jagOx,
                        Mathf.Sin(a) * 1.7f + u * 2.3f + jagOz) - 0.5f) * 2f;
                    float r = surface.OutlineRadius(a) * shrink * jag;
                    verts.Add(new Vector3(Mathf.Cos(a) * r, y, Mathf.Sin(a) * r));
                }
            }
            int tipIdx = verts.Count;
            verts.Add(tip);

            int Under(int ring, int j) => underStart + ring * seg + ((j % seg + seg) % seg);

            for (int k = 0; k < underRings - 1; k++)
            {
                for (int j = 0; j < seg; j++)
                {
                    int a0 = Under(k, j), a1 = Under(k, j + 1);
                    int b0 = Under(k + 1, j), b1 = Under(k + 1, j + 1);
                    underTris.Add(a0); underTris.Add(b1); underTris.Add(b0);
                    underTris.Add(a0); underTris.Add(a1); underTris.Add(b1);
                }
            }
            for (int j = 0; j < seg; j++)
            {
                underTris.Add(Under(underRings - 1, j));
                underTris.Add(tipIdx);
                underTris.Add(Under(underRings - 1, j + 1));
            }

            var mesh = new Mesh
            {
                name = "IslandBody",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32
            };
            mesh.SetVertices(verts);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(topTris, 0);
            mesh.SetTriangles(underTris, 1);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>
        /// A hexagonal crystal shard tapering to a point along +Y, base at the origin (~14 verts).
        /// Callers rotate it to hang from undersides or jut from resource nodes.
        /// </summary>
        public static Mesh BuildCrystal(System.Random rng, float length, float radius)
        {
            const int n = 6;
            var verts = new List<Vector3>(2 + n * 2);
            var tris = new List<int>(n * 12);

            float shoulderY = length * Range(rng, 0.6f, 0.78f);

            verts.Add(Vector3.zero); // 0: base center
            for (int j = 0; j < n; j++) // 1..6: base ring
            {
                float a = j * Mathf.PI * 2f / n;
                float r = radius * Range(rng, 0.85f, 1.15f);
                verts.Add(new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r));
            }
            for (int j = 0; j < n; j++) // 7..12: shoulder ring
            {
                float a = j * Mathf.PI * 2f / n;
                float r = radius * 0.7f * Range(rng, 0.8f, 1.1f);
                verts.Add(new Vector3(Mathf.Cos(a) * r, shoulderY, Mathf.Sin(a) * r));
            }
            int tip = verts.Count; // 13
            verts.Add(new Vector3(Range(rng, -0.15f, 0.15f) * radius, length, Range(rng, -0.15f, 0.15f) * radius));

            int B(int j) => 1 + ((j % n + n) % n);
            int S(int j) => 1 + n + ((j % n + n) % n);

            // Base cap (faces down).
            for (int j = 0; j < n; j++) { tris.Add(0); tris.Add(B(j + 1)); tris.Add(B(j)); }
            // Sides base->shoulder (face outward).
            for (int j = 0; j < n; j++)
            {
                tris.Add(B(j)); tris.Add(S(j)); tris.Add(S(j + 1));
                tris.Add(B(j)); tris.Add(S(j + 1)); tris.Add(B(j + 1));
            }
            // Shoulder->tip fan.
            for (int j = 0; j < n; j++)
            {
                tris.Add(S(j)); tris.Add(tip); tris.Add(S(j + 1));
            }

            var mesh = new Mesh { name = "Crystal" };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static float NoiseOffset(System.Random rng) => (float)rng.NextDouble() * 64f;
        private static float Range(System.Random rng, float min, float max)
            => min + (float)rng.NextDouble() * (max - min);
    }
}

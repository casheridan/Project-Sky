using UnityEngine;

namespace Skyship
{
    /// <summary>
    /// Shared runtime sky dressing used by BOTH the procedural world and the authored hub:
    /// a tinted procedural skybox and the "cloud sea" deck of puffs far below, selling altitude.
    /// Purely visual — no colliders, no network state.
    /// </summary>
    public static class SkyDressing
    {
        /// <summary>
        /// Swap the camera's flat clear color for Unity's procedural skybox.
        /// NOTE FOR BUILDS: "Skybox/Procedural" must be in Project Settings → Graphics →
        /// Always Included Shaders (it's assigned at runtime, so Unity won't auto-include it).
        /// </summary>
        public static Material ApplyProceduralSkybox(Color skyTint, Color groundColor,
                                                     float exposure, float atmosphereThickness)
        {
            Shader shader = Shader.Find("Skybox/Procedural");
            if (shader == null)
            {
                Debug.LogWarning("[SkyDressing] Skybox/Procedural shader not found (missing from build?). Keeping camera clear color.");
                return null;
            }

            var sky = new Material(shader);
            sky.SetFloat("_SunSize", 0.05f);
            sky.SetFloat("_SunSizeConvergence", 5f);
            sky.SetFloat("_AtmosphereThickness", atmosphereThickness);
            sky.SetFloat("_Exposure", exposure);
            sky.SetColor("_SkyTint", skyTint);
            sky.SetColor("_GroundColor", groundColor);
            RenderSettings.skybox = sky;

            var cam = Camera.main;
            if (cam != null) cam.clearFlags = CameraClearFlags.Skybox;
            return sky;
        }

        /// <summary>
        /// The cloud deck far below: a solid haze floor (so gaps never show void) with a field of
        /// big flattened puffs. Opaque, so no transparency sorting; distance fog blends the far
        /// reaches away. Pass a seeded rng for determinism, or any rng where it doesn't matter.
        /// </summary>
        public static Transform BuildCloudSea(Transform parent, Vector3 localCenter, float radius,
                                              int puffCount, Color cloudColor, Color floorColor,
                                              System.Random rng)
        {
            var sea = new GameObject("CloudSea").transform;
            if (parent != null) sea.SetParent(parent, false);
            sea.localPosition = localCenter;

            Material puffMat = MakeMat(cloudColor);
            Material floorMat = MakeMat(floorColor);

            // Haze floor: a huge thin disc under the puffs.
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            floor.name = "CloudFloor";
            Object.Destroy(floor.GetComponent<Collider>());
            floor.transform.SetParent(sea, false);
            floor.transform.localPosition = new Vector3(0f, -45f, 0f);
            floor.transform.localScale = new Vector3(radius * 2f, 2f, radius * 2f);
            floor.GetComponent<Renderer>().sharedMaterial = floorMat;

            // Puffs: wide, squashed spheres with jittered heights so they overlap organically.
            float fieldRadius = radius * 0.95f;
            for (int i = 0; i < puffCount; i++)
            {
                float ang = (float)(rng.NextDouble() * Mathf.PI * 2.0);
                float dist = fieldRadius * Mathf.Sqrt((float)rng.NextDouble());
                float w = Range(rng, 140f, 420f);

                var puff = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                puff.name = "CloudPuff";
                Object.Destroy(puff.GetComponent<Collider>());
                puff.transform.SetParent(sea, false);
                puff.transform.localPosition = new Vector3(
                    Mathf.Cos(ang) * dist, Range(rng, -28f, 22f), Mathf.Sin(ang) * dist);
                puff.transform.localScale = new Vector3(w, Range(rng, 28f, 60f), w * Range(rng, 0.7f, 1.2f));
                puff.transform.localRotation = Quaternion.Euler(0f, Range(rng, 0f, 360f), 0f);
                puff.GetComponent<Renderer>().sharedMaterial = puffMat;
            }
            return sea;
        }

        private static float Range(System.Random rng, float min, float max)
            => min + (float)rng.NextDouble() * (max - min);

        public static Material MakeMat(Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            var m = new Material(shader) { color = color };
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            return m;
        }
    }
}

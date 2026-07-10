using UnityEngine;
using UnityEngine.SceneManagement;

namespace Skyship
{
    /// <summary>
    /// Installs the vertical-slice systems at runtime so NO scene needs manual wiring (matching
    /// the project's procedural-authoring style — ShipHelm builds its levers the same way):
    ///
    ///  - Once, on first load: a persistent "ExpeditionSystems" object carrying ExpeditionManager,
    ///    GameplayHUD, and ExpeditionDebugTools (peers to the NetworkManagerP2P singleton).
    ///  - Per scene:
    ///      HubScene   → SkyChartTable on the authored menu board.
    ///      WorldScene → ShipStressSystem on ShipRoot, ExpeditionThreatDirector,
    ///                   ShipReturnStation on deck.
    /// </summary>
    public static class VerticalSliceBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Init()
        {
            if (ExpeditionManager.Instance == null)
            {
                var go = new GameObject("ExpeditionSystems");
                go.AddComponent<ExpeditionManager>();   // makes itself DontDestroyOnLoad
                go.AddComponent<GameplayHUD>();
                go.AddComponent<ExpeditionDebugTools>();
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            SceneManager.sceneLoaded += OnSceneLoaded;
            SetupScene(SceneManager.GetActiveScene());
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode) => SetupScene(scene);

        private static void SetupScene(Scene scene)
        {
            switch (scene.name)
            {
                case "HubScene":
                    SkyChartTable.CreateInHub();
                    DressHubSky();
                    break;

                case "WorldScene":
                    var ship = GameObject.Find("ShipRoot");
                    if (ship != null && ship.GetComponent<ShipStressSystem>() == null)
                        ship.AddComponent<ShipStressSystem>();

                    if (Object.FindAnyObjectByType<ExpeditionThreatDirector>() == null)
                        new GameObject("ExpeditionThreatDirector").AddComponent<ExpeditionThreatDirector>();

                    if (Object.FindAnyObjectByType<StormSystem>() == null)
                        new GameObject("StormSystem").AddComponent<StormSystem>();

                    // The eldritch threat roster (each host-authoritative, synced via the blob).
                    if (Object.FindAnyObjectByType<StaticScreamSystem>() == null)
                    {
                        var threats = new GameObject("EldritchThreats");
                        threats.AddComponent<StaticScreamSystem>();
                        threats.AddComponent<WhisperFogSystem>();
                        threats.AddComponent<LeviathanSystem>();
                        threats.AddComponent<BarnacleSystem>();
                    }

                    ShipReturnStation.CreateOnShip();
                    break;
            }
        }

        /// <summary>
        /// Give the authored hub the same sky treatment as the generated world: procedural
        /// skybox, a long draw distance + gentle fog, and a cloud sea below the dock so the
        /// port reads as perched miles up. Idempotent per scene load.
        /// </summary>
        private static void DressHubSky()
        {
            if (GameObject.Find("CloudSea") != null) return;

            SkyDressing.ApplyProceduralSkybox(
                new Color(0.42f, 0.55f, 0.75f),   // sky tint (matches the world defaults)
                new Color(0.55f, 0.58f, 0.63f),   // horizon haze
                1.25f, 0.85f);

            var cam = Camera.main;
            if (cam != null) cam.farClipPlane = Mathf.Max(cam.farClipPlane, 5000f);

            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogStartDistance = 1500f;
            RenderSettings.fogEndDistance = 4500f;
            RenderSettings.fogColor = new Color(0.65f, 0.72f, 0.82f);

            // Anchor the cloud deck below the dock (fall back to the world origin).
            var dock = GameObject.Find("Dock_Floor");
            float baseY = dock != null ? dock.transform.position.y : 0f;
            SkyDressing.BuildCloudSea(
                null,
                new Vector3(0f, baseY - 220f, 0f),
                2600f, 90,
                new Color(0.90f, 0.92f, 0.96f),
                new Color(0.74f, 0.77f, 0.81f),
                new System.Random(1337)); // fixed seed: same pretty hub for everyone
        }
    }
}

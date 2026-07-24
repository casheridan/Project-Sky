# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Project Sky ("Skyship") is a Unity prototype for a co-op game about piloting a flying salvage ship whose handling depends on how cargo is physically distributed across its deck. Players walk around the deck in first person, pick up and drop weighted cargo crates, then pilot the ship — where load and weight imbalance continuously affect speed, steering pull, and the ship's visual tilt. Up to 4 players can play together over a dependency-free UDP peer-to-peer layer.

- **Unity version:** 6000.5.0f1 (Unity 6) — see [ProjectSettings/ProjectVersion.txt](ProjectSettings/ProjectVersion.txt).
- **Render pipeline:** Universal Render Pipeline (URP) 17.5.0.
- **Input:** New Input System (1.19.0) via direct device polling (`Keyboard.current`, `Mouse.current`) — there is intentionally no `.inputactions` asset.
- All gameplay code lives in [Assets/Scripts/Skyship/](Assets/Scripts/Skyship/) under the `Skyship` namespace. There are no assembly definitions, so everything compiles into the default `Assembly-CSharp`.

## Building, Running, and Testing

This is a Unity project with no command-line build/test wiring committed. Day-to-day work happens in the Unity Editor:

- **Run/iterate:** Open the project in Unity 6000.5.0f1 and press Play. Entry scenes are under [Assets/Scenes/](Assets/Scenes/): `MainMenuScene` (host/join lobby) → `MainGameScene` (gameplay). `SampleScene` is the older prototype scene.
- **Tests:** The Unity Test Framework (1.7.0) is installed but no tests exist yet. If adding tests, create them under an `Assets/Tests/` folder with their own `.asmdef` and run them via **Window → General → Test Runner** (EditMode/PlayMode).
- The Unity MCP tools available in this environment (e.g. `Unity_GetConsoleLogs`, `Unity_RunCommand`, scene/camera capture) can drive and inspect the running editor — prefer them over guessing at runtime behavior.

## Core Architecture

The simulation is built around a strict separation between a **gameplay/physics root** and a **visual root** that must be preserved when editing.

### The ShipRoot / ShipVisualRoot split (critical)

- `ShipRoot` is the GameObject that actually **moves and rotates (yaw)** through the world. It carries `ShipMovementController`, `ShipBalanceController`, `ShipPlatformArea`, and `ShipFailureMonitor`.
- `ShipVisualRoot` is a **child** that only ever receives **roll/pitch tilt** for visual feedback. It is never moved by gameplay logic.
- Cargo and the piloting player are parented to `ShipVisualRoot` (the "ride parent") so they ride along with both translation and tilt. **Never apply gameplay translation/yaw to the visual root, and never apply tilt to the gameplay root.** This boundary is the backbone of the whole prototype.

### Balance model (`ShipBalanceController`)

The heart of the prototype. Every frame it reads all non-held `CargoItem`s currently on the deck (from `ShipPlatformArea.itemsInPlatform`), converts each item's world position into `ShipVisualRoot`-local coordinates, and computes **continuous, coordinate-weighted torque** (`torque = weight * local offset`) for roll (X axis) and pitch (Z axis). There are no discrete weight "zones" — `CargoZone` is a retired placeholder kept only for legacy debug enums/colors.

Outputs consumed by other systems:
- `speedMultiplier` (load → slower top speed), `turnPull` (roll imbalance → steering bias toward the heavy side), `loadPercent`, and `engineStrain`.
- Visual tilt applied by smoothing `ShipVisualRoot.localRotation` toward the target roll/pitch.

### Movement and mode toggling (`ShipMovementController` + `ShipThrottleLever`)

Implements heavy momentum/inertia flight (acceleration, passive glide deceleration, active braking, yaw inertia) and reads `speedMultiplier`/`turnPull` from the balance controller. **F is the ship-station key** (E stays cargo/nodes): F on the wheel takes/releases the helm; F is HELD to work the two big deck-hinged levers flanking it (`ShipDeckLever` base, built procedurally by `ShipHelm.Awake`). Starboard: the **engine telegraph** (`ShipThrottleLever`, five detents Reverse/Neutral/Slow/Medium/Fast → throttle −0.5/0/0.35/0.7/1, HOLDS its setting when released). Port: the **lift lever** (`ShipLiftLever`, Down/Neutral/Up → lift −1/0/+1, SPRING-RETURNS to Neutral on release, so the ship only climbs/descends while someone holds it). While a lever is held, mouse sensitivity drops (heavy feel) and pushing the view toward the BOW — the ship's forward axis projected into the grabber's screen space, so it works from either side of the stick — clicks the arm through its detents. The wheel only steers (A/D); the ship holds throttle with nobody at the helm. All stations are host-authoritative (`SetThrottle`/`SetLift` requests + stages in State packets). W/S + Space/Shift at the helm remain fallbacks only in scenes without levers.

### Boarding ramp (`ShipBoardingRamp` + `ShipRampButton`)

A button (F) on the port deck edge amidships extends a 6 m plank outboard, then swings it down until probe raycasts find the nearest terrain/object — capped at `maxDropAngle` (38°). While deployed it continuously RE-SEATS as the ship moves/tilts. The plank is a solid collider under `ShipVisualRoot` (layer 2 so its own probes never self-hit): players walk on it, `ShipRider` carries them, and a carried crate's weight counts at the carrier's position (strong outboard roll torque). If tilt would push the grounded ramp past its upper travel stop (−8°), the ramp writes a roll limit into `ShipBalanceController.externalRollMin/Max` — the ground props the ship up instead of being clipped (verified: port cargo wanting +4.8° roll gets clamped to the contact-derived limit). Only the deployed flag is synced (`SetRamp` + State); each peer seats its own ramp locally. The ship holds its set throttle with nobody at the helm; the wheel only steers (A/D) and lifts (Space/Shift). Host-authoritative: clients send `SetThrottle` requests and mirror the stage from State packets. W/S remain the throttle fallback only in scenes without a lever. **Tab toggles between walking and piloting:** when piloting begins, the player is reparented onto the ride parent, `FirstPersonController.allowMovement` is disabled (mouse-look stays on), `PlayerInteraction` is disabled, and any held cargo is dropped. Toggling back restores the player's original parent.

### Player (`FirstPersonController` + `PlayerInteraction`)

Deliberately minimal and kept as two separate components so the movement controller can be swapped without touching interaction. `FirstPersonController` uses a `CharacterController` (disabled while piloting). `PlayerInteraction` raycasts from the camera: `E` picks up / drops cargo, left mouse drops. Picking up makes a `CargoItem` kinematic + collider-off and parents it to the hold point; dropping restores dynamic physics.

### Cargo lifecycle (`CargoItem` + `ShipPlatformArea`)

`CargoItem` (requires a `Rigidbody`) is dynamic (gravity, sliding, rolling) while loose and kinematic while held. `ShipPlatformArea` uses a **trigger volume** (the "cargo deck air volume") on `ShipRoot`: `OnTriggerEnter/Exit` maintains `itemsInPlatform`, parents loose items to the ride parent in `FixedUpdate`, and on exit unparents them and injects the ship's current linear velocity so they retain momentum when they slide off.

### Procedural world (`WorldGenerator` + `IslandMeshBuilder`)

`WorldScene` (reached via HubScene → `HubController.LaunchToWorld`) is filled at runtime by `WorldGenerator`: an ~8×0.7×8 km volume of floating islands in four size tiers per map (1 very large / 2–3 large / 6–7 medium / 9–12 small) plus 2–4 derelicts, spaced edge-to-edge by `baseIslandGap`. Island bodies are **vertex-displaced meshes** from `IslandMeshBuilder` (noise-bumped walkable top welded to a craggy underside cone; the `Surface` struct recomputes exact top heights so props sit flush without raycasts), decorated with steep non-climbable rocks on top and crystals/roots/chunks below. Everything is **deterministic from the network world seed** (per-instance `System.Random`, fixed call order, no host/client branching) — static geometry is never synced. The generator also applies ambience at runtime (camera far clip + linear fog) and records every placed structure in a registry consumed by `ShipMapTable`, which builds a live flat 2D chart (tier-colored markers projected onto the board, altitude dropped, + live ship heading arrow) on the LowerCabin desk. Pressing F at the table enters a local-only overhead **chart view** (E/Q zoom, WASD pan, F exits); it suspends the whole `FirstPersonController` (its Update writes camera rotation every frame) and `PlayerInteraction`, and parents the camera to the table so ship motion carries the view.

### Resource nodes (`ResourceNode`)

Harvestable rocks (`WNode_0000` deterministic names) scattered on islands with type-colored veins (Stone/Ore/Crystal → matching `CargoCategory` values). `E` (via `PlayerInteraction.TryHarvestNode`) breaks one crate off at a clear, visible spot in front of the player (`ResourceNode.FindDropPoint` — overlap + line-of-sight checked); each node has a finite seed-rolled yield and shrinks/loses its veins as it depletes. Raw resources come ONLY from nodes; islands keep occasional loose Fuel/Treasure crates and derelicts keep their loot pool.

### Networking (`NetworkManagerP2P`)

A self-contained pure-C# UDP peer-to-peer manager (no Netcode/transport package dependency) supporting Host + up to 3 clients. A background receive thread enqueues packets into a `ConcurrentQueue` that's drained on the Unity main thread (Unity APIs are not thread-safe). Packets are JSON (`JsonUtility`) carrying player, cargo, and ship state. **The host is authoritative for ship kinematics, loose cargo, and node yields**; clients lerp toward host state and set synced cargo kinematic. Remote players are represented by procedurally generated "puppet" primitives. `StartGame` packets tell clients to load the gameplay scene. Harvesting is host-validated: clients send `HarvestRequest`, the host consumes yield and broadcasts `HarvestResult` (crate name `nodeId_cN`, category, spawn position) so every peer spawns the identical crate; node yields and cargo categories also ride in every 20 Hz `State` packet, so a client that misses a result packet self-heals (unknown crate names in the State cargo list are spawned on sight).

### Menu flow (`MainMenuManager`)

uGUI-based panel navigation (Main/Lobby/Settings/Join) wired to `NetworkManagerP2P` for hosting/joining. The host's **Start Game** broadcasts a `StartGame` notice and loads `gameplaySceneName` (`MainGameScene`); settings persist via `PlayerPrefs`.

## Conventions

- **Anti-jitter execution chain**: ship movers run at explicit negative `[DefaultExecutionOrder]` — `NetworkManagerP2P` (−110, client ship lerp + pilot input) → `ShipMovementController` (−100) → `ShipBalanceController` (−90, tilt) → `ShipRider` (−50, deck-carry) → player scripts (0). The deck-carry must land BETWEEN ship motion and the player's own `CharacterController.Move`, or standing players vibrate during climbs/decel (depenetration pops against the freshly-moved deck). Don't move these scripts between update phases or orders without preserving this chain.
- Most components **auto-wire their references** in `Awake`/`Start` (e.g. via `GetComponent`, `FindAnyObjectByType`, or `GameObject.Find("ShipRoot")`/`"Player"`). When adding components or renaming those root GameObjects, keep these lookups working or assign the inspector references explicitly.
- Inspector fields are heavily organized with `[Header]`/`[Tooltip]` and read-only runtime state is surfaced in the inspector for debugging — follow this pattern for new tunable systems.
- `.meta` files are committed and required by Unity; always let file operations create/preserve them and never edit GUIDs by hand.

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

### Movement and mode toggling (`ShipMovementController`)

Implements heavy momentum/inertia flight (acceleration, passive glide deceleration, active braking, yaw inertia) and reads `speedMultiplier`/`turnPull` from the balance controller. **Tab toggles between walking and piloting:** when piloting begins, the player is reparented onto the ride parent, `FirstPersonController.allowMovement` is disabled (mouse-look stays on), `PlayerInteraction` is disabled, and any held cargo is dropped. Toggling back restores the player's original parent.

### Player (`FirstPersonController` + `PlayerInteraction`)

Deliberately minimal and kept as two separate components so the movement controller can be swapped without touching interaction. `FirstPersonController` uses a `CharacterController` (disabled while piloting). `PlayerInteraction` raycasts from the camera: `E` picks up / drops cargo, left mouse drops. Picking up makes a `CargoItem` kinematic + collider-off and parents it to the hold point; dropping restores dynamic physics.

### Cargo lifecycle (`CargoItem` + `ShipPlatformArea`)

`CargoItem` (requires a `Rigidbody`) is dynamic (gravity, sliding, rolling) while loose and kinematic while held. `ShipPlatformArea` uses a **trigger volume** (the "cargo deck air volume") on `ShipRoot`: `OnTriggerEnter/Exit` maintains `itemsInPlatform`, parents loose items to the ride parent in `FixedUpdate`, and on exit unparents them and injects the ship's current linear velocity so they retain momentum when they slide off.

### Networking (`NetworkManagerP2P`)

A self-contained pure-C# UDP peer-to-peer manager (no Netcode/transport package dependency) supporting Host + up to 3 clients. A background receive thread enqueues packets into a `ConcurrentQueue` that's drained on the Unity main thread (Unity APIs are not thread-safe). Packets are JSON (`JsonUtility`) carrying player, cargo, and ship state. **The host is authoritative for ship kinematics and loose cargo**; clients lerp toward host state and set synced cargo kinematic. Remote players are represented by procedurally generated "puppet" primitives. `StartGame` packets tell clients to load the gameplay scene.

### Menu flow (`MainMenuManager`)

uGUI-based panel navigation (Main/Lobby/Settings/Join) wired to `NetworkManagerP2P` for hosting/joining. The host's **Start Game** broadcasts a `StartGame` notice and loads `gameplaySceneName` (`MainGameScene`); settings persist via `PlayerPrefs`.

## Conventions

- Most components **auto-wire their references** in `Awake`/`Start` (e.g. via `GetComponent`, `FindAnyObjectByType`, or `GameObject.Find("ShipRoot")`/`"Player"`). When adding components or renaming those root GameObjects, keep these lookups working or assign the inspector references explicitly.
- Inspector fields are heavily organized with `[Header]`/`[Tooltip]` and read-only runtime state is surfaced in the inspector for debugging — follow this pattern for new tunable systems.
- `.meta` files are committed and required by Unity; always let file operations create/preserve them and never edit GUIDs by hand.

# Huawei Rendering Benchmark — Technical Approach and Current Status

## Purpose of This Document

This note explains, at both a high level and a technical level, how the Unity benchmark project has been implemented against the Statement of Work dated 8 April 2026, why specific design choices were made, what files were added or changed, how Variable Rate Shading (VRS) is applied, and what has and has not been verified yet.

It is intended to be shareable with the client.

---

## Executive Summary

The implementation now supports two benchmark modes in the Oasis Unity scene:

1. **Cinematic mode**
   This is the original SOW-aligned mode. It runs a fixed one-minute camera route using the same animation every time, with 10 seconds of static camera and 50 seconds of motion at varying speeds. This mode exists to satisfy the Phase 1 requirement for a repeatable, comparable measurement path.

2. **Gameplay Replay mode**
   This was added after later discussion to better represent real gameplay. Instead of moving the camera by a fixed Timeline path, it records real user gameplay input and replays that input back through the live first-person controller. This preserves controller integration, gravity, collisions, camera noise, and path variation.

The result is a benchmark harness that supports both:

- a **controlled repeatability mode** for strict comparison, and
- a **real gameplay mode** for more realistic performance behaviour.

This split is intentional. These two modes answer different questions.

---

## Alignment With the SOW

### Phase 1 requirement: fixed one-minute camera path

The SOW explicitly requires:

- a reasonably complex Unity demo project,
- a fixed camera walk-through path so the same animation can be replayed under different rendering configurations,
- one minute total duration,
- 10 seconds static camera,
- 50 seconds camera motion at different speeds,
- refresh-rate limiting at 120, 60, 40 and 30 fps,
- hardware VRS modes 1x2, 2x2 and 4x4,
- and a start screen with buttons to activate the options.

This requirement is met by the **Cinematic mode** implementation.

### Later discussion: real gameplay rather than only a fixed camera

In later discussion, the preference shifted toward observing behaviour closer to real gameplay rather than only a fixed cinematic route. That is a valid requirement, but it is not exactly the same measurement problem as the original Phase 1 SOW.

For that reason, the project now keeps the original cinematic route and adds a separate **Gameplay Replay mode** rather than replacing the original approach.

That means:

- the project still satisfies the original fixed-animation requirement, and
- it also supports a more realistic, controller-driven measurement mode.

### Phase 2 requirement: 100 ms statistics and throttling awareness

The current implementation already logs benchmark rows every 100 ms and flags likely throttling based on frame-rate collapse relative to the chosen target. However, **device power draw, GPU utilisation and CPU utilisation are not currently collected by this Unity codebase**. Those remain Phase 2 items and likely require Huawei-specific tooling or private APIs.

### Phase 3 requirement

Phase 3 is explicitly outside this SOW. It is not part of the Unity implementation here.

---

## Why This Architecture Was Chosen

### Why the fixed cinematic route was chosen first

The first implementation was based on a fixed Timeline route because the SOW asked for the same animation to be replayed under different rendering settings. A fixed Timeline route is the most direct way to satisfy that requirement.

Benefits of the cinematic route:

- identical camera path on every run,
- identical scene visibility pattern on every run,
- clean comparison between rendering settings,
- straightforward one-minute schedule,
- and easy explanation to the client.

### Why gameplay replay was then added

A fixed camera route is good for controlled comparison, but it does not fully represent the variability of real player motion. In real gameplay, the engine is affected by:

- player input noise,
- camera jitter,
- collisions,
- gravity,
- controller acceleration and rotation,
- route drift,
- and the timing behaviour of the live gameplay stack.

Because later discussion indicated that this realism matters, gameplay replay was added as a separate mode.

### Why gameplay replay uses recorded controller input instead of direct transform motion

An earlier experimental approach used direct transform playback. That was rejected as the final gameplay method because it bypassed the real first-person controller.

The final gameplay replay design instead records and replays the same input state consumed by the live controller:

- movement,
- look,
- jump,
- sprint.

This means the run still passes through:

- `StarterAssetsInputs`,
- `FirstPersonController`,
- `CharacterController`,
- gravity and grounded logic,
- and the real camera logic.

This is the correct choice if the goal is to measure performance under gameplay-like conditions rather than only under a fixed pre-authored shot.

### Why both modes are kept

The two modes serve different purposes:

- **Cinematic mode** is better for strict, low-noise comparison and SOW traceability.
- **Gameplay Replay mode** is better for realism and client-facing gameplay behaviour.

Keeping both is stronger than choosing only one.

---

## Current File-Level Design

### `Assets/StartScreenUI.cs`

This is the main benchmark harness.

It is responsible for:

- finding `PlayerManager`,
- building the entire benchmark UI in code,
- applying frame-rate caps,
- applying VRS modes,
- selecting between Cinematic and Gameplay Replay modes,
- starting the fixed flythrough path,
- starting gameplay replay runs,
- and running the 12-case cinematic matrix.

Important design points:

- **Cinematic is still the default mode.** This preserves the original benchmark behaviour.
- **Gameplay Replay is a separate selectable mode.**
- **The 12-case matrix remains cinematic-only.** This was intentional, because the original matrix requirement is the fixed-animation case.
- **Replay mode loads a previously recorded gameplay input file and starts the run through the live first-person controller path.**

### `Assets/FlythroughController.cs`

This file started as a Timeline speed-schedule controller and on-screen FPS overlay.

It now has a dual role:

- in Cinematic mode, it still drives the Timeline speed schedule,
- in both Cinematic and Gameplay Replay modes, it acts as the shared on-screen measurement and CSV logging component.

This refactoring was chosen so that:

- the same FPS/VRS overlay is used in both modes,
- the same CSV cadence is used in both modes,
- and logging logic is not duplicated in multiple scripts.

### `Assets/InputRecorder.cs`

This records real gameplay input from the Oasis first-person controller stack.

It records:

- current scene path,
- player start position,
- player start rotation,
- starting camera pitch,
- and per-frame gameplay input state:
  - move,
  - look,
  - jump,
  - sprint.

This is written to `gameplay_input_recording.bin` inside `Application.persistentDataPath`.

### `Assets/InputReplayer.cs`

This replays the recorded gameplay input back through the real gameplay controller.

It does **not** move the transform directly.

Instead, it:

- resets the player to the recorded start pose,
- restores the recorded camera pitch,
- optionally disables live `PlayerInput` during the run,
- and feeds recorded input back into `StarterAssetsInputs`.

This preserves real controller behaviour and is the main reason this mode is suitable for gameplay-like testing.

### `Assets/SharedAssets/Scripts/Runtime/PlayerManager.cs`

This is an existing runtime script from the project. It manages transitions between the flythrough state and first-person control.

This is relevant because:

- Cinematic mode depends on `EnableFlythrough()`, and
- Gameplay Replay mode depends on switching back into first-person control via `EnableFirstPersonController()`.

### `README.md`

The README documents project setup and the baseline benchmark path. It is useful as a project-level setup guide.

This new note is intended to be the more detailed client-facing technical explanation.

---

## What Was Added or Changed

### Major implemented changes

1. **Fixed cinematic benchmark path**
   A one-minute flythrough path with the required 10 seconds static plus 50 seconds of motion at different speeds.

2. **Start screen UI**
   Added an in-engine start screen that allows the user to select:
   - refresh cap,
   - VRS mode,
   - and now also the run mode.

3. **12-case cinematic matrix runner**
   Added an automatic runner for the required refresh-rate/VRS combinations.

4. **CSV logging every 100 ms**
   Added regular benchmark logging for performance monitoring.

5. **Gameplay replay mode**
   Added a client-requested mode that replays real gameplay input through the real controller.

6. **Replay-aware CSV metadata**
   Replay runs now include metadata such as replay file name and scenario in the CSV header.

7. **Unused capture path removed from active design**
   The earlier transform-based replay idea was superseded by the gameplay-input replay approach.

### Relevant recent commits

- `27d9af2` — add 12-case matrix runner and fix button highlight state after batch run
- `e12bfa6` — switch recorder/replayer to real gameplay input for Oasis
- `e158eea` — add Oasis gameplay replay benchmark mode and CSV metadata

---

## How the Cinematic Mode Works

The cinematic mode is based on the existing `PlayableDirector` and `PlayerManager.EnableFlythrough()` path.

At a high level:

1. The user selects the frame-rate cap and VRS mode.
2. `StartScreenUI` applies the frame-rate cap and VRS.
3. The code starts the Timeline flythrough through `PlayerManager.EnableFlythrough()`.
4. `FlythroughController` immediately sets the Timeline speed to zero, then applies the one-minute schedule.
5. The overlay displays live FPS and VRS state.
6. CSV rows are emitted every 100 ms.

The speed schedule is:

- 10 s at 0.00x
- 10 s at 0.50x
- 8 s at 1.50x
- 10 s at 0.60x
- 7 s at 1.20x
- 15 s at 0.24x

This consumes 35 seconds of authored Timeline content across exactly 60 seconds of wall-clock time.

This directly matches the original Phase 1 requirement for a fixed walk-through with varying motion speed.

---

## How the Gameplay Replay Mode Works

The gameplay replay mode is intended to answer a different question: not "what happens on a fixed cinematic route?" but "what happens during gameplay-like movement under the same rendering settings?"

At a high level:

1. A gameplay input recording is created in Oasis.
2. The user selects Gameplay Replay mode on the start screen.
3. `StartScreenUI` loads the input recording.
4. The benchmark switches back into the first-person controller state.
5. `InputReplayer` restores the recorded start pose.
6. `InputReplayer` feeds recorded move/look/jump/sprint values into `StarterAssetsInputs`.
7. `FirstPersonController` and `CharacterController` process those inputs normally.
8. `FlythroughController` logs FPS/VRS data during the replay using the same CSV pipeline used for cinematic mode.

This means gameplay replay preserves:

- live collision behaviour,
- gravity,
- grounded state changes,
- controller acceleration,
- camera rotation,
- and natural noise introduced by the real gameplay stack.

This is useful when the goal is realism rather than perfectly repeatable camera motion.

---

## Why Gameplay Replay Is Not the Same as the Original SOW Path

This point is important and should be stated clearly.

The SOW’s original fixed-animation requirement is about **comparing rendering settings under the same camera route**. Gameplay replay, by contrast, allows controller noise and frame-rate-sensitive route variation to remain part of the run.

That means:

- gameplay replay is **more realistic**, but
- gameplay replay is **less controlled** than the fixed cinematic route.

Therefore, gameplay replay should be viewed as an **additional analysis mode**, not as a replacement for the original fixed-animation benchmark.

---

## How VRS Is Implemented

### API path used

VRS is applied using Tuanjie/Unity rendering APIs:

- `GraphicsSettings.variableRateShadingMode`
- `Renderer.shadingRate`

### Supported VRS modes in the UI

The user-facing options are:

- `VRS Off`
- `1x2`
- `2x2`
- `4x4`

The SOW requires the hardware VRS modes:

- `1x2`
- `2x2`
- `4x4`

The project includes `VRS Off` as an additional baseline, which is useful for comparison.

### How the mapping works

The code maps the UI mode to the engine fragment size:

- `1x2` -> `ShadingRateFragmentSize.Size1x2`
- `2x2` -> `ShadingRateFragmentSize.Size2x2`
- `4x4` -> `ShadingRateFragmentSize.Size4x4`

The benchmark then:

1. checks `SystemInfo.shadingRateTypeCaps`,
2. sets `GraphicsSettings.variableRateShadingMode`,
3. iterates scene renderers,
4. skips renderers under a `Canvas`,
5. and applies `Renderer.shadingRate` to scene renderers only.

Skipping UI renderers is intentional because non-1x1 shading on UI can be meaningless or visually problematic.

### What the project currently checks

The project checks the following at runtime:

- whether the device reports VRS hardware capability via `SystemInfo.shadingRateTypeCaps`,
- which `GraphicsSettings.variableRateShadingMode` is currently active,
- and how many scene renderers were assigned a VRS rate.

This information is shown on-screen and written into the CSV headers/logging path.

### What the project does not yet prove conclusively

This project does **not** prove at a low-level GPU-driver or hardware-profiler level that the hardware used the exact requested shading pattern for every draw.

To prove that conclusively, one would still want vendor-level profiling or Huawei-assisted verification, for example through:

- Huawei DevEco Profiler,
- or a GPU debugging/profiling tool available for the target hardware.

In other words:

- the project **does configure VRS through the engine APIs**, and
- the project **does check the engine-reported capability and mode**, but
- final hardware-level proof is still a profiling task, not something the current Unity code can guarantee alone.

---

## CSV Logging and Measurement Behaviour

The benchmark logs rows every 100 ms.

Each row includes:

- elapsed time,
- measured FPS,
- phase index,
- current speed multiplier,
- active VRS mode,
- number of VRS-modified renderers,
- and a simple throttling indicator.

Replay runs add metadata such as:

- replay mode,
- scenario,
- replay file name,
- replay scene path,
- replay duration.

This satisfies the Phase 1/Phase 2 expectation that the run should be observable in a structured way at 100 ms cadence, even though device power telemetry is not yet part of this Unity-side implementation.

---

## Thermal Throttling Detection

The current code does **not** read a device thermal API directly.

However, it does include a simple throttling indicator based on FPS collapse relative to the selected target frame rate.

This is useful as an early warning signal but should not be treated as a complete thermal diagnosis.

Proper thermal-throttling verification for Phase 2 would ideally include:

- device temperature or thermal-state APIs,
- power measurement,
- and ideally controlled cooling during repeated runs.

This is consistent with the SOW note that active cooling may be required and that Huawei assistance may be needed.

---

## What Has Been Verified

Based on the current codebase and implementation review, the following have been verified at code level:

- the Oasis benchmark supports frame-rate caps of 120, 60, 40 and 30 fps,
- the Oasis benchmark supports VRS Off, 1x2, 2x2 and 4x4,
- the cinematic mode still exists and remains the default mode,
- the cinematic matrix runner still exists and is restricted to the fixed-animation mode,
- the gameplay replay mode now routes through the real first-person controller path,
- the shared overlay/CSV logger supports both run types,
- replay CSV files now include replay-specific metadata,
- and the updated scripts compile cleanly.

---

## What Has Not Been Fully Verified Yet

The following items remain open or partially verified:

1. **Hardware-level confirmation of VRS effectiveness**
   The engine configuration path is in place, but hardware-profiler confirmation is still recommended.

2. **Phase 2 power-draw collection**
   Not yet implemented in this Unity code. This likely requires Huawei assistance or additional tooling.

3. **Optional GPU/CPU utilisation collection**
   Not yet implemented in this Unity code.

4. **Command-line Harmony build stability in the current local environment**
   The intended build path is through DevEco Studio. A previously investigated command-line `hvigor` path was blocked by a local SDK registration issue. That is a tooling/environment issue rather than a benchmark-design issue.

5. **Desktop screen recording automation**
   The SOW mentions desktop recordings. The project no longer relies on the earlier unused `CameraRecorder.cs` path. Desktop capture would need to be handled either by Unity Recorder, an OS recording tool, or a separate clean capture workflow.

6. **Multiple replay-file selection in the UI**
   The current gameplay replay path loads the default replay file unless configured otherwise. This is sufficient for initial use but can be extended.

---

## Risks and Limitations

### 1. Gameplay replay is intentionally noisy

This is a feature, not a bug, but it changes how results should be interpreted.

Because gameplay replay keeps real controller behaviour active, two runs at different frame rates may not traverse the scene in exactly the same way. That means gameplay replay should be analysed statistically rather than treated as a perfectly repeatable path.

Recommended interpretation:

- use **Cinematic mode** for controlled, low-noise comparison,
- use **Gameplay Replay mode** for realism,
- and do not confuse the two.

### 2. Replay files are scene-specific

A replay recorded in Oasis should be treated as an Oasis replay. Reusing a replay in another scene may not make sense and is not the current intended workflow.

### 3. PlayerManager assumptions depend on the Oasis setup

Gameplay Replay mode relies on the Oasis first-person controller and `PlayerManager` structure. This is appropriate for the current demo project, but it is not a general benchmark framework for arbitrary Unity scenes yet.

### 4. Harmony/OpenHarmony build tooling remains environment-sensitive

The Unity-side project is structured for Harmony/OpenHarmony export, but final deployment still depends on a correctly installed SDK and DevEco environment.

---

## Why This Is the Right Current Approach

From a first-principles point of view, the current implementation is a good fit because it separates two distinct benchmarking goals instead of forcing one method to do both jobs badly.

### If the goal is strict rendering comparison

Use **Cinematic mode**.

Why:

- same route,
- same timing model,
- same scene visibility pattern,
- lower measurement noise,
- directly aligned to the original SOW wording.

### If the goal is realistic gameplay behaviour

Use **Gameplay Replay mode**.

Why:

- real controller path,
- real gameplay motion,
- real collisions and gravity,
- real camera behaviour,
- more representative of player experience.

### Why keeping both is the strongest answer

Because the client’s later request introduced a different measurement objective, the best answer is not to delete the original method. The best answer is to preserve the original SOW-compliant route and add the more realistic route alongside it.

That is what has now been implemented.

---

## Recommended Next Steps

### Short-term

1. Record one or more Oasis gameplay replay files representative of the scenarios the client cares about.
2. Run the cinematic matrix for baseline comparison.
3. Run repeated gameplay replay tests per VRS setting and compare distributions rather than only single runs.

### Medium-term

1. Integrate power-draw collection for Phase 2.
2. Add GPU/CPU utilisation if Huawei APIs or tools permit it.
3. Add replay-file selection in the UI.
4. Define a clean desktop capture workflow for screen recordings.

### Validation

1. Confirm VRS behaviour on-device with Huawei-supported profiling tools.
2. Validate that the chosen gameplay replay scenarios are representative.
3. Validate that thermal control during testing is adequate.

---

## Final Position

The implementation is now in a good state for Phase 1 benchmarking because it supports both:

- the original fixed one-minute camera route required by the SOW, and
- the later-requested gameplay-like replay mode.

The rendering configuration controls, VRS controls, overlay, and CSV logging are all present. The main remaining gaps are Phase 2 telemetry and environment-level deployment/tooling verification rather than missing benchmark logic inside Unity.

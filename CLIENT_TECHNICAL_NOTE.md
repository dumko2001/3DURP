# Huawei Rendering Benchmark — Client Summary

## Purpose

This note explains what has been built for the Unity rendering benchmark, how it maps to the Statement of Work dated 8 April 2026, why the current technical approach was chosen, and what has and has not been verified yet.

---

## Summary

The project now supports two benchmark modes in the Oasis Unity scene:

1. **Cinematic mode**
   A fixed one-minute benchmark route designed to satisfy the original Phase 1 SOW requirement for repeatable measurements under the same camera animation.

2. **Gameplay Replay mode**
   A separate mode added after later discussion to better represent real gameplay. This records real player input and replays it through the live first-person controller rather than moving the camera along a fixed path.

The reason both modes are kept is simple:

- **Cinematic mode** is best for controlled, low-noise comparison.
- **Gameplay Replay mode** is best for realistic gameplay behaviour.

They answer different questions, so it is stronger to support both than to force one method to do both jobs.

---

## SOW Alignment

### Phase 1

The original Phase 1 requirements were:

- use a reasonably complex Unity demo,
- create a fixed walk-through path that can be rerun under different rendering settings,
- make that path one minute long,
- include 10 seconds of static camera and 50 seconds of motion at different speeds,
- support frame-rate limits of 120, 60, 40 and 30 fps,
- support hardware VRS modes 1x2, 2x2 and 4x4,
- and provide a start screen to choose the options.

This is satisfied by the current **Cinematic mode** implementation.

### Later client direction

Later discussion introduced a second goal: measure behaviour closer to real gameplay, not only a fixed cinematic route. That is a valid extension, but it is not identical to the original SOW benchmark problem.

For that reason, the project now supports **Gameplay Replay mode** in addition to the original fixed camera route, rather than replacing it.

### Phase 2

The project already logs benchmark rows every 100 ms and includes a simple throttling indicator. However, the following are **not yet collected by the Unity codebase**:

- total device power draw,
- current device power draw,
- GPU utilisation,
- CPU utilisation.

Those remain Phase 2 items and likely require Huawei tooling, DevEco profiling, or private APIs.

### Phase 3

Phase 3 is outside the Unity implementation covered here.

---

## Why This Architecture Was Chosen

### Why the fixed cinematic route exists

The SOW asked for the same animation to be rerun under different rendering settings. A Timeline-based route is the most direct and defensible way to do that.

Benefits:

- identical camera path each run,
- identical scene visibility pattern each run,
- lower measurement noise,
- straightforward SOW traceability.

### Why gameplay replay was added

A fixed camera route is useful for controlled comparison, but it does not reflect live gameplay behaviour. Real gameplay includes:

- input noise,
- collision response,
- gravity,
- controller acceleration,
- camera jitter,
- and frame-rate-sensitive route variation.

Because that realism matters for the client’s later requirement, gameplay replay was added as a separate mode.

### Why gameplay replay uses input replay instead of transform playback

The final design records and replays the same input state used by the live controller:

- move,
- look,
- jump,
- sprint.

This means the replay still passes through:

- `StarterAssetsInputs`,
- `FirstPersonController`,
- `CharacterController`,
- gravity and grounded logic,
- and the real camera logic.

That is the correct design if the goal is gameplay realism. Direct transform playback was not kept as the final gameplay method because it would bypass too much of the real controller stack.

---

## What Has Been Implemented

### 1. Fixed cinematic benchmark path

A one-minute route with:

- 10 seconds static camera,
- 50 seconds motion,
- varying speed phases,
- and repeatable execution.

### 2. Start-screen benchmark UI

The in-app start screen lets the user choose:

- run mode,
- frame-rate cap,
- VRS mode,
- gameplay replay file,
- and benchmark start.

### 3. 12-case cinematic matrix runner

The original matrix runner remains in place for the fixed-animation benchmark.

### 4. Shared FPS/VRS overlay and CSV logging

Both modes use the same on-screen overlay and the same CSV logging path.

### 5. Gameplay input recorder and replayer

The project now supports recording Oasis gameplay input and replaying it back through the live first-person controller.

---

## Key Files and Their Roles

### `Assets/StartScreenUI.cs`

Main benchmark launcher.

Responsibilities:

- find `PlayerManager`,
- build the benchmark UI in code,
- apply frame-rate caps,
- apply VRS modes,
- switch between Cinematic and Gameplay Replay modes,
- list and select available gameplay replay files,
- launch the fixed flythrough,
- launch gameplay replay runs,
- run the 12-case cinematic matrix.

Important point:

- **Cinematic mode remains the default**, so the original benchmark behaviour is preserved.

### `Assets/FlythroughController.cs`

Shared benchmark runtime.

Responsibilities:

- drive the cinematic speed schedule in Cinematic mode,
- show live FPS/VRS overlay,
- write benchmark CSV rows every 100 ms,
- support both Cinematic and Gameplay Replay logging paths.

### `Assets/InputRecorder.cs`

Records Oasis gameplay input.

It stores:

- scene path,
- player start position,
- player start rotation,
- starting camera pitch,
- per-frame move/look/jump/sprint input state.

New recordings are now saved as timestamped `.bin` files inside `Application.persistentDataPath`, so multiple gameplay paths can be kept and selected later.

Each new gameplay recording session now resets to the captured benchmark start pose before recording begins, so repeated recordings do not continue from the previous end position.

### `Assets/InputReplayer.cs`

Replays the recorded gameplay input back through the live gameplay controller.

It does **not** move the transform directly.

Instead, it:

- restores the recorded start pose,
- restores the recorded camera pitch,
- feeds the recorded input back into `StarterAssetsInputs`,
- and lets the real first-person controller process it.

### `Assets/GameplayInputResolver.cs`

Small helper used to find the correct active gameplay input target.

This matters because the project can contain multiple `StarterAssetsInputs` instances, inactive prefabs, or temporary objects in the scene. The resolver scores candidates and prefers the active first-person setup with the expected controller components attached.

That reduces the risk of the recorder, replayer, or benchmark UI binding to the wrong player object.

### `Assets/SharedAssets/Scripts/Runtime/PlayerManager.cs`

Existing project script used to switch between flythrough mode and first-person mode.

This matters because:

- Cinematic mode relies on `EnableFlythrough()`,
- Gameplay Replay mode relies on `EnableFirstPersonController()`.

---

## How the Two Benchmark Modes Work

### Cinematic mode

Flow:

1. User selects FPS and VRS.
2. `StartScreenUI` applies the selected settings.
3. `PlayerManager.EnableFlythrough()` starts the Timeline-based route.
4. `FlythroughController` applies the one-minute speed schedule.
5. Overlay and CSV logging run throughout the benchmark.

This is the SOW-aligned fixed-animation path.

### Gameplay Replay mode

Flow:

1. A gameplay recording is first created in Oasis.
2. User selects Gameplay Replay mode.
3. `StartScreenUI` shows the available replay files and loads the selected file.
4. The benchmark switches into first-person controller mode.
5. `InputReplayer` restores the recorded start pose.
6. `InputReplayer` feeds recorded move/look/jump/sprint values into the live controller stack.
7. `FlythroughController` logs FPS/VRS data during the run.

This keeps real controller behaviour, including collisions, gravity and input noise.

For on-device recording, gameplay uses the existing mobile touch controls from the first-person controller package, and recording can be stopped with an on-screen `STOP & SAVE` overlay button rather than relying on a hardware keyboard.

The mobile recording controls are the existing move/look touch controls plus jump and sprint buttons. Replay runs themselves do not require manual controls.

---

## How VRS Is Implemented

The benchmark uses Unity/Tuanjie rendering APIs:

- `GraphicsSettings.variableRateShadingMode`
- `Renderer.shadingRate`

Supported UI options are:

- `VRS Off`
- `1x2`
- `2x2`
- `4x4`

The SOW-required hardware VRS modes are:

- `1x2`
- `2x2`
- `4x4`

`VRS Off` is included as a useful baseline.

At runtime the benchmark:

1. checks `SystemInfo.shadingRateTypeCaps`,
2. sets `GraphicsSettings.variableRateShadingMode`,
3. iterates scene renderers,
4. skips renderers under a `Canvas`,
5. applies `Renderer.shadingRate` to scene renderers.

Skipping UI renderers is intentional. UI is not the right target for this benchmark, and applying non-1x1 shading to UI can create misleading visuals.

### What this verifies

The code verifies:

- engine-reported VRS capability,
- engine-reported active VRS mode,
- and the number of renderers given a VRS rate.

### What this does not fully prove

The current Unity code does **not** prove hardware-level VRS execution at the GPU-driver level. Final confirmation would still benefit from Huawei-supported profiling tools such as DevEco Profiler or equivalent GPU-level validation.

So the correct statement is:

- the project **does configure VRS correctly through engine APIs**,
- the project **does verify the engine-reported mode and capability**,
- but **final hardware proof still requires profiling**.

---

## Logging, Storage and Device Workflow

### What is logged

The benchmark writes rows every 100 ms containing:

- elapsed time,
- measured FPS,
- phase index,
- current speed multiplier,
- active VRS mode,
- number of VRS-modified renderers,
- a simple throttling flag.

Replay runs also add CSV metadata such as:

- replay file name,
- replay scene,
- replay duration,
- run mode.

### Where files are saved

The files are written to Unity’s `Application.persistentDataPath`.

That means:

- when the app runs in the Unity Editor, the files are saved on the local development machine,
- when the app runs on the Huawei device, the files are saved on the Huawei device.

The current files of interest are:

- benchmark CSV files,
- `gameplay_input_*.bin` replay files.

### Does the device need to stay connected?

No. The device does **not** need to remain connected to the computer just to run the benchmark after installation.

The device connection is mainly needed for:

- build and deployment,
- pulling files back from the device,
- debugging,
- profiling.

### How files are retrieved

The expected workflow is to pull them back from the device with `hdc file recv` after the run.

---

## Verification Status

At code level, the following have been verified:

- Oasis benchmark supports frame-rate caps of 120, 60, 40 and 30 fps,
- Oasis benchmark supports VRS Off, 1x2, 2x2 and 4x4,
- Cinematic mode still exists and remains the default mode,
- the cinematic matrix runner remains in place,
- Gameplay Replay mode routes through the real first-person controller path,
- both modes use the same overlay and CSV logger,
- replay CSVs include replay-specific metadata,
- the current updated scripts compile cleanly.

Also, a practical UI edge case was fixed during review: single Cinematic and Gameplay Replay runs now return cleanly to the menu after completion.

---

## What Is Not Yet Fully Verified or Implemented

The following items remain open:

1. **Hardware-level VRS confirmation**
   Engine-level setup is in place, but hardware-level confirmation still needs profiling.

2. **Phase 2 power telemetry**
   Device power draw is not yet collected by this Unity code.

3. **Optional GPU/CPU utilisation collection**
   Not yet implemented in this Unity code.

4. **Harmony/OpenHarmony command-line build robustness in the local environment**
   The recommended build route remains DevEco Studio. A previous local `hvigor` issue was an SDK/tooling registration problem rather than a benchmark logic problem.

5. **Desktop recording workflow**
   Still needs a clear final method, for example Unity Recorder, OS-level capture, or another approved desktop path.

6. **Replay-file management niceties**
   Replay-file selection is now available in the UI. Optional future improvements would be rename/delete or richer replay metadata in the picker.

---

## Risks and Limitations

### Gameplay Replay is intentionally noisy

This is by design.

Because replay runs through the live controller, different frame rates can lead to slightly different route outcomes. That is what makes this mode more realistic, but also less controlled.

Recommended interpretation:

- use **Cinematic mode** for strict comparison,
- use **Gameplay Replay mode** for realism,
- treat Gameplay Replay as a distribution-based measurement rather than a perfectly repeatable path.

### Replay files are scene-specific

An Oasis replay should be treated as an Oasis replay.

### Gameplay Replay currently depends on the Oasis controller setup

This is appropriate for the approved demo project, but it is not yet a generic benchmark system for arbitrary Unity scenes.

### Final deployment still depends on the Huawei/DevEco environment

The Unity-side benchmark logic is in place, but final build/deploy steps still depend on a correct Harmony/OpenHarmony SDK and DevEco configuration.

---

## Recommended Use

### Use Cinematic mode when the goal is:

- same route,
- same timing model,
- same scene visibility pattern,
- lower measurement noise,
- direct traceability to the original SOW.

### Use Gameplay Replay mode when the goal is:

- realistic gameplay motion,
- live controller behaviour,
- collision and gravity effects,
- more representative player-facing behaviour.

The strongest overall approach is to keep both:

- **Cinematic mode** as the SOW-compliant baseline,
- **Gameplay Replay mode** as the realism-oriented extension.

---

## Recommended Next Steps

### Short-term

1. Record one or more Oasis gameplay replay files representing the client’s chosen gameplay scenarios.
2. Run the cinematic matrix for baseline comparison.
3. Run repeated gameplay replay tests per VRS setting and compare distributions rather than single runs.

### Medium-term

1. Integrate Phase 2 power collection.
2. Add GPU/CPU utilisation if Huawei tooling allows it.
3. Define the desktop recording workflow.
4. Optionally improve replay-file management in the UI.

### Validation

1. Confirm VRS behaviour on-device with Huawei-supported profiling tools.
2. Validate that the replay scenarios are representative of real use.
3. Validate thermal control during repeated test runs.

---

## Final Position

The implementation is in a good state for Phase 1 because it now supports both:

- the original fixed one-minute camera route required by the SOW, and
- the later-requested gameplay-like replay mode.

The key remaining gaps are not missing Unity-side benchmark logic. They are mainly:

- Phase 2 telemetry,
- hardware-level VRS confirmation,
- and environment/tooling validation for deployment and profiling.

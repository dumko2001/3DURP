# Rendering Benchmark — Huawei Mobile (Phase 1)

Unity app for benchmarking rendering performance on a Huawei mobile device.  
Runs a fixed 60-second cinematic camera flythrough under selectable refresh-rate and VRS configurations. Logs FPS, VRS state, and throttle events to CSV.

> **Note:** This repository contains only the **custom logic and configuration**. The large environment assets must be reconstructed from the standard Tuanjie URP template.

---

## Prerequisites

| Requirement | Version / Note |
|---|---|
| **Tuanjie Editor** (Huawei Unity fork) | **1.8.5** (2022.3.62t7) — install via Tuanjie Hub |
| **DevEco Studio** | 4.0+ — required to sign and deploy the HAP |
| **OpenHarmony SDK** | installed via DevEco Studio SDK Manager |
| Target device | Huawei running HarmonyOS 4+ with USB debugging enabled |
| USB debugging | enabled in Developer Options on device |

---

## How to Replicate / Setup

To keep the repository lightweight, the standard "Oasis" environment assets are not included. Follow these steps to reconstruct the project:

1.  **Create a New Project:** 
    - Open **Tuanjie Hub**.
    - Create a new project using the **URP Sample** (Oasis) template.
    - Name it and wait for the editor to open.
2.  **Apply this Repository:**
    - Clone this repository into a separate folder.
    - Copy the contents of this repository into your new Tuanjie project, **overwriting** existing files when prompted.
    - Specifically, ensure `Assets/*.cs`, `Assets/Resources/`, and the `ProjectSettings/` folder are updated.
3.  **Open Scene:**
    - Open `Assets/Scenes/Oasis/OasisScene.scene`.
4.  **Manager GameObject:**
    - If the "Manager" GameObject does not exist in the Hierarchy:
      - *GameObject → Create Empty* → rename it **Manager**.
      - *Add Component → StartScreenUI*.

---

## Scene setup

The benchmark runs in the Oasis scene. `StartScreenUI` (on the Manager GO) handles the following at runtime:
- finding `PlayerManager` automatically.
- building the start-screen UI entirely in code.
- applying the selected frame rate cap and VRS mode.
- calling `PlayerManager.EnableFlythrough()` to bind the Cinemachine Brain to the Timeline.

---

## Build & deploy (OpenHarmony / HarmonyOS)

### Step 1 — Export from Tuanjie
1. **File → Build Settings** → select **OpenHarmony** as platform (switch if needed).
2. Export configuration:
    - *Scripting Backend*: IL2CPP.
    - *Target Architectures*: ARM64.
    - The current exported Harmony project resolves to `compatibleSdkVersion 6.0.0(20)` and `targetSdkVersion 6.0.1(21)`.
3. Click **Export Project** — Tuanjie generates an hvigor project folder (not a HAP directly).

### Step 2 — Build HAP in DevEco Studio
1. Open DevEco Studio → **Open** the exported hvigor project folder.
2. Let it sync Gradle/hvigor dependencies.
3. **Build → Build Hap(s)/App(s) → Build Debug Hap(s)** to produce the debug HAP.
4. If a Huawei device or supported OpenHarmony emulator is available, press **Run** to install and launch.
5. The `.hap` file is output to `build/default/outputs/default/`.

### Step 3 — Manual install (optional, if sharing HAP directly)
```bash
hdc install path/to/app.hap
hdc shell aa start -a TuanjiePlayerAbility -b com.com.freelance.OasisBenchmark
```

---

## Code structure

- `Assets/StartScreenUI.cs`: Main benchmark harness and VRS logic.
- `Assets/FlythroughController.cs`: FPS overlay and 60s speed schedule controller.

---

## Phase 1 status

- ✅ **VRS Implemented:** Uses Tuanjie's `GraphicsSettings.variableRateShadingMode` and `Renderer.shadingRate` API.
- ✅ **60s Schedule:** `FlythroughController.cs` drives the 35s Timeline over exactly 60s of wall-clock time.
- ✅ **FPS Overlay:** Live performance stats shown top-left during the run.
- ✅ **12-case Matrix Runner:** Start screen can now launch the SOW-required 12 combinations as separate runs with separate CSV files.

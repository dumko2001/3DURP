# Rendering Benchmark — Huawei Mobile (Phase 1)

Unity app for benchmarking rendering performance on a Huawei mobile device.  
Runs a fixed 60-second cinematic camera flythrough under selectable refresh-rate and VRS configurations and captures power draw.

> **Note:** This repository contains only the **custom logic and configuration**. The large environment assets must be reconstructed from the standard Tuanjie URP template.

---

## Prerequisites

| Requirement | Version |
|---|---|
| **Tuanjie Editor** (Huawei Unity fork) | **1.8.5** (2022.3.62t7) |
| Android SDK / NDK | installed via Unity Hub |
| Target device | Huawei — Android API ≥ 23 |
| USB debugging | enabled on device |

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

## Build & deploy

### Android build settings

1. **File → Build Settings** → switch platform to **Android**.
2. Player Settings:
   - *Minimum API Level*: 23.
   - *Scripting Backend*: IL2CPP.
   - *Target Architectures*: ARM64.
3. Connect the Huawei device via USB.
4. Click **Build and Run**.

---

## Code structure

- `Assets/StartScreenUI.cs`: Main benchmark harness and VRS logic.
- `Assets/FlythroughController.cs`: FPS overlay and 60s speed schedule controller.
- `Assets/CameraRecorder.cs`: Existing frame capture utility.

---

## Phase 1 status

- ✅ **VRS Implemented:** Uses Tuanjie's `GraphicsSettings.variableRateShadingMode` and `Renderer.shadingRate` API.
- ✅ **60s Schedule:** `FlythroughController.cs` drives the 35s Timeline over exactly 60s of wall-clock time.
- ✅ **FPS Overlay:** Live performance stats shown top-left during the run.

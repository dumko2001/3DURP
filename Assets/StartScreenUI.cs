using System.Collections;
using Cinemachine;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Playables;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;

// Attach to Manager GameObject only.
// No other scripts or drag-and-drop needed.
// Automatically finds PlayableDirector and disables FPS controller.
public class StartScreenUI : MonoBehaviour
{
    private enum BenchmarkRunMode
    {
        CinematicFlythrough,
        GameplayReplay,
    }

    // ── State ────────────────────────────────────────────────────────
    private int              selectedFPS    = -1;   // -1 = not chosen yet
    private int              selectedVRS    = -1;   // -1 = not chosen yet
    private GameObject       canvasGO;
    private PlayerManager     playerManager;
    private Text              statusText;        // shows current selection
    private Button            startButton;
    private Button            runMatrixButton;
    private Button            flythroughModeButton;
    private Button            replayModeButton;
    private Image             flythroughModeImage;
    private Image             replayModeImage;
    private bool              isMatrixRunning;
    private BenchmarkRunMode  selectedRunMode = BenchmarkRunMode.CinematicFlythrough;

    // Buttons arrays so we can highlight selected one
    private Image[] fpsBtnImages;
    private Image[] vrsBtnImages;
    private int     vrsRendererCount = 0;  // set by ApplyVRS, passed to overlay

    // ── Config options ───────────────────────────────────────────────
    private readonly int[]    fpsOptions  = { 120, 60, 40, 30 };
    private readonly int[]    requiredVrsModes = { 1, 2, 3 };
    private readonly string[] vrsLabels   = { "VRS Off", "1x2", "2x2", "4x4" };

    // ── Colors ───────────────────────────────────────────────────────
    private readonly Color COL_IDLE     = new Color(0.15f, 0.15f, 0.15f, 1f);
    private readonly Color COL_SELECTED = new Color(0.18f, 0.52f, 0.90f, 1f);
    private readonly Color COL_START    = new Color(0.10f, 0.70f, 0.40f, 1f);
    private readonly Color COL_DISABLED = new Color(0.25f, 0.25f, 0.25f, 0.5f);

    // ── Unity lifecycle ──────────────────────────────────────────────
    void Start()
    {
        // Find PlayerManager — keep it alive but disable its Update loop
        // so it doesn't start the idle-flythrough timer or grab the cursor.
        // We still need it to call EnableFlythrough() which binds the
        // CinemachineBrain to the Timeline track.
        playerManager = FindObjectOfType<PlayerManager>(true);
        if (playerManager != null)
        {
            playerManager.enabled = false;   // suspends Start/Update; public methods still callable
            // Stop the director if it was already playing
            if (playerManager.FlythroughDirector != null)
            {
                playerManager.FlythroughDirector.Stop();
                playerManager.FlythroughDirector.time = 0;
                Debug.Log($"Found flythrough: {playerManager.FlythroughDirector.playableAsset?.name}");
            }
        }
        else
        {
            Debug.LogWarning("PlayerManager not found — flythrough may not play correctly.");
        }

        // Free the cursor for UI interaction
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        EnsureEventSystem();
        BuildUI();
    }

    // ── EventSystem management ───────────────────────────────────────
    // The scene's UI_EventSystem may be inactive if PlayerManager.Start()
    // was skipped (because we disabled it). Find it (including inactive),
    // activate it, and disable any duplicates to avoid conflicts.
    void EnsureEventSystem()
    {
        // FindObjectsOfType(true) includes inactive GameObjects (Unity 2020.3+)
        var allES = FindObjectsOfType<EventSystem>(true);

        if (allES.Length == 0)
        {
            // No EventSystem in scene at all — create a minimal one.
            // Uses StandaloneInputModule as fallback; the new Input System
            // auto-upgrades it at runtime when the package is present.
            var esGO = new GameObject("EventSystem");
            esGO.AddComponent<EventSystem>();
            esGO.AddComponent<StandaloneInputModule>();
            Debug.Log("[UI] No EventSystem found — created a new one.");
            return;
        }

        // Prefer the scene's named UI_EventSystem; otherwise take the first.
        EventSystem chosen = null;
        foreach (var es in allES)
            if (chosen == null || es.gameObject.name == "UI_EventSystem")
                chosen = es;

        // Make sure it's active and enabled.
        chosen.gameObject.SetActive(true);
        chosen.enabled = true;

        // Disable any additional EventSystems to prevent input conflicts.
        foreach (var es in allES)
            if (es != chosen)
            {
                es.enabled = false;
                Debug.Log($"[UI] Disabled duplicate EventSystem: {es.gameObject.name}");
            }

        Debug.Log($"[UI] Using EventSystem: {chosen.gameObject.name}");
    }

    // ── UI construction ──────────────────────────────────────────────
    void BuildUI()
    {
        // Canvas
        canvasGO = new GameObject("StartCanvas");
        var canvas       = canvasGO.AddComponent<Canvas>();
        canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder  = 999;
        var scaler       = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode   = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGO.AddComponent<GraphicRaycaster>();

        // Semi-transparent background panel — centered
        var panel = MakePanel(canvasGO.transform,
                              new Color(0f, 0f, 0f, 0.82f),
                              Vector2.zero, new Vector2(760, 700));

        // Title
        MakeText(panel.transform, "Rendering Config", 38, FontStyle.Bold,
                 new Vector2(0, 255), new Vector2(720, 55));

        // ── Mode row ─────────────────────────────────────────────────
        MakeText(panel.transform, "RUN MODE", 15, FontStyle.Normal,
                 new Vector2(0, 190), new Vector2(720, 28));

        flythroughModeButton = MakeButton(panel.transform,
                              "CINEMATIC",
                              new Vector2(-150, 140),
                              new Vector2(230, 46),
                              () => SetRunMode(BenchmarkRunMode.CinematicFlythrough)).GetComponent<Button>();
        flythroughModeImage = flythroughModeButton.GetComponent<Image>();

        replayModeButton = MakeButton(panel.transform,
                          "GAMEPLAY REPLAY",
                          new Vector2(150, 140),
                          new Vector2(230, 46),
                          () => SetRunMode(BenchmarkRunMode.GameplayReplay)).GetComponent<Button>();
        replayModeImage = replayModeButton.GetComponent<Image>();

        // ── FPS row ──────────────────────────────────────────────────
        MakeText(panel.transform, "REFRESH RATE", 15, FontStyle.Normal,
                 new Vector2(0, 80), new Vector2(720, 28));

        fpsBtnImages = new Image[fpsOptions.Length];
        for (int i = 0; i < fpsOptions.Length; i++)
        {
            int fpsCopy = fpsOptions[i];   // closure-safe copy
            var btn = MakeButton(panel.transform,
                                  fpsCopy + " fps",
                                  new Vector2(-270 + i * 180, 30),
                                  new Vector2(160, 48),
                                  () => OnFPSSelected(fpsCopy));
            fpsBtnImages[i] = btn.GetComponent<Image>();
        }

        // ── VRS row ──────────────────────────────────────────────────
        MakeText(panel.transform, "SHADING RATE (VRS)", 15, FontStyle.Normal,
                 new Vector2(0, -35), new Vector2(720, 28));

        vrsBtnImages = new Image[vrsLabels.Length];
        for (int i = 0; i < vrsLabels.Length; i++)
        {
            int    modeCopy  = i;              // closure-safe copy
            string labelCopy = vrsLabels[i];
            var btn = MakeButton(panel.transform,
                                  labelCopy,
                                  new Vector2(-270 + i * 180, -85),
                                  new Vector2(160, 48),
                                  () => OnVRSSelected(modeCopy));
            vrsBtnImages[i] = btn.GetComponent<Image>();
        }

        // ── Status line — shows what's currently selected ─────────────
        var statusGO = new GameObject("Status");
        statusGO.transform.SetParent(panel.transform, false);
        statusText = statusGO.AddComponent<Text>();
        statusText.text      = "Select mode, refresh and VRS to start.";
        statusText.fontSize  = 16;
        statusText.color     = new Color(1f, 1f, 1f, 0.6f);
        statusText.alignment = TextAnchor.MiddleCenter;
        statusText.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var srt              = statusGO.GetComponent<RectTransform>();
        srt.anchoredPosition = new Vector2(0, -150);
        srt.sizeDelta        = new Vector2(720, 30);

        // ── START button — disabled until both are selected ───────────
        startButton = MakeButton(panel.transform, "START",
                     new Vector2(0, -225),
                     new Vector2(220, 58),
                     OnStartClicked,
                     isStart: true).GetComponent<Button>();

        runMatrixButton = MakeButton(panel.transform, "RUN ALL 12 REQUIRED",
                         new Vector2(0, -295),
                         new Vector2(320, 52),
                         OnAutoRunClicked).GetComponent<Button>();
        var matrixImage = runMatrixButton.GetComponent<Image>();
        matrixImage.color = new Color(0.80f, 0.44f, 0.16f, 1f);
        var matrixColors = runMatrixButton.colors;
        matrixColors.normalColor      = matrixImage.color;
        matrixColors.highlightedColor = new Color(0.92f, 0.55f, 0.23f, 1f);
        matrixColors.pressedColor     = new Color(0.48f, 0.24f, 0.08f, 1f);
        runMatrixButton.colors        = matrixColors;

        MakeText(panel.transform,
           "Cinematic uses the existing timeline path. Gameplay Replay uses gameplay_input_recording.bin. Matrix mode stays cinematic-only.",
             14, FontStyle.Normal,
           new Vector2(0, -355), new Vector2(700, 42));

       SetRunMode(selectedRunMode);
    }

    // ── Selection handlers ───────────────────────────────────────────
    void OnFPSSelected(int fps)
    {
        selectedFPS = fps;
        RefreshSelectionVisuals();
        UpdateStatus();
        Debug.Log($"FPS selected: {fps}");
    }

    void OnVRSSelected(int mode)
    {
        selectedVRS = mode;
        RefreshSelectionVisuals();
        UpdateStatus();
        Debug.Log($"VRS selected: {vrsLabels[mode]}");
    }

    void SetRunMode(BenchmarkRunMode mode)
    {
        selectedRunMode = mode;
        RefreshModeVisuals();
        UpdateStatus();
    }

    void UpdateStatus()
    {
        string modeStr = selectedRunMode == BenchmarkRunMode.CinematicFlythrough
            ? "Cinematic"
            : "Gameplay Replay";
        string fpsStr = selectedFPS  == -1 ? "?" : selectedFPS + "fps";
        string vrsStr = selectedVRS  == -1 ? "?" : vrsLabels[selectedVRS];
        statusText.text  = $"Mode: {modeStr}  |  {fpsStr}  |  {vrsStr}";
        statusText.color = new Color(1f, 1f, 1f,
                           (selectedFPS != -1 && selectedVRS != -1) ? 1f : 0.6f);
    }

    // ── Start handler ────────────────────────────────────────────────
    void OnStartClicked()
    {
        if (isMatrixRunning)
            return;

        // Guard — both must be selected
        if (selectedFPS == -1 || selectedVRS == -1)
        {
            statusText.text  = "Please select both a refresh rate and shading rate first!";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            return;
        }

        if (selectedRunMode == BenchmarkRunMode.GameplayReplay)
            StartReplayBenchmarkRun(selectedFPS, selectedVRS, BuildReplayCsvName(selectedFPS, selectedVRS));
        else
            StartBenchmarkRun(selectedFPS, selectedVRS, "benchmark_results.csv");
    }

    void OnAutoRunClicked()
    {
        if (isMatrixRunning)
            return;

        if (selectedRunMode != BenchmarkRunMode.CinematicFlythrough)
        {
            statusText.text  = "12-case matrix is only available in Cinematic mode.";
            statusText.color = new Color(1f, 0.82f, 0.40f, 1f);
            return;
        }

        StartCoroutine(RunRequiredMatrix());
    }

    IEnumerator RunRequiredMatrix()
    {
        isMatrixRunning = true;
        SetControlsInteractable(false);
        statusText.text  = "Running required 12-case matrix...";
        statusText.color = Color.white;

        int totalRuns = fpsOptions.Length * requiredVrsModes.Length;
        int completedRuns = 0;

        foreach (int fps in fpsOptions)
        {
            foreach (int vrsMode in requiredVrsModes)
            {
                selectedFPS = fps;
                selectedVRS = vrsMode;
                HighlightSelections();

                bool runComplete = false;
                string csvFileName = BuildMatrixCsvName(fps, vrsMode);
                statusText.text = $"Running {completedRuns + 1}/{totalRuns}: {fps}fps | {vrsLabels[vrsMode]}";

                if (!StartBenchmarkRun(fps, vrsMode, csvFileName, () => runComplete = true))
                {
                    isMatrixRunning = false;
                    SetControlsInteractable(true);
                    canvasGO.SetActive(true);
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible   = true;
                    yield break;
                }

                while (!runComplete)
                    yield return null;

                completedRuns++;
                canvasGO.SetActive(true);
                statusText.text = $"Completed {completedRuns}/{totalRuns}: {fps}fps | {vrsLabels[vrsMode]}";
                statusText.color = new Color(0.70f, 1f, 0.70f, 1f);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        isMatrixRunning = false;
        SetControlsInteractable(true);
        canvasGO.SetActive(true);
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;
        statusText.text  = "Required 12-case matrix complete. CSV files saved to persistentDataPath.";
        statusText.color = new Color(0.70f, 1f, 0.70f, 1f);
    }

    bool StartBenchmarkRun(int fps, int vrsMode, string csvFileName, System.Action onRunComplete = null)
    {
        // Apply frame rate cap
        QualitySettings.vSyncCount  = 0;
        Application.targetFrameRate = fps;

        // Apply VRS — captures renderer count for the overlay
        vrsRendererCount = ApplyVRS(vrsMode);

        if (playerManager == null)
        {
            statusText.text  = "PlayerManager missing — cannot start flythrough.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            Debug.LogError("[START] PlayerManager missing — cannot start flythrough!");
            return false;
        }

        // playerManager.enabled stays FALSE — public methods are still callable.
        var dir = playerManager.FlythroughDirector;
        if (dir == null)
        {
            statusText.text  = "FlythroughDirector missing on PlayerManager.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            Debug.LogError("[START] FlythroughDirector is not assigned on PlayerManager. " +
                           "Open the Manager GameObject in Inspector and assign the Cinematic Timeline director.");
            return false;
        }

        // Hide UI
        canvasGO.SetActive(false);

        // Lock cursor so accidental mouse movement cannot trigger PlayerManager's
        // NotifyPlayerMoved() → EnableFirstPersonController() → director.SetActive(false)
        // which would dispose the PlayableGraph mid-run.
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;

        // Trigger the flythrough via PlayerManager — this handles activating the
        // director GameObject, binding the CinemachineBrain to the Timeline track,
        // and calling Play(). Without the binding step the camera never moves.
        // IMPORTANT: keep PlayerManager DISABLED after the call. Re-enabling it
        // would restart its Update() loop which watches for mouse/touch and calls
        // EnableFirstPersonController(), stopping the flythrough.
        dir.extrapolationMode = DirectorWrapMode.None;  // stop cleanly at end
        dir.time = 0;
        playerManager.EnableFlythrough();

        // CRITICAL: clear m_InFlythrough via reflection immediately after EnableFlythrough().
        // Why: PlayerManager.Start() was skipped (component disabled before it ran), so
        // m_VirtualCamera is null. The InputSystem '<pointer>/press' action in
        // StarterAssetsInputs fires on ANY screen tap or mouse click, which calls
        // CameraManager.NotifyPlayerMoved(). That method calls EnableFirstPersonController()
        // only when m_InFlythrough==true, and that crashes on m_VirtualCamera being null.
        // Setting it false makes NotifyPlayerMoved() a safe no-op for the entire run.
        // The director keeps playing; FlythroughController manages it directly.
        var inFlyField = typeof(PlayerManager).GetField(
            "m_InFlythrough",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (inFlyField != null)
            inFlyField.SetValue(playerManager, false);
        else
            Debug.LogWarning("[START] Reflection: m_InFlythrough not found — accidental tap may break flythrough.");

        // Start the speed-schedule controller that shapes a 35 s timeline into
        // exactly 60 s: 10 s static + 50 s motion at varying speeds.
        // Also shows the live FPS overlay on screen.
        var controller = gameObject.GetComponent<FlythroughController>()
                         ?? gameObject.AddComponent<FlythroughController>();
        string configLabel = $"{fps}fps | {vrsLabels[vrsMode]}";
        controller.StartFlythrough(dir, configLabel, vrsRendererCount, fps, csvFileName, onRunComplete);

        Debug.Log($"[START] {fps}fps | VRS={vrsLabels[vrsMode]}");
        return true;
    }

    bool StartReplayBenchmarkRun(int fps, int vrsMode, string csvFileName, System.Action onRunComplete = null)
    {
        QualitySettings.vSyncCount  = 0;
        Application.targetFrameRate = fps;
        vrsRendererCount = ApplyVRS(vrsMode);

        var replayer = gameObject.GetComponent<InputReplayer>()
                       ?? gameObject.AddComponent<InputReplayer>();
        if (!replayer.Load())
        {
            statusText.text  = "Gameplay replay file not found — record an Oasis input path first.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            return false;
        }

        if (!PrepareGameplayReplayMode())
            return false;

        canvasGO.SetActive(false);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible   = false;

        var logger = gameObject.GetComponent<FlythroughController>()
                     ?? gameObject.AddComponent<FlythroughController>();
        string replayScene = string.IsNullOrWhiteSpace(replayer.RecordingSceneName)
            ? "Oasis"
            : replayer.RecordingSceneName;
        string configLabel = $"{fps}fps | {vrsLabels[vrsMode]} | Gameplay Replay";

        logger.StartMeasuredRun(
            configLabel,
            vrsRendererCount,
            replayer.Duration,
            fps,
            csvFileName,
            onRunStart: () =>
            {
                if (!replayer.StartReplay(() => logger.CompleteMeasuredRun()))
                    logger.CompleteMeasuredRun();
            },
            onRunComplete: onRunComplete,
            "Mode: GameplayReplay",
            $"Scenario: {replayScene}",
            $"ReplayFile: {replayer.RecordingFileName}",
            $"ReplayScenePath: {replayer.RecordingScenePath}",
            $"ReplayDurationSeconds: {replayer.Duration:F2}");

        Debug.Log($"[START] {fps}fps | VRS={vrsLabels[vrsMode]} | Replay={replayer.RecordingFileName}");
        return true;
    }

    bool PrepareGameplayReplayMode()
    {
        if (playerManager == null)
        {
            statusText.text  = "PlayerManager missing — cannot start gameplay replay.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            Debug.LogError("[START] PlayerManager missing — cannot start gameplay replay!");
            return false;
        }

        var virtualCameraField = typeof(PlayerManager).GetField(
            "m_VirtualCamera",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (virtualCameraField != null && virtualCameraField.GetValue(playerManager) == null)
        {
            var virtualCamera = playerManager.GetComponentInChildren<CinemachineVirtualCamera>(true);
            if (virtualCamera == null)
            {
                statusText.text  = "Cinemachine virtual camera missing — cannot start gameplay replay.";
                statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
                Debug.LogError("[START] CinemachineVirtualCamera missing — gameplay replay cannot switch to first-person mode.");
                return false;
            }

            virtualCameraField.SetValue(playerManager, virtualCamera);
        }

        playerManager.EnableFirstPersonController();
        playerManager.enabled = false;
        return true;
    }

    void HighlightSelections()
    {
        RefreshSelectionVisuals();
    }

    void SetControlsInteractable(bool interactable)
    {
        if (interactable)
        {
            RefreshSelectionVisuals();
            RefreshModeVisuals();
        }
        else
        {
            foreach (var image in fpsBtnImages)
                image.color = COL_DISABLED;

            foreach (var image in vrsBtnImages)
                image.color = COL_DISABLED;

            if (flythroughModeImage != null)
                flythroughModeImage.color = COL_DISABLED;
            if (replayModeImage != null)
                replayModeImage.color = COL_DISABLED;
        }

        if (startButton != null)
            startButton.interactable = interactable;
        if (runMatrixButton != null)
            runMatrixButton.interactable = interactable && selectedRunMode == BenchmarkRunMode.CinematicFlythrough;
        if (flythroughModeButton != null)
            flythroughModeButton.interactable = interactable;
        if (replayModeButton != null)
            replayModeButton.interactable = interactable;
    }

    void RefreshSelectionVisuals()
    {
        if (fpsBtnImages != null)
        {
            for (int i = 0; i < fpsBtnImages.Length; i++)
                fpsBtnImages[i].color = (fpsOptions[i] == selectedFPS) ? COL_SELECTED : COL_IDLE;
        }

        if (vrsBtnImages != null)
        {
            for (int i = 0; i < vrsBtnImages.Length; i++)
                vrsBtnImages[i].color = (i == selectedVRS) ? COL_SELECTED : COL_IDLE;
        }
    }

    void RefreshModeVisuals()
    {
        if (flythroughModeImage != null)
            flythroughModeImage.color = selectedRunMode == BenchmarkRunMode.CinematicFlythrough ? COL_SELECTED : COL_IDLE;

        if (replayModeImage != null)
            replayModeImage.color = selectedRunMode == BenchmarkRunMode.GameplayReplay ? COL_SELECTED : COL_IDLE;

        if (runMatrixButton != null)
            runMatrixButton.interactable = !isMatrixRunning && selectedRunMode == BenchmarkRunMode.CinematicFlythrough;
    }

    string BuildMatrixCsvName(int fps, int vrsMode)
    {
        return $"benchmark_results_{fps}fps_{vrsLabels[vrsMode].Replace(" ", string.Empty)}.csv";
    }

    string BuildReplayCsvName(int fps, int vrsMode)
    {
        return $"benchmark_results_replay_{fps}fps_{vrsLabels[vrsMode].Replace(" ", string.Empty)}.csv";
    }

    // Returns the number of scene renderers that received the VRS rate.
    // 0 = VRS Off or no renderers modified.
    int ApplyVRS(int mode)
    {
        // Always log hardware caps so it appears in adb logcat during testing.
        ShadingRateTypeCaps caps = SystemInfo.shadingRateTypeCaps;
        Debug.Log($"[VRS] Hardware caps: {caps}");

        if (mode == 0)
        {
            // VRS Off — restore full 1×1 shading rate on all renderers.
            GraphicsSettings.variableRateShadingMode = VariableRateShadingMode.Off;
            foreach (var r in FindObjectsOfType<Renderer>(true))
                if (IsSceneRenderer(r))
                    r.shadingRate = ShadingRateFragmentSize.Size1x1;
            Debug.Log("[VRS] Disabled — full 1×1 quality restored.");
            return 0;
        }

        // Require at least Pipeline or Primitive hardware support.
        if (caps == ShadingRateTypeCaps.None)
        {
            Debug.LogWarning("[VRS] Hardware does not support VRS on this device — skipped.");
            return 0;
        }

        // Map button index to fragment size.
        ShadingRateFragmentSize fragmentSize = mode switch
        {
            1 => ShadingRateFragmentSize.Size1x2,  // 1×2 — halve vertical resolution
            2 => ShadingRateFragmentSize.Size2x2,  // 2×2 — quarter shading resolution
            3 => ShadingRateFragmentSize.Size4x4,  // 4×4 — sixteenth shading resolution
            _ => ShadingRateFragmentSize.Size1x1,
        };

        // Custom mode: per-renderer shadingRate values are read by URP.
        GraphicsSettings.variableRateShadingMode = VariableRateShadingMode.Custom;

        // Apply to 3D scene renderers only — skip UI, particle systems on
        // CanvasRenderer-bearing GameObjects, and any renderer on a Canvas layer.
        int count = 0;
        foreach (var r in FindObjectsOfType<Renderer>(true))
        {
            if (!IsSceneRenderer(r)) continue;
            r.shadingRate = fragmentSize;
            count++;
        }

        Debug.Log($"[VRS] {vrsLabels[mode]} ({fragmentSize}) → {count} scene renderers. Caps={caps}");
        return count;
    }

    // Returns true for renderers that should receive a VRS rate.
    // Excludes anything attached to a Canvas hierarchy (UI) because:
    //   a) shading rate is meaningless for screen-space UI
    //   b) some Vulkan drivers produce artefacts if a UI pass uses a non-1x1 rate.
    static bool IsSceneRenderer(Renderer r)
    {
        // CanvasRenderer shares a GameObject path with UI renderers; any Renderer
        // whose transform root carries a Canvas component is a UI renderer.
        return r.GetComponentInParent<Canvas>() == null;
    }

    // ── UI helpers ───────────────────────────────────────────────────
    GameObject MakePanel(Transform parent, Color col, Vector2 pos, Vector2 size)
    {
        var go  = new GameObject("Panel");
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = col;
        var rt  = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
        return go;
    }

    void MakeText(Transform parent, string txt, int size, FontStyle style,
                  Vector2 pos, Vector2 sizeDelta)
    {
        var go = new GameObject(txt);
        go.transform.SetParent(parent, false);
        var t  = go.AddComponent<Text>();
        t.text      = txt;
        t.fontSize  = size;
        t.fontStyle = style;
        t.color     = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var rt = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = sizeDelta;
    }

    GameObject MakeButton(Transform parent, string label, Vector2 pos, Vector2 size,
                          UnityEngine.Events.UnityAction onClick,
                          bool isStart = false)
    {
        var go  = new GameObject(label);
        go.transform.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.color = isStart ? COL_START : COL_IDLE;
        var btn = go.AddComponent<Button>();
        btn.onClick.AddListener(onClick);

        // Hover tint via ColorBlock
        var cb          = btn.colors;
        cb.normalColor      = isStart ? COL_START : COL_IDLE;
        cb.highlightedColor = isStart
            ? new Color(0.12f, 0.85f, 0.50f)
            : new Color(0.30f, 0.30f, 0.30f);
        cb.pressedColor     = new Color(0.10f, 0.10f, 0.10f);
        cb.colorMultiplier  = 1f;
        btn.colors          = cb;

        var rt  = go.GetComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;

        // Label
        var tgo = new GameObject("Label");
        tgo.transform.SetParent(go.transform, false);
        var t   = tgo.AddComponent<Text>();
        t.text      = label;
        t.fontSize  = isStart ? 20 : 16;
        t.fontStyle = isStart ? FontStyle.Bold : FontStyle.Normal;
        t.color     = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var trt = tgo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = trt.offsetMax = Vector2.zero;

        return go;
    }
}
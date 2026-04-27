using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cinemachine;
using StarterAssets;
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
    private Button            recordReplayButton;
    private Button            runMatrixButton;
    private Button            flythroughModeButton;
    private Button            replayModeButton;
    private Button            replayPrevButton;
    private Button            replayNextButton;
    private Button            stopRecordingButton;
    private Image             flythroughModeImage;
    private Image             replayModeImage;
    private Text              replayFileText;
    private Text              recordingOverlayText;
    private bool              isMatrixRunning;
    private BenchmarkRunMode  selectedRunMode = BenchmarkRunMode.CinematicFlythrough;
    private StarterAssetsInputs gameplayInputs;
    private bool gameplayCursorLockedDefault = true;
    private bool gameplayCursorLookDefault   = true;
    private InputRecorder      gameplayRecorder;
    private bool               isRecordingGameplayPath;
    private readonly List<string> replayFilePaths = new();
    private int                selectedReplayIndex = -1;
    private GameObject         recordingOverlayGO;
    private GameObject         gameplayTouchControlsGO;
    private bool               gameplayStartPoseCaptured;
    private Vector3            gameplayStartPosition;
    private Quaternion         gameplayStartRotation;
    private float              gameplayStartPitch;

    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo PitchField = typeof(FirstPersonController).GetField("_cinemachineTargetPitch", PrivateInstance);
    private static readonly FieldInfo SpeedField = typeof(FirstPersonController).GetField("_speed", PrivateInstance);
    private static readonly FieldInfo RotationVelocityField = typeof(FirstPersonController).GetField("_rotationVelocity", PrivateInstance);
    private static readonly FieldInfo VerticalVelocityField = typeof(FirstPersonController).GetField("_verticalVelocity", PrivateInstance);
    private static readonly FieldInfo JumpTimeoutField = typeof(FirstPersonController).GetField("_jumpTimeoutDelta", PrivateInstance);
    private static readonly FieldInfo FallTimeoutField = typeof(FirstPersonController).GetField("_fallTimeoutDelta", PrivateInstance);

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

        gameplayInputs = GameplayInputResolver.FindBestInput();
        CacheGameplayInputDefaults(gameplayInputs);
        CaptureGameplayStartPose(gameplayInputs);

        EnsureEventSystem();
        SetMenuInteractionState(true);
        BuildUI();
        SetGameplayTouchControlsVisible(false);
    }

    void Update()
    {
        if (!isRecordingGameplayPath || gameplayRecorder == null)
            return;

        if (gameplayRecorder.IsRecording)
            return;

        isRecordingGameplayPath = false;
        canvasGO.SetActive(true);
        SetMenuInteractionState(true);
        ShowRecordingOverlay(false);
        SetGameplayTouchControlsVisible(false);
        RefreshReplayFiles(gameplayRecorder.LastSavedPath);
        string savedName = string.IsNullOrEmpty(gameplayRecorder.LastSavedPath)
            ? "new recording"
            : Path.GetFileName(gameplayRecorder.LastSavedPath);
        statusText.text  = $"Gameplay input recording saved: {savedName}. Select FPS + VRS, then press START.";
        statusText.color = new Color(0.70f, 1f, 0.70f, 1f);
        RefreshModeVisuals();
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

    void SetMenuInteractionState(bool menuVisible)
    {
        var allInputs = FindObjectsOfType<StarterAssetsInputs>(true);
        if (allInputs.Length > 0)
        {
            gameplayInputs = GameplayInputResolver.FindBestInput() ?? allInputs[0];
            CacheGameplayInputDefaults(gameplayInputs);
            foreach (var input in allInputs)
            {
                input.cursorLocked       = menuVisible ? false : gameplayCursorLockedDefault;
                input.cursorInputForLook = menuVisible ? false : gameplayCursorLookDefault;
                if (menuVisible)
                {
                    input.MoveInput(Vector2.zero);
                    input.LookInput(Vector2.zero);
                    input.JumpInput(false);
                    input.SprintInput(false);
                    input.CrouchInput(false);
                }
            }
        }

        Cursor.lockState = menuVisible ? CursorLockMode.None : CursorLockMode.Locked;
        Cursor.visible   = menuVisible;
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
                              Vector2.zero, new Vector2(760, 860));

        // Title
        MakeText(panel.transform, "Rendering Config", 38, FontStyle.Bold,
                 new Vector2(0, 305), new Vector2(720, 55));

        // ── Mode row ─────────────────────────────────────────────────
        MakeText(panel.transform, "RUN MODE", 15, FontStyle.Normal,
                 new Vector2(0, 240), new Vector2(720, 28));

        flythroughModeButton = MakeButton(panel.transform,
                              "CINEMATIC",
                              new Vector2(-150, 190),
                              new Vector2(230, 46),
                              () => SetRunMode(BenchmarkRunMode.CinematicFlythrough)).GetComponent<Button>();
        flythroughModeImage = flythroughModeButton.GetComponent<Image>();

        replayModeButton = MakeButton(panel.transform,
                          "GAMEPLAY REPLAY",
                          new Vector2(150, 190),
                          new Vector2(230, 46),
                          () => SetRunMode(BenchmarkRunMode.GameplayReplay)).GetComponent<Button>();
        replayModeImage = replayModeButton.GetComponent<Image>();

        // ── FPS row ──────────────────────────────────────────────────
        MakeText(panel.transform, "REFRESH RATE", 15, FontStyle.Normal,
                 new Vector2(0, 105), new Vector2(720, 28));

        fpsBtnImages = new Image[fpsOptions.Length];
        for (int i = 0; i < fpsOptions.Length; i++)
        {
            int fpsCopy = fpsOptions[i];   // closure-safe copy
            var btn = MakeButton(panel.transform,
                                  fpsCopy + " fps",
                                  new Vector2(-270 + i * 180, 55),
                                  new Vector2(160, 48),
                                  () => OnFPSSelected(fpsCopy));
            fpsBtnImages[i] = btn.GetComponent<Image>();
        }

        // ── VRS row ──────────────────────────────────────────────────
        MakeText(panel.transform, "SHADING RATE (VRS)", 15, FontStyle.Normal,
                 new Vector2(0, -15), new Vector2(720, 28));

        vrsBtnImages = new Image[vrsLabels.Length];
        for (int i = 0; i < vrsLabels.Length; i++)
        {
            int    modeCopy  = i;              // closure-safe copy
            string labelCopy = vrsLabels[i];
            var btn = MakeButton(panel.transform,
                                  labelCopy,
                                  new Vector2(-270 + i * 180, -65),
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
        srt.anchoredPosition = new Vector2(0, -125);
        srt.sizeDelta        = new Vector2(720, 30);

        MakeText(panel.transform, "REPLAY FILE", 15, FontStyle.Normal,
             new Vector2(0, -165), new Vector2(720, 28));

        replayPrevButton = MakeButton(panel.transform,
                  "<",
                  new Vector2(-265, -210),
                  new Vector2(60, 44),
                  () => StepReplaySelection(-1)).GetComponent<Button>();

        var replayValuePanel = MakePanel(panel.transform,
                         new Color(0.08f, 0.08f, 0.08f, 1f),
                         new Vector2(0, -210),
                         new Vector2(430, 44));
        replayFileText = MakeText(replayValuePanel.transform,
                      "No gameplay recordings found",
                      14,
                      FontStyle.Normal,
                      Vector2.zero,
                      new Vector2(410, 36));
        replayFileText.color = new Color(1f, 1f, 1f, 0.6f);

        replayNextButton = MakeButton(panel.transform,
                  ">",
                  new Vector2(265, -210),
                  new Vector2(60, 44),
                  () => StepReplaySelection(1)).GetComponent<Button>();

        // ── START button — disabled until both are selected ───────────
        startButton = MakeButton(panel.transform, "START",
                 new Vector2(0, -290),
                     new Vector2(220, 58),
                     OnStartClicked,
                     isStart: true).GetComponent<Button>();

        recordReplayButton = MakeButton(panel.transform, "RECORD GAMEPLAY PATH",
                new Vector2(240, -290),
                    new Vector2(250, 58),
                    OnRecordReplayClicked).GetComponent<Button>();
        var recordImage = recordReplayButton.GetComponent<Image>();
        recordImage.color = new Color(0.66f, 0.22f, 0.16f, 1f);
        var recordColors = recordReplayButton.colors;
        recordColors.normalColor      = recordImage.color;
        recordColors.highlightedColor = new Color(0.82f, 0.30f, 0.22f, 1f);
        recordColors.pressedColor     = new Color(0.40f, 0.12f, 0.08f, 1f);
        recordReplayButton.colors     = recordColors;

        runMatrixButton = MakeButton(panel.transform, "RUN ALL 12 REQUIRED",
                         new Vector2(0, -360),
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
                                                      "Cinematic uses the timeline path. Gameplay Replay reuses the selected .bin recording. Use < and > to choose a recording. RECORD saves a new timestamped file. During record: R toggles stop/save, Esc force-stops, Space jumps, C crouches.",
             14, FontStyle.Normal,
              new Vector2(0, -420), new Vector2(700, 52));

          RefreshReplayFiles();
        BuildRecordingOverlay();
          SetRunMode(selectedRunMode);
    }

    void BuildRecordingOverlay()
    {
        recordingOverlayGO = new GameObject("RecordingOverlayCanvas");
        var canvas = recordingOverlayGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1001;

        var scaler = recordingOverlayGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        recordingOverlayGO.AddComponent<GraphicRaycaster>();

        var panel = MakePanel(recordingOverlayGO.transform,
                              new Color(0f, 0f, 0f, 0.78f),
                              new Vector2(0f, -24f),
                              new Vector2(620f, 120f));
        var panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 1f);
        panelRect.anchorMax = new Vector2(0.5f, 1f);
        panelRect.pivot = new Vector2(0.5f, 1f);

        recordingOverlayText = MakeText(panel.transform,
                                        "Recording gameplay path. Use touch controls to move, look, jump and sprint. Press STOP & SAVE when done.",
                                        16,
                                        FontStyle.Normal,
                                        new Vector2(0f, -28f),
                                        new Vector2(560f, 44f));

        stopRecordingButton = MakeButton(panel.transform,
                                         "STOP & SAVE",
                                         new Vector2(0f, -72f),
                                         new Vector2(220f, 44f),
                                         OnStopRecordingClicked,
                                         isStart: true).GetComponent<Button>();

        recordingOverlayGO.SetActive(false);
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
        if (selectedRunMode == BenchmarkRunMode.GameplayReplay)
            RefreshReplayFiles();
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
        if (selectedRunMode == BenchmarkRunMode.GameplayReplay)
        {
            string replayLabel = ShortenReplayLabel(GetSelectedReplayFileName());
            statusText.text = $"Mode: {modeStr}  |  {fpsStr}  |  {vrsStr}  |  Replay: {replayLabel}";
        }
        else
        {
            statusText.text = $"Mode: {modeStr}  |  {fpsStr}  |  {vrsStr}";
        }

        bool readyToStart = selectedFPS != -1 && selectedVRS != -1;
        if (selectedRunMode == BenchmarkRunMode.GameplayReplay)
            readyToStart &= HasSelectedReplay();

        statusText.color = new Color(1f, 1f, 1f, readyToStart ? 1f : 0.6f);
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
            StartReplayBenchmarkRun(selectedFPS, selectedVRS, BuildReplayCsvName(selectedFPS, selectedVRS), OnSingleRunComplete);
        else
            StartBenchmarkRun(selectedFPS, selectedVRS, "benchmark_results.csv", OnSingleRunComplete);
    }

    void OnRecordReplayClicked()
    {
        if (isMatrixRunning)
            return;

        if (selectedRunMode != BenchmarkRunMode.GameplayReplay)
        {
            statusText.text  = "Switch RUN MODE to Gameplay Replay, then press RECORD.";
            statusText.color = new Color(1f, 0.82f, 0.40f, 1f);
            return;
        }

        if (isRecordingGameplayPath)
        {
            statusText.text  = "Recording in progress. Press R to stop (toggle), or Esc to force-stop.";
            statusText.color = new Color(1f, 1f, 1f, 0.9f);
            return;
        }

        if (!TryStartGameplayRecordingSession())
        {
            statusText.text  = "Could not start recording. Check PlayerManager/camera setup in Oasis.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            return;
        }

        statusText.text  = "Recording gameplay path. Press R again to stop and save, or Esc to force-stop.";
        statusText.color = new Color(1f, 1f, 1f, 0.9f);
    }

    void OnStopRecordingClicked()
    {
        if (!isRecordingGameplayPath || gameplayRecorder == null || !gameplayRecorder.IsRecording)
            return;

        gameplayRecorder.StopRecording();
    }

    void StepReplaySelection(int direction)
    {
        if (replayFilePaths.Count == 0)
            return;

        selectedReplayIndex = (selectedReplayIndex + direction + replayFilePaths.Count) % replayFilePaths.Count;
        RefreshReplayPicker();
        UpdateStatus();
    }

    void OnSingleRunComplete()
    {
        canvasGO.SetActive(true);
        SetMenuInteractionState(true);
        ShowRecordingOverlay(false);
        SetGameplayTouchControlsVisible(false);

        string modeLabel = selectedRunMode == BenchmarkRunMode.GameplayReplay
            ? "Gameplay replay"
            : "Cinematic run";
        statusText.text  = $"{modeLabel} complete. Output saved to persistentDataPath.";
        statusText.color = new Color(0.70f, 1f, 0.70f, 1f);
        RefreshSelectionVisuals();
        RefreshModeVisuals();
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
                    SetMenuInteractionState(true);
                    yield break;
                }

                while (!runComplete)
                    yield return null;

                completedRuns++;
                canvasGO.SetActive(true);
                SetMenuInteractionState(true);
                statusText.text = $"Completed {completedRuns}/{totalRuns}: {fps}fps | {vrsLabels[vrsMode]}";
                statusText.color = new Color(0.70f, 1f, 0.70f, 1f);
                yield return new WaitForSecondsRealtime(0.5f);
            }
        }

        isMatrixRunning = false;
        SetControlsInteractable(true);
        canvasGO.SetActive(true);
        SetMenuInteractionState(true);
        statusText.text  = "Required 12-case matrix complete. CSV files saved to persistentDataPath.";
        statusText.color = new Color(0.70f, 1f, 0.70f, 1f);
    }

    bool StartBenchmarkRun(int fps, int vrsMode, string csvFileName, System.Action onRunComplete = null)
    {
        // Apply frame rate cap
        QualitySettings.vSyncCount  = 0;
        Application.targetFrameRate = fps;

        // Spawn a red 'Proof Cube' to provide visual confirmation of VRS artifacts
        SpawnVRSProofCube();

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
        SetMenuInteractionState(false);
        ShowRecordingOverlay(false);
        SetGameplayTouchControlsVisible(false);

        // Lock cursor so accidental mouse movement cannot trigger PlayerManager's
        // NotifyPlayerMoved() → EnableFirstPersonController() → director.SetActive(false)
        // which would dispose the PlayableGraph mid-run.

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
        SpawnVRSProofCube();
        vrsRendererCount = ApplyVRS(vrsMode);

        string replayPath = GetSelectedReplayPath();
        if (string.IsNullOrEmpty(replayPath))
        {
            RefreshReplayFiles();
            statusText.text  = "No gameplay recording selected. Press RECORD first, then choose the saved file.";
            statusText.color = new Color(1f, 0.82f, 0.40f, 1f);
            return false;
        }

        var replayer = gameObject.GetComponent<InputReplayer>()
                       ?? gameObject.AddComponent<InputReplayer>();
        if (!replayer.Load(replayPath))
        {
            RefreshReplayFiles();
            statusText.text  = "Selected gameplay replay could not be loaded. Re-record or choose another file.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            return false;
        }

        if (!PrepareGameplayReplayMode())
            return false;

        if (!BindGameplayReplayTargets(replayer))
            return false;

        canvasGO.SetActive(false);
        SetMenuInteractionState(false);
        ShowRecordingOverlay(false);
        SetGameplayTouchControlsVisible(false);

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

    bool TryStartGameplayRecordingSession()
    {
        if (isRecordingGameplayPath)
            return true;

        gameplayRecorder = gameObject.GetComponent<InputRecorder>()
                         ?? gameObject.AddComponent<InputRecorder>();
        gameplayRecorder.autoStartOnPlay = false;
        gameplayRecorder.toggleKey = KeyCode.R;
        gameplayRecorder.useTimestampedFileNames = true;

        if (!PrepareGameplayReplayMode())
            return false;

        if (!BindGameplayRecorderTargets(gameplayRecorder))
            return false;

        RestoreGameplayStartPose();

        canvasGO.SetActive(false);
        SetMenuInteractionState(false);
        ShowRecordingOverlay(true);
        SetGameplayTouchControlsVisible(true);
        gameplayRecorder.StartRecording();

        if (!gameplayRecorder.IsRecording)
        {
            ShowRecordingOverlay(false);
            SetGameplayTouchControlsVisible(false);
            return false;
        }

        isRecordingGameplayPath = true;
    Debug.Log("[START] Recording gameplay input to a new timestamped replay file.");
        Debug.Log("[START] Play normally. Press R again to stop and save, or Esc to force-stop. The benchmark menu will return automatically.");

        if (statusText != null)
        {
            statusText.text  = "Recording gameplay path. Press R again to stop and save, or Esc to force-stop.";
            statusText.color = new Color(1f, 1f, 1f, 0.9f);
        }

        if (recordingOverlayText != null)
            recordingOverlayText.text = "Recording gameplay path. Use touch controls to move, look, jump and sprint. Press STOP & SAVE when done.";
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

        gameplayInputs = GameplayInputResolver.FindBestInput();
        CacheGameplayInputDefaults(gameplayInputs);
        CaptureGameplayStartPose(gameplayInputs);
        return true;
    }

    bool BindGameplayRecorderTargets(InputRecorder recorder)
    {
        if (!TryResolveGameplayRuntime(out var input, out var controller))
            return false;

        recorder.inputSource = input;
        recorder.playerRoot = input.transform;
        recorder.controller = controller;
        return true;
    }

    bool BindGameplayReplayTargets(InputReplayer replayer)
    {
        if (!TryResolveGameplayRuntime(out var input, out var controller))
            return false;

        replayer.targetInput = input;
        replayer.playerRoot = input.transform;
        replayer.controller = controller;
        return true;
    }

    bool TryResolveGameplayRuntime(out StarterAssetsInputs input, out FirstPersonController controller)
    {
        input = GameplayInputResolver.FindBestInput();
        controller = input != null ? input.GetComponent<FirstPersonController>() : null;

        if (input == null || controller == null)
        {
            statusText.text  = "Gameplay controller not found — cannot start replay or recording.";
            statusText.color = new Color(1f, 0.4f, 0.4f, 1f);
            Debug.LogError("[START] Active gameplay controller not found.");
            return false;
        }

        gameplayInputs = input;
        CacheGameplayInputDefaults(gameplayInputs);
        CaptureGameplayStartPose(gameplayInputs);
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
        if (recordReplayButton != null)
            recordReplayButton.interactable = interactable && selectedRunMode == BenchmarkRunMode.GameplayReplay;
        if (runMatrixButton != null)
            runMatrixButton.interactable = interactable && selectedRunMode == BenchmarkRunMode.CinematicFlythrough;
        if (flythroughModeButton != null)
            flythroughModeButton.interactable = interactable;
        if (replayModeButton != null)
            replayModeButton.interactable = interactable;

        RefreshReplayPicker();
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

        if (recordReplayButton != null)
            recordReplayButton.interactable = !isMatrixRunning && selectedRunMode == BenchmarkRunMode.GameplayReplay;

        RefreshReplayPicker();
    }

    string BuildMatrixCsvName(int fps, int vrsMode)
    {
        return $"benchmark_results_{fps}fps_{vrsLabels[vrsMode].Replace(" ", string.Empty)}.csv";
    }

    string BuildReplayCsvName(int fps, int vrsMode)
    {
        string replayLabel = SanitizeFileSegment(Path.GetFileNameWithoutExtension(GetSelectedReplayFileName()));
        if (string.IsNullOrEmpty(replayLabel))
            replayLabel = "replay";

        return $"benchmark_results_replay_{replayLabel}_{fps}fps_{vrsLabels[vrsMode].Replace(" ", string.Empty)}.csv";
    }

    void RefreshReplayFiles(string preferredPath = null)
    {
        string selectedPath = string.IsNullOrEmpty(preferredPath)
            ? GetSelectedReplayPath()
            : preferredPath;

        replayFilePaths.Clear();
        if (Directory.Exists(Application.persistentDataPath))
        {
            foreach (var path in Directory.GetFiles(Application.persistentDataPath, "gameplay_input*.bin"))
                replayFilePaths.Add(path);

            replayFilePaths.Sort((left, right) =>
                File.GetLastWriteTimeUtc(right).CompareTo(File.GetLastWriteTimeUtc(left)));
        }

        if (replayFilePaths.Count == 0)
        {
            selectedReplayIndex = -1;
        }
        else if (!string.IsNullOrEmpty(selectedPath))
        {
            selectedReplayIndex = replayFilePaths.FindIndex(path => string.Equals(path, selectedPath));
            if (selectedReplayIndex < 0)
                selectedReplayIndex = 0;
        }
        else if (selectedReplayIndex < 0 || selectedReplayIndex >= replayFilePaths.Count)
        {
            selectedReplayIndex = 0;
        }

        RefreshReplayPicker();
    }

    void RefreshReplayPicker()
    {
        if (replayFileText != null)
        {
            bool hasReplay = HasSelectedReplay();
            replayFileText.text = hasReplay
                ? BuildReplayPickerLabel(selectedReplayIndex, replayFilePaths.Count, Path.GetFileName(replayFilePaths[selectedReplayIndex]))
                : "No gameplay recordings found in persistentDataPath";
            replayFileText.color = hasReplay ? Color.white : new Color(1f, 1f, 1f, 0.6f);
        }

        bool canSelect = !isMatrixRunning && selectedRunMode == BenchmarkRunMode.GameplayReplay;
        if (replayPrevButton != null)
            replayPrevButton.interactable = canSelect && replayFilePaths.Count > 1;
        if (replayNextButton != null)
            replayNextButton.interactable = canSelect && replayFilePaths.Count > 1;
    }

    void ShowRecordingOverlay(bool visible)
    {
        if (recordingOverlayGO != null)
            recordingOverlayGO.SetActive(visible);
    }

    void SetGameplayTouchControlsVisible(bool visible)
    {
        gameplayTouchControlsGO ??= ResolveGameplayTouchControlsRoot();
        if (gameplayTouchControlsGO != null)
            gameplayTouchControlsGO.SetActive(visible);
    }

    GameObject ResolveGameplayTouchControlsRoot()
    {
        var uiCanvases = FindObjectsOfType<UICanvasControllerInput>(true);
        if (uiCanvases.Length == 0)
            return null;

        foreach (var uiCanvas in uiCanvases)
        {
            if (gameplayInputs != null && uiCanvas.starterAssetsInputs == gameplayInputs)
                return uiCanvas.gameObject;
        }

        return uiCanvases[0].gameObject;
    }

    bool HasSelectedReplay()
    {
        return selectedReplayIndex >= 0 && selectedReplayIndex < replayFilePaths.Count;
    }

    string GetSelectedReplayPath()
    {
        return HasSelectedReplay() ? replayFilePaths[selectedReplayIndex] : null;
    }

    string GetSelectedReplayFileName()
    {
        return HasSelectedReplay() ? Path.GetFileName(replayFilePaths[selectedReplayIndex]) : "None";
    }

    void CacheGameplayInputDefaults(StarterAssetsInputs input)
    {
        if (input == null)
            return;

        gameplayCursorLockedDefault = input.cursorLocked;
        gameplayCursorLookDefault = input.cursorInputForLook;
    }

    void CaptureGameplayStartPose(StarterAssetsInputs input)
    {
        if (gameplayStartPoseCaptured || input == null)
            return;

        var controller = input.GetComponent<FirstPersonController>();
        gameplayStartPosition = input.transform.position;
        gameplayStartRotation = input.transform.rotation;
        gameplayStartPitch = CaptureCameraPitch(controller);
        gameplayStartPoseCaptured = true;
    }

    void RestoreGameplayStartPose()
    {
        if (!gameplayStartPoseCaptured || gameplayInputs == null)
            return;

        var controller = gameplayInputs.GetComponent<FirstPersonController>();
        var playerRoot = gameplayInputs.transform;
        var characterController = controller != null ? controller.GetComponent<CharacterController>() : null;
        bool reenableController = characterController != null && characterController.enabled;

        if (reenableController)
            characterController.enabled = false;

        playerRoot.SetPositionAndRotation(gameplayStartPosition, gameplayStartRotation);

        if (controller != null)
        {
            if (controller.CinemachineCameraTarget != null)
                controller.CinemachineCameraTarget.transform.localRotation = Quaternion.Euler(gameplayStartPitch, 0f, 0f);

            SetField(PitchField, controller, gameplayStartPitch);
            SetField(SpeedField, controller, 0f);
            SetField(RotationVelocityField, controller, 0f);
            SetField(VerticalVelocityField, controller, -2f);
            SetField(JumpTimeoutField, controller, controller.JumpTimeout);
            SetField(FallTimeoutField, controller, controller.FallTimeout);
        }

        if (reenableController)
            characterController.enabled = true;

        gameplayInputs.MoveInput(Vector2.zero);
        gameplayInputs.LookInput(Vector2.zero);
        gameplayInputs.JumpInput(false);
        gameplayInputs.SprintInput(false);
        gameplayInputs.CrouchInput(false);
    }

    static float CaptureCameraPitch(FirstPersonController controller)
    {
        if (controller == null || controller.CinemachineCameraTarget == null)
            return 0f;

        float angle = controller.CinemachineCameraTarget.transform.localEulerAngles.x;
        return angle > 180f ? angle - 360f : angle;
    }

    static void SetField(FieldInfo field, object target, object value)
    {
        field?.SetValue(target, value);
    }

    private void SpawnVRSProofCube()
    {
        // Clean up old proof cubes if any
        var oldCube = GameObject.Find("VRS_Proof_Cube");
        if (oldCube != null) Destroy(oldCube);

        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "VRS_Proof_Cube_GREEN";
        
        // Remove collider so it doesn't interfere with physics or movement
        var col = cube.GetComponent<Collider>();
        if (col != null) Destroy(col);

        // Attach to Main Camera so it's always in view during flythrough/gameplay
        var cam = Camera.main;
        if (cam != null)
        {
            cube.transform.SetParent(cam.transform, false);
            cube.transform.localPosition = new Vector3(0.8f, 0.5f, 2.5f); // Offset to the top-right corner
            cube.transform.localRotation = Quaternion.identity;
            cube.transform.localScale = Vector3.one * 0.3f; // Smaller 'HUD' style cube
        }
        else
        {
            // Fallback to world origin if no camera found
            cube.transform.position = new Vector3(0f, 2.5f, 5f); 
            cube.transform.localScale = Vector3.one * 1.5f;
        }

        var r = cube.GetComponent<Renderer>();
        if (r != null)
        {
            // Use the URP/Lit shader instead of the built-in 'Standard' shader which fails in URP/Vulkan
            var urpLit = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLit != null)
            {
                r.material = new Material(urpLit);
            }
            
            r.material.color = Color.green;
        }
        
        Debug.Log("[VRS] Spawned Proof Cube attached to Camera to verify shading artifacts visually.");
    }

    static string BuildReplayPickerLabel(int index, int total, string fileName)
    {
        return total <= 1 ? fileName : $"{index + 1}/{total}  {fileName}";
    }

    static string ShortenReplayLabel(string fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName == "None")
            return "None";

        return fileName.Length <= 36 ? fileName : fileName.Substring(0, 33) + "...";
    }

    static string SanitizeFileSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value.Replace(' ', '_');
    }

    private Coroutine _vrsCoroutine;

    // Returns the number of scene renderers that received the VRS rate immediately.
    int ApplyVRS(int mode)
    {
        // DEEP DIAGNOSTIC: Log the raw truth about the GPU driver state
        string api = SystemInfo.graphicsDeviceVersion;
        string gpu = SystemInfo.graphicsDeviceName;
        var caps = SystemInfo.shadingRateTypeCaps;
        var apiType = SystemInfo.graphicsDeviceType;
        
        Debug.Log($"[VRS_DIAGNOSTIC] Device: {gpu}");
        Debug.Log($"[VRS_DIAGNOSTIC] API (Full): {api}");
        Debug.Log($"[VRS_DIAGNOSTIC] API (Type): {apiType}");
        Debug.Log($"[VRS_DIAGNOSTIC] Caps: {caps}");

        // First-Principles Check: VRS only works on Vulkan for this platform.
        if (apiType == UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3)
        {
            Debug.LogError("[VRS_FATAL] Engine fallback to OpenGL ES 3 detected. VRS is hardware-disabled in this mode. You must use Vulkan.");
        }

        // Always log hardware caps so it appears in adb logcat during testing.
        Debug.Log($"[VRS] Hardware caps check: {caps}");

        if (mode == 0)
        {
            GraphicsSettings.variableRateShadingMode = VariableRateShadingMode.Off;
            int count = ApplyVRSInternal(ShadingRateFragmentSize.Size1x1);
            if (_vrsCoroutine != null) StopCoroutine(_vrsCoroutine);
            Debug.Log("[VRS] Disabled — full 1×1 quality restored.");
            return 0;
        }

        // We continue even if caps == None. 
        // FIRST PRINCIPLES: If the detection logic is buggy on this new GPU, 
        // we should 'Brute Force' the command anyway. If the hardware can do it, 
        // it will do it, even if Unity's detection says 'None'.
        if (caps == ShadingRateTypeCaps.None)
        {
            Debug.LogWarning("[VRS_BRUTE_FORCE] Hardware detection returned 'None', but we are pushing the shading rate commands to the GPU anyway.");
        }

        ShadingRateFragmentSize fragmentSize = mode switch
        {
            1 => ShadingRateFragmentSize.Size1x2,
            2 => ShadingRateFragmentSize.Size2x2,
            3 => ShadingRateFragmentSize.Size4x4,
            _ => ShadingRateFragmentSize.Size1x1,
        };

        GraphicsSettings.variableRateShadingMode = VariableRateShadingMode.Custom;

        // Apply immediately for anything already loaded
        int initialCount = ApplyVRSInternal(fragmentSize);
        Debug.Log($"[VRS] {vrsLabels[mode]} ({fragmentSize}) → {initialCount} scene renderers initially. Caps={caps}");

        // Start routine to catch late-spawning or additively loaded objects
        if (_vrsCoroutine != null) StopCoroutine(_vrsCoroutine);
        _vrsCoroutine = StartCoroutine(ContinuousVRSApplyRoutine(fragmentSize));

        return initialCount;
    }

    System.Collections.IEnumerator ContinuousVRSApplyRoutine(ShadingRateFragmentSize fragmentSize)
    {
        // Cache the wait to generate ZERO garbage collection spikes during the benchmark
        var wait = new WaitForSeconds(2.0f);

        // Poll continuously every 2 seconds to catch anything loaded dynamically during Gameplay or Replays
        while (true)
        {
            yield return wait;
            int newCount = ApplyVRSInternal(fragmentSize);
            
            // If new renderers were spawned/loaded, update the UI tracker variable
            if (newCount > vrsRendererCount)
            {
                vrsRendererCount = newCount;
                Debug.Log($"[VRS] Late Spawns Caught! Updated active VRS renderers to: {vrsRendererCount}");
            }
        }
    }

    // Extracted logic to apply the rate and count valid renderers
    int ApplyVRSInternal(ShadingRateFragmentSize fragmentSize)
    {
        int count = 0;
        foreach (var r in FindObjectsOfType<Renderer>(true))
        {
            if (!IsSceneRenderer(r)) continue;
            r.shadingRate = fragmentSize;
            count++;
        }
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

    Text MakeText(Transform parent, string txt, int size, FontStyle style,
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
        return t;
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
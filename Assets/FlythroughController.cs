using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Shared benchmark runtime for both cinematic flythrough runs and gameplay replay runs.
/// It provides:
///
/// 1. SPEED SCHEDULE — drives the PlayableDirector at varying speeds so that
///    35 s of Timeline content fills exactly 60 s of wall-clock time:
///
///    Phase         Real time   Speed   Content consumed
///    ──────────────────────────────────────────────────
///    Static        0 – 10 s    0.00      0.0 s  (camera holds, no GPU load variation)
///    Slow          10 – 20 s   0.50      5.0 s  (leisurely environment push)
///    Fast          20 – 28 s   1.50     12.0 s  (dynamic camera rush)
///    Medium-slow   28 – 38 s   0.60      6.0 s  (mid-course scenic pan)
///    Very-fast     38 – 45 s   1.20      8.4 s  (aggressive acceleration)
///    Slow finale   45 – 60 s   0.24      3.6 s  (lingering end shot)
///                                        ─────
///                                        35.0 s total content  ✓
///
/// 2. FPS OVERLAY — a lightweight on-screen counter (top-left corner) that
///    shows measured frame rate, elapsed time, and the active config so the
///    device screen can be photographed/recorded without any external tool.
///
/// StartScreenUI auto-adds this component; no editor wiring needed.
/// </summary>
public class FlythroughController : MonoBehaviour
{
    // ── Speed schedule ───────────────────────────────────────────────────────
    // (real-time seconds for this phase, playable speed multiplier)
    private static readonly (float realDur, float speed)[] Phases =
    {
        (10f, 0.000f),   // static     —  0.0 s content
        (10f, 0.500f),   // slow       —  5.0 s content
        ( 8f, 1.500f),   // fast       — 12.0 s content
        (10f, 0.600f),   // med-slow   —  6.0 s content
        ( 7f, 1.200f),   // very-fast  —  8.4 s content
        (15f, 0.240f),   // finale     —  3.6 s content  (total = 35 s)
    };

    // ── Timing ───────────────────────────────────────────────────────────────
    private const float TOTAL_REAL_DURATION = 60f;   // SOW: exactly 60 s wall-clock
    private const float FPS_SAMPLE_WINDOW   = 0.5f;  // rolling FPS window
    private const int   OVERLAY_FONT_SIZE   = 26;

    // ── Runtime state ────────────────────────────────────────────────────────
    private PlayableDirector _director;
    private string           _configLabel      = "";
    private int              _vrsRendererCount = 0;
    private int              _targetFps        = 60;  // used for throttle detection
    private bool             _running          = false;

    // FPS tracking
    private float _fpsAccum;
    private int   _fpsFrameCount;
    private float _fpsTimer;
    private float _measuredFPS;
    private float _elapsedRealTime;

    // Current phase info (updated by DriveSpeed, read by Update for CSV)
    private int   _currentPhase = 0;
    private float _currentSpeed = 0f;

    // CSV data logger
    private StringBuilder _csv;
    private float         _csvTimer;
    private const float   CSV_INTERVAL = 0.1f;  // 100 ms rows = 600 rows over 60 s

    // UI — two lines
    private Text  _line1;   // measured fps | elapsed time | config
    private Text  _line2;   // VRS hardware caps + active mode (read live from engine)
    private Text  _line3;   // Deep Diagnostic: GPU Name | API Version
    private GameObject _overlayCanvas;
    private Action _runCompleteCallback;
    private string _csvFileName = "benchmark_results.csv";
    private string[] _csvMetadataLines = Array.Empty<string>();
    private float _runDurationSeconds = TOTAL_REAL_DURATION;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Call immediately after PlayerManager.EnableFlythrough().
    /// vrsRendererCount: number of scene renderers that received a VRS rate (0 = VRS Off).
    /// targetFps: the Application.targetFrameRate that was applied — used for throttle detection.
    /// </summary>
    public void StartFlythrough(PlayableDirector director, string configLabel, int vrsRendererCount,
                                int targetFps = 60, string csvFileName = "benchmark_results.csv",
                                Action onRunComplete = null)
    {
        StopAllCoroutines();

        _director          = director;
        _csvMetadataLines  = Array.Empty<string>();
        _runDurationSeconds = TOTAL_REAL_DURATION;
        _currentPhase    = 0;
        _currentSpeed    = 0f;
        BeginMeasuredRun(configLabel, vrsRendererCount, targetFps, csvFileName, onRunComplete);

        // Set speed=0 right now — Play() has already built the graph synchronously
        // before this method is called, so root is accessible immediately.
        // Without this, the timeline advances at speed 1.0 for the first frame.
        if (_director.playableGraph.IsValid())
            _director.playableGraph.GetRootPlayable(0).SetSpeed(0.0);
        else
            Debug.LogWarning("[Flythrough] Graph not valid at start — first frame may use speed 1.");

        BuildOverlay();
        StartCoroutine(DriveSpeed());
        StartCoroutine(HardStop());
    }

    public void StartMeasuredRun(string configLabel, int vrsRendererCount, float runDurationSeconds,
                                 int targetFps = 60, string csvFileName = "benchmark_results.csv",
                                 Action onRunStart = null, Action onRunComplete = null,
                                 params string[] metadataLines)
    {
        StopAllCoroutines();

        _director = null;
        _csvMetadataLines = metadataLines ?? Array.Empty<string>();
        _runDurationSeconds = Mathf.Max(0.5f, runDurationSeconds);
        _currentPhase = -1;
        _currentSpeed = 0f;
        BeginMeasuredRun(configLabel, vrsRendererCount, targetFps, csvFileName, onRunComplete);

        BuildOverlay();
        StartCoroutine(HardStop());
        onRunStart?.Invoke();
    }

    public void CompleteMeasuredRun()
    {
        if (!_running)
            return;

        StopAllCoroutines();
        if (_director != null && _director.state == PlayState.Playing)
            _director.Stop();

        FinishRun();
    }

    private void BeginMeasuredRun(string configLabel, int vrsRendererCount, int targetFps,
                                  string csvFileName, Action onRunComplete)
    {
        _configLabel = configLabel;
        _vrsRendererCount = vrsRendererCount;
        _targetFps = targetFps;
        _csvFileName = string.IsNullOrWhiteSpace(csvFileName) ? "benchmark_results.csv" : csvFileName;
        _runCompleteCallback = onRunComplete;
        _running = true;

        _fpsAccum = 0f;
        _fpsFrameCount = 0;
        _fpsTimer = 0f;
        _measuredFPS = 0f;
        _elapsedRealTime = 0f;
        _csvTimer = 0f;

        _csv = new StringBuilder();
        _csv.AppendLine($"# Benchmark run: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        _csv.AppendLine($"# Config: {configLabel}");
        _csv.AppendLine($"# Device: {SystemInfo.deviceModel}  GPU: {SystemInfo.graphicsDeviceName}");
        _csv.AppendLine($"# VRS caps: {SystemInfo.shadingRateTypeCaps}  Renderers modified: {vrsRendererCount}");
        foreach (string metadata in _csvMetadataLines)
            if (!string.IsNullOrWhiteSpace(metadata))
                _csv.AppendLine($"# {metadata}");
        _csv.AppendLine("time_s,fps,phase,speed,vrs_mode,vrs_renderers,throttle");
    }

    // ── MonoBehaviour ────────────────────────────────────────────────────────

    void Update()
    {
        if (_line1 == null || !_running) return;

        // Rolling FPS average.
        _fpsAccum        += Time.unscaledDeltaTime > 0f ? 1f / Time.unscaledDeltaTime : 0f;
        _fpsFrameCount++;
        _fpsTimer        += Time.unscaledDeltaTime;
        _elapsedRealTime += Time.unscaledDeltaTime;

        if (_fpsTimer >= FPS_SAMPLE_WINDOW)
        {
            _measuredFPS   = _fpsAccum / _fpsFrameCount;
            _fpsAccum      = 0f;
            _fpsFrameCount = 0;
            _fpsTimer      = 0f;
        }

        // CSV: one row per 100 ms.
        _csvTimer += Time.unscaledDeltaTime;
        if (_csvTimer >= CSV_INTERVAL && _csv != null)
        {
            _csvTimer = 0f;
            var csvMode  = GraphicsSettings.variableRateShadingMode;
            int throttle = (_measuredFPS > 0f && _measuredFPS < _targetFps * 0.85f) ? 1 : 0;
            _csv.AppendLine(
                $"{_elapsedRealTime:F1},{_measuredFPS:F1},{_currentPhase},{_currentSpeed:F3}," +
                $"{csvMode},{_vrsRendererCount},{throttle}");
        }

        // Line 1: performance numbers + selected config.
        _line1.text = $"{_measuredFPS:F1} fps  |  t={_elapsedRealTime:F1}s  |  {_configLabel}";

        // Line 2: ACTUAL hardware VRS state, read live from the engine every frame.
        // This is the real answer to "did VRS happen?":
        //   caps=None  → hardware does not support VRS on this device (editor on Mac will show this)
        //   caps=Pipeline/Primitive → hardware can do VRS
        //   mode=Custom + renderers>0 → VRS rates were applied and URP will forward them
        // NOTE: confirming at the GPU driver level requires a hardware profiler
        //        (Huawei DevEco Profiler / Mali Graphics Debugger).
        var    caps    = SystemInfo.shadingRateTypeCaps;
        var    mode    = GraphicsSettings.variableRateShadingMode;
        string capStr  = (caps == ShadingRateTypeCaps.None)
                         ? "None — NOT supported on this GPU"
                         : caps.ToString();
        string modeStr = (mode == VariableRateShadingMode.Off)
                         ? "Off"
                         : $"{mode} ({_vrsRendererCount} renderers)";

        // If hardware doesn't support it, label it as Ignored so it isn't misleading on Mac/Editor.
        if (caps == ShadingRateTypeCaps.None && mode != VariableRateShadingMode.Off)
        {
            modeStr = $"Ignored — {modeStr}";
        }

        _line2.text = $"VRS hw: {capStr}  |  active: {modeStr}";

        // Line 3: Deep Hardware Identity (DNA)
        _line3.text = $"GPU: {SystemInfo.graphicsDeviceName}  |  API: {SystemInfo.graphicsDeviceVersion}  |  Caps: {SystemInfo.shadingRateTypeCaps}";
    }

    // ── Speed schedule coroutine ─────────────────────────────────────────────

    private IEnumerator DriveSpeed()
    {
        // No yield return null needed here — speed was already set to 0 in StartFlythrough().
        // Each phase: apply speed, wait exactly realDur wall-clock seconds, repeat.
        int phaseIndex = 0;
        foreach (var (realDur, speed) in Phases)
        {
            // Re-validate each phase. The graph is disposed if anything calls
            // FlythroughDirector.gameObject.SetActive(false) externally.
            if (!_director.playableGraph.IsValid())
            {
                Debug.LogWarning($"[Flythrough] Graph disposed at phase {phaseIndex} — aborting.");
                yield break;
            }

            _director.playableGraph.GetRootPlayable(0).SetSpeed(speed);
            Debug.Log($"[Flythrough] Phase {phaseIndex}: speed={speed:F3}x  realDur={realDur}s");
            _currentPhase = phaseIndex;
            _currentSpeed = speed;
            phaseIndex++;

            yield return new WaitForSecondsRealtime(realDur);
        }

        // Content exhausted — director stops automatically via DirectorWrapMode.None.
        // Restore speed in case anything re-uses this director later.
        if (_director != null && _director.playableGraph.IsValid())
            _director.playableGraph.GetRootPlayable(0).SetSpeed(1.0);

        Debug.Log("[Flythrough] Speed schedule complete — 60 s consumed.");
    }

    // Safety net: fires at 60.5 s. If the speed schedule ran correctly the director
    // is already stopped. If something kept it alive, stop it now and mark run done.
    private IEnumerator HardStop()
    {
        yield return new WaitForSecondsRealtime(_runDurationSeconds + 0.5f);

        if (!_running)
            yield break;

        if (_director != null && _director.state == PlayState.Playing)
        {
            _director.Stop();
            Debug.LogWarning($"[Flythrough] Hard-stop triggered — director was still playing at {_runDurationSeconds + 0.5f:F1} s.");
        }

        FinishRun();
    }

    private void FinishRun()
    {
        _running = false;

        // Show run-complete state on overlay so it's clear the benchmark finished.
        if (_line1 != null) _line1.text = "RUN COMPLETE — check VRS hw line below";
        if (_line2 != null)
        {
            var caps = SystemInfo.shadingRateTypeCaps;
            var mode = GraphicsSettings.variableRateShadingMode;
            _line2.text = $"VRS hw: {caps}  |  final mode: {mode} ({_vrsRendererCount} renderers)";
        }

        // Release cursor — locks in Editor during flythrough to prevent mouse breaking the run.
        // Safe to unlock now; on HarmonyOS device this is a no-op (no mouse).
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible   = true;

        WriteCSV();

        Action runCompleteCallback = _runCompleteCallback;
        _runCompleteCallback = null;
        runCompleteCallback?.Invoke();

        Debug.Log("[Flythrough] Run complete. Check 'VRS hw' line in overlay for hardware verification.");
    }

    // ── CSV output ───────────────────────────────────────────────────────────

    private void WriteCSV()
    {
        if (_csv == null) return;
        try
        {
            string path = Path.Combine(Application.persistentDataPath, _csvFileName);
            File.WriteAllText(path, _csv.ToString());
            Debug.Log($"[Flythrough] CSV saved → {path}");
            // On HarmonyOS device, pull with:
            //   hdc file recv /data/storage/el2/base/files/<csv-file-name> ./
            // On Android/Editor, path is in Application.persistentDataPath.
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Flythrough] Failed to write CSV: {ex.Message}");
        }
    }

    // ── FPS overlay construction ─────────────────────────────────────────────

    private void BuildOverlay()
    {
        if (_overlayCanvas != null)
        {
            _overlayCanvas.SetActive(true);
            return;
        }

        _overlayCanvas = new GameObject("FPS_Overlay");
        var canvas = _overlayCanvas.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;  // above StartCanvas (999)
        var scaler = _overlayCanvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        _overlayCanvas.AddComponent<GraphicRaycaster>();

        // Dark pill — tall enough for two lines (two 32px rows + 8px padding).
        var bgGO  = new GameObject("BG");
        bgGO.transform.SetParent(_overlayCanvas.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.62f);
        var bgRT  = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin        = new Vector2(0f, 1f);  // pin to top-left of screen
        bgRT.anchorMax        = new Vector2(0f, 1f);
        bgRT.pivot            = new Vector2(0f, 1f);
        bgRT.anchoredPosition = new Vector2(12f, -12f);
        bgRT.sizeDelta        = new Vector2(820f, 108f);

        // Line 1 (top row) — white: measured fps, elapsed time, config.
        _line1 = MakeTextRow(bgGO.transform, new Vector2(10f, 72f), new Vector2(-10f, 0f), Color.white);

        // Line 2 (middle row) — amber: live VRS hardware state from engine.
        _line2 = MakeTextRow(bgGO.transform, new Vector2(10f, 36f), new Vector2(-10f, -36f),
                             new Color(1f, 0.85f, 0.35f, 1f));
        _line2.fontSize = OVERLAY_FONT_SIZE - 2;

        // Line 3 (bottom row) — cyan: deep GPU diagnostic (Driver DNA).
        _line3 = MakeTextRow(bgGO.transform, new Vector2(10f, 0f), new Vector2(-10f, -72f),
                             new Color(0.4f, 1f, 1f, 1f));
        _line3.fontSize = OVERLAY_FONT_SIZE - 4;
    }

    private Text MakeTextRow(Transform parent, Vector2 offsetMin, Vector2 offsetMax, Color color)
    {
        var go = new GameObject("Row");
        go.transform.SetParent(parent, false);
        var t  = go.AddComponent<Text>();
        t.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize  = OVERLAY_FONT_SIZE;
        t.color     = color;
        t.alignment = TextAnchor.UpperLeft;
        t.text      = "...";
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = offsetMin;
        rt.offsetMax = offsetMax;
        return t;
    }
}

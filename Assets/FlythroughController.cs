using System.Collections;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// Two responsibilities:
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
    private int              _vrsRendererCount = 0;   // how many scene renderers got VRS
    private bool             _running          = false;

    // FPS tracking
    private float _fpsAccum;
    private int   _fpsFrameCount;
    private float _fpsTimer;
    private float _measuredFPS;
    private float _elapsedRealTime;

    // UI — two lines
    private Text  _line1;   // measured fps | elapsed time | config
    private Text  _line2;   // VRS hardware caps + active mode (read live from engine)

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Call immediately after PlayerManager.EnableFlythrough().
    /// vrsRendererCount: number of scene renderers that received a VRS rate (0 = VRS Off).
    /// </summary>
    public void StartFlythrough(PlayableDirector director, string configLabel, int vrsRendererCount)
    {
        _director          = director;
        _configLabel       = configLabel;
        _vrsRendererCount  = vrsRendererCount;
        _running           = true;

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
        _line2.text = $"VRS hw: {capStr}  |  active: {modeStr}";
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
            phaseIndex++;

            yield return new WaitForSecondsRealtime(realDur);
        }

        // Content exhausted — director stops automatically via DirectorWrapMode.None.
        // Restore speed in case anything re-uses this director later.
        if (_director.playableGraph.IsValid())
            _director.playableGraph.GetRootPlayable(0).SetSpeed(1.0);

        Debug.Log("[Flythrough] Speed schedule complete — 60 s consumed.");
    }

    // Safety net: fires at 60.5 s. If the speed schedule ran correctly the director
    // is already stopped. If something kept it alive, stop it now and mark run done.
    private IEnumerator HardStop()
    {
        yield return new WaitForSecondsRealtime(TOTAL_REAL_DURATION + 0.5f);

        if (_director != null && _director.state == PlayState.Playing)
        {
            _director.Stop();
            Debug.LogWarning("[Flythrough] Hard-stop triggered — director was still playing at 60.5 s.");
        }

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

        Debug.Log("[Flythrough] Run complete. Check 'VRS hw' line in overlay for hardware verification.");
    }

    // ── FPS overlay construction ─────────────────────────────────────────────

    private void BuildOverlay()
    {
        var canvasGO = new GameObject("FPS_Overlay");
        var canvas   = canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;  // above StartCanvas (999)
        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGO.AddComponent<GraphicRaycaster>();

        // Dark pill — tall enough for two lines (two 32px rows + 8px padding).
        var bgGO  = new GameObject("BG");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.62f);
        var bgRT  = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin        = new Vector2(0f, 1f);  // pin to top-left of screen
        bgRT.anchorMax        = new Vector2(0f, 1f);
        bgRT.pivot            = new Vector2(0f, 1f);
        bgRT.anchoredPosition = new Vector2(12f, -12f);
        bgRT.sizeDelta        = new Vector2(820f, 72f);

        // Line 1 (top row) — white: measured fps, elapsed time, config.
        // offsetMin.y=36 → bottom of row is at the middle of the BG (36px from bottom)
        // offsetMax.y=0  → top of row touches BG top
        _line1 = MakeTextRow(bgGO.transform, new Vector2(10f, 36f), new Vector2(-10f, 0f), Color.white);

        // Line 2 (bottom row) — amber: live VRS hardware state from engine.
        // offsetMin.y=4  → 4px padding above BG bottom
        // offsetMax.y=-36 → top of row is at the BG midpoint (36px below BG top)
        _line2 = MakeTextRow(bgGO.transform, new Vector2(10f, 4f), new Vector2(-10f, -36f),
                             new Color(1f, 0.85f, 0.35f, 1f));
        _line2.fontSize = OVERLAY_FONT_SIZE - 2;
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

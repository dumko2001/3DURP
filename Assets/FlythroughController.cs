using System.Collections;
using UnityEngine;
using UnityEngine.Playables;
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

    // ── FPS overlay config ───────────────────────────────────────────────────
    // Measured over a rolling 0.5 s window — short enough to catch transients,
    // long enough not to flicker unreadably. Shown top-left, small but legible.
    private const float FPS_SAMPLE_WINDOW = 0.5f;   // seconds between FPS recalculations
    private const int   OVERLAY_FONT_SIZE = 28;     // ~14 pt equivalent on 1080p

    // ── Runtime state ────────────────────────────────────────────────────────
    private PlayableDirector _director;
    private string           _configLabel = "";      // e.g. "60fps | 2x2 VRS"

    // FPS tracking
    private float _fpsAccum;        // sum of (1/deltaTime) within current window
    private int   _fpsFrameCount;   // frames counted in current window
    private float _fpsTimer;        // time since last FPS recalculation
    private float _measuredFPS;     // last computed average

    // UI refs
    private Text  _overlayText;
    private float _elapsedRealTime;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Call immediately after PlayerManager.EnableFlythrough().
    /// configLabel example: "60fps | 2x2 VRS"
    /// </summary>
    public void StartFlythrough(PlayableDirector director, string configLabel)
    {
        _director    = director;
        _configLabel = configLabel;

        BuildOverlay();
        StartCoroutine(DriveSpeed());
    }

    // ── MonoBehaviour ────────────────────────────────────────────────────────

    void Update()
    {
        if (_overlayText == null) return;

        // Accumulate for rolling FPS average.
        _fpsAccum      += Time.unscaledDeltaTime > 0 ? 1f / Time.unscaledDeltaTime : 0f;
        _fpsFrameCount++;
        _fpsTimer      += Time.unscaledDeltaTime;
        _elapsedRealTime += Time.unscaledDeltaTime;

        if (_fpsTimer >= FPS_SAMPLE_WINDOW)
        {
            _measuredFPS   = _fpsAccum / _fpsFrameCount;
            _fpsAccum      = 0f;
            _fpsFrameCount = 0;
            _fpsTimer      = 0f;
        }

        // Format: "60.0 fps  |  t=12.3s  |  60fps | 2x2 VRS"
        _overlayText.text =
            $"{_measuredFPS:F1} fps  |  t={_elapsedRealTime:F1}s  |  {_configLabel}";
    }

    // ── Speed schedule coroutine ─────────────────────────────────────────────

    private IEnumerator DriveSpeed()
    {
        // One frame delay — ensures director.Play() has initialised the PlayableGraph.
        yield return null;

        if (!_director.playableGraph.IsValid())
        {
            Debug.LogError("[Flythrough] PlayableGraph invalid — speed control aborted.");
            yield break;
        }

        // The root playable (index 0) is the top-level TimelinePlayable.
        var root = _director.playableGraph.GetRootPlayable(0);

        int phaseIndex = 0;
        foreach (var (realDur, speed) in Phases)
        {
            root.SetSpeed(speed);
            Debug.Log($"[Flythrough] Phase {phaseIndex}: speed={speed:F3}x  duration={realDur}s");
            phaseIndex++;

            // WaitForSecondsRealtime is unaffected by Time.timeScale and frame-rate
            // caps, so each phase occupies exactly the intended wall-clock seconds.
            yield return new WaitForSecondsRealtime(realDur);
        }

        // All content consumed. Restore speed; director stops via DirectorWrapMode.None.
        root.SetSpeed(1.0);
        Debug.Log("[Flythrough] Speed schedule complete.");
    }

    // ── FPS overlay construction ─────────────────────────────────────────────

    private void BuildOverlay()
    {
        // Dedicated canvas — ScreenSpaceOverlay so it renders above everything.
        var canvasGO = new GameObject("FPS_Overlay");
        var canvas   = canvasGO.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;   // always on top (> StartCanvas sortingOrder 999)
        var scaler              = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode      = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        canvasGO.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        // Semi-transparent dark pill so text is readable over any background.
        var bgGO  = new GameObject("BG");
        bgGO.transform.SetParent(canvasGO.transform, false);
        var bgImg = bgGO.AddComponent<Image>();
        bgImg.color = new Color(0f, 0f, 0f, 0.55f);
        var bgRT  = bgGO.GetComponent<RectTransform>();
        bgRT.anchorMin = new Vector2(0f, 1f);   // top-left anchor
        bgRT.anchorMax = new Vector2(0f, 1f);
        bgRT.pivot     = new Vector2(0f, 1f);
        bgRT.anchoredPosition = new Vector2(12f, -12f);  // 12 px inset from top-left
        bgRT.sizeDelta        = new Vector2(560f, 42f);

        // Text label inside the pill.
        var txtGO = new GameObject("FPSText");
        txtGO.transform.SetParent(bgGO.transform, false);
        _overlayText = txtGO.AddComponent<Text>();
        _overlayText.font      = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _overlayText.fontSize  = OVERLAY_FONT_SIZE;
        _overlayText.color     = Color.white;
        _overlayText.alignment = TextAnchor.MiddleLeft;
        _overlayText.text      = "-- fps";
        var txtRT = txtGO.GetComponent<RectTransform>();
        txtRT.anchorMin = Vector2.zero;
        txtRT.anchorMax = Vector2.one;
        txtRT.offsetMin = new Vector2(10f, 0f);
        txtRT.offsetMax = Vector2.zero;
    }
}

// InputReplayer.cs
// Replays a path file produced by InputRecorder by driving a target Transform
// directly — no character controller, no physics integration, no input polling.
// This makes each replay deterministic regardless of frame rate, so benchmark
// results across VRS modes and FPS caps remain comparable.
//
// Why transform-based replay instead of raw-input replay:
//   Raw input (stick/mouse values) must pass through the character controller's
//   velocity integration, which is frame-rate dependent.  Two runs at different
//   FPS targets will produce slightly different camera positions, making GPU
//   load comparisons noisy.  Driving the transform directly removes that
//   variable while still using a path that came from real user movement.
//
// Integration with the benchmark harness:
//   InputReplayer uses the same onRunComplete Action callback pattern as
//   FlythroughController, so StartScreenUI can treat both interchangeably.
//   The FlythroughController's FPS overlay and CSV writer are independent
//   components and can run alongside a replay.
//
// Typical setup:
//   1. Attach InputReplayer to the scene Manager (or any persistent GO).
//   2. Set `target` to the active camera root.
//   3. Call Load() once (e.g. in Awake or just before the first run).
//   4. Call StartReplay(onComplete) to begin; pass the same completion
//      callback you would pass to FlythroughController.StartFlythrough().

using System;
using System.Collections;
using System.IO;
using UnityEngine;

public class InputReplayer : MonoBehaviour
{
    [Tooltip("Transform to animate during replay (usually the camera root or player).")]
    public Transform target;

    [Tooltip("Path to the .bin file written by InputRecorder. " +
             "Defaults to InputRecorder.DefaultSavePath if left blank.")]
    public string recordingPath;

    // ── Public state ─────────────────────────────────────────────────────────

    public bool IsReplaying  { get; private set; }

    /// <summary>Total duration of the loaded recording in seconds, or 0 if none loaded.</summary>
    public float Duration => (_frames != null && _frames.Length > 0)
        ? _frames[_frames.Length - 1].t
        : 0f;

    /// <summary>True after a successful Load() call.</summary>
    public bool IsLoaded => _frames != null;

    // ── Internal ─────────────────────────────────────────────────────────────

    private ReplayFrame[] _frames;
    private float         _recordHz;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        if (target == null)
            target = transform;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Read a recording from disk and prepare it for playback.
    /// Call once before <see cref="StartReplay"/>.
    /// </summary>
    /// <param name="path">Path to .bin file; uses <see cref="InputRecorder.DefaultSavePath"/> if null.</param>
    /// <returns>True on success, false if the file is missing or corrupt (error logged).</returns>
    public bool Load(string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = string.IsNullOrEmpty(recordingPath)
                ? InputRecorder.DefaultSavePath
                : recordingPath;

        if (!File.Exists(path))
        {
            Debug.LogError($"[InputReplayer] Recording not found: {path}");
            _frames = null;
            return false;
        }

        try
        {
            using var r   = new BinaryReader(File.OpenRead(path));
            int version   = r.ReadInt32();
            if (version != 1)
            {
                Debug.LogError($"[InputReplayer] Unknown recording format version {version}");
                _frames = null;
                return false;
            }

            _recordHz     = r.ReadSingle();
            int count     = r.ReadInt32();
            _frames       = new ReplayFrame[count];

            for (int i = 0; i < count; i++)
            {
                _frames[i] = new ReplayFrame
                {
                    t   = r.ReadSingle(),
                    pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
                    rot = new Quaternion(r.ReadSingle(), r.ReadSingle(),
                                        r.ReadSingle(), r.ReadSingle()),
                };
                // Raw input axes are stored for reference; skip them during replay.
                r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadSingle();
            }

            Debug.Log($"[InputReplayer] Loaded {count} frames " +
                      $"({count / _recordHz:F1}s @ {_recordHz}Hz) from {path}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[InputReplayer] Failed to parse recording: {ex.Message}");
            _frames = null;
            return false;
        }
    }

    /// <summary>
    /// Start replaying the loaded path.  Drives <see cref="target"/> until the
    /// path ends, then calls <paramref name="onComplete"/>.
    /// </summary>
    /// <returns>False if no recording is loaded (call Load() first).</returns>
    public bool StartReplay(Action onComplete = null)
    {
        if (_frames == null)
        {
            Debug.LogError("[InputReplayer] No recording loaded — call Load() before StartReplay().");
            return false;
        }
        if (IsReplaying)
            StopReplay();

        if (target == null)
            target = transform;

        StartCoroutine(ReplayCoroutine(onComplete));
        return true;
    }

    /// <summary>Stop an in-progress replay immediately.</summary>
    public void StopReplay()
    {
        StopAllCoroutines();
        IsReplaying = false;
    }

    // ── Replay coroutine ─────────────────────────────────────────────────────

    private IEnumerator ReplayCoroutine(Action onComplete)
    {
        IsReplaying = true;
        float startReal = Time.realtimeSinceStartup;
        int   idx       = 0;

        while (true)
        {
            float elapsed = Time.realtimeSinceStartup - startReal;

            // Advance index to the last frame whose timestamp we have passed.
            while (idx < _frames.Length - 1 && _frames[idx + 1].t <= elapsed)
                idx++;

            if (idx >= _frames.Length - 1)
            {
                // Reached or passed the final frame — snap and finish.
                target.position = _frames[_frames.Length - 1].pos;
                target.rotation = _frames[_frames.Length - 1].rot;
                break;
            }

            // Lerp between the surrounding frames for smooth sub-frame motion.
            float span  = _frames[idx + 1].t - _frames[idx].t;
            float local = elapsed - _frames[idx].t;
            float frac  = span > 0f ? Mathf.Clamp01(local / span) : 1f;

            target.position = Vector3.Lerp(
                _frames[idx].pos, _frames[idx + 1].pos, frac);
            target.rotation = Quaternion.Slerp(
                _frames[idx].rot, _frames[idx + 1].rot, frac);

            yield return null;
        }

        IsReplaying = false;
        onComplete?.Invoke();
        Debug.Log("[InputReplayer] Replay complete.");
    }

    // ── Data types ────────────────────────────────────────────────────────────

    private struct ReplayFrame
    {
        public float      t;
        public Vector3    pos;
        public Quaternion rot;
    }
}

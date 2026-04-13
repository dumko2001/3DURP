// InputRecorder.cs
// Records camera/player transform snapshots and raw input axis values to a binary
// file during a manual play session.  The resulting file can then be replayed by
// InputReplayer to produce a deterministic, human-driven camera path without a
// character controller — more realistic than a hand-authored Timeline because the
// path came from actual user movement, and still deterministic because replay
// drives the transform directly (no physics integration drift).
//
// Usage:
//   1. Attach to the active camera or player root in the scene.
//   2. Enter Play mode and manually walk/fly through the scene.
//   3. Press R (or the configured toggleKey) to start recording; press again to stop.
//   4. The .bin file is written to Application.persistentDataPath/recorded_path.bin.
//   5. That file is read by InputReplayer at benchmark time.
//
// On HarmonyOS device, pull the recording with:
//   hdc file recv /data/storage/el2/base/files/recorded_path.bin ./recorded_path.bin

using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class InputRecorder : MonoBehaviour
{
    [Tooltip("Transform whose position and rotation are captured each sample. " +
             "Defaults to this GameObject's transform if left blank.")]
    public Transform target;

    [Tooltip("How many snapshots to capture per second.")]
    public float recordHz = 60f;

    [Tooltip("Keyboard shortcut to toggle recording on/off during Play mode.")]
    public KeyCode toggleKey = KeyCode.R;

    // ── Public state ─────────────────────────────────────────────────────────

    public bool IsRecording { get; private set; }

    /// <summary>Default on-device path used by both recorder and replayer.</summary>
    public static string DefaultSavePath =>
        Path.Combine(Application.persistentDataPath, "recorded_path.bin");

    // ── Internal ─────────────────────────────────────────────────────────────

    private readonly List<PathSample> _samples = new();
    private float _startTime;
    private float _nextSampleTime;
    private float _interval;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    void Awake()
    {
        if (target == null)
            target = transform;
        _interval = 1f / Mathf.Max(1f, recordHz);
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (IsRecording) StopRecording();
            else             StartRecording();
        }
    }

    void LateUpdate()
    {
        if (!IsRecording) return;

        float elapsed = Time.realtimeSinceStartup - _startTime;
        if (elapsed < _nextSampleTime) return;

        _nextSampleTime += _interval;

        _samples.Add(new PathSample
        {
            t      = elapsed,
            pos    = target.position,
            rot    = target.rotation,
            axisH  = Input.GetAxisRaw("Horizontal"),
            axisV  = Input.GetAxisRaw("Vertical"),
            mouseX = Input.GetAxisRaw("Mouse X"),
            mouseY = Input.GetAxisRaw("Mouse Y"),
        });
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Begin capturing; clears any previously held samples.</summary>
    public void StartRecording()
    {
        _samples.Clear();
        _startTime      = Time.realtimeSinceStartup;
        _nextSampleTime = 0f;
        IsRecording     = true;
        Debug.Log($"[InputRecorder] Recording started — press {toggleKey} to stop");
    }

    /// <summary>Stop capturing and write to <see cref="DefaultSavePath"/>.</summary>
    public void StopRecording() => StopRecording(DefaultSavePath);

    /// <summary>Stop capturing and write to the specified path.</summary>
    public void StopRecording(string path)
    {
        IsRecording = false;
        WriteBinary(path);
        float duration = _samples.Count > 0 ? _samples[_samples.Count - 1].t : 0f;
        Debug.Log($"[InputRecorder] Saved {_samples.Count} samples " +
                  $"({duration:F1}s @ {recordHz}Hz) → {path}");
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    // Binary format (version 1):
    //   int32   version      = 1
    //   float32 recordHz
    //   int32   sampleCount
    //   per sample:
    //     float32  t          (seconds since StartRecording)
    //     float32  pos.x/y/z
    //     float32  rot.x/y/z/w  (Quaternion)
    //     float32  axisH, axisV, mouseX, mouseY  (raw input axes, stored for reference)

    private void WriteBinary(string path)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var w = new BinaryWriter(File.Open(path, FileMode.Create));
        w.Write(1);              // format version
        w.Write(recordHz);
        w.Write(_samples.Count);
        foreach (var s in _samples)
        {
            w.Write(s.t);
            w.Write(s.pos.x); w.Write(s.pos.y); w.Write(s.pos.z);
            w.Write(s.rot.x); w.Write(s.rot.y); w.Write(s.rot.z); w.Write(s.rot.w);
            w.Write(s.axisH); w.Write(s.axisV);
            w.Write(s.mouseX); w.Write(s.mouseY);
        }
    }

    // ── Data types ────────────────────────────────────────────────────────────

    private struct PathSample
    {
        public float      t;
        public Vector3    pos;
        public Quaternion rot;
        public float      axisH, axisV, mouseX, mouseY;
    }
}

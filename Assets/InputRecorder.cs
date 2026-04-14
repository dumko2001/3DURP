// InputRecorder.cs
// Records real gameplay input from StarterAssetsInputs for Oasis benchmarking.
//
// This is intentionally not transform capture. The goal is to preserve the same
// player/controller/collision/camera loop that live gameplay uses, including the
// same move/look/jump/sprint/crouch state consumed by FirstPersonController.
//
// Usage:
//   1. Attach to the active player root (or any object in the scene).
//   2. Ensure `inputSource` resolves to the Oasis StarterAssetsInputs component.
//   3. Enter Play mode and play normally.
//   4. Press R (or the configured toggleKey) to toggle recording start/stop.
//      Press Esc (or stopKey) to force-stop recording.
//   5. The .bin file is written to Application.persistentDataPath/gameplay_input_recording.bin.
//
// On HarmonyOS device, pull the recording with:
//   hdc file recv /data/storage/el2/base/files/gameplay_input_recording.bin ./gameplay_input_recording.bin

using System.Collections.Generic;
using System;
using System.IO;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DefaultExecutionOrder(1000)]
public class InputRecorder : MonoBehaviour
{
    [Tooltip("StarterAssetsInputs source to sample. Defaults to the first one found in the active scene.")]
    public StarterAssetsInputs inputSource;

    [Tooltip("Player root transform controlled by FirstPersonController. Defaults to inputSource.transform.")]
    public Transform playerRoot;

    [Tooltip("Optional FirstPersonController used to capture the starting camera pitch.")]
    public FirstPersonController controller;

    [Tooltip("Keyboard shortcut to toggle recording on/off during Play mode.")]
    public KeyCode toggleKey = KeyCode.R;

    [Tooltip("Secondary key to stop recording immediately during Play mode.")]
    public KeyCode stopKey = KeyCode.Escape;

    [Tooltip("Automatically start recording when Play mode begins.")]
    public bool autoStartOnPlay;

    [Tooltip("Save new recordings as timestamped files so multiple gameplay paths can be kept.")]
    public bool useTimestampedFileNames = true;

    public bool IsRecording { get; private set; }

    public string LastSavedPath { get; private set; }

    public static string DefaultSavePath =>
        Path.Combine(Application.persistentDataPath, "gameplay_input_recording.bin");

    private readonly List<InputFrame> _frames = new();
    private float _startTime;
    private RecordingHeader _header;

    void Awake()
    {
        ResolveReferences();
    }

    void Start()
    {
        if (autoStartOnPlay)
            StartRecording();
    }

    void Update()
    {
        if (WasKeyPressed(toggleKey))
        {
            if (IsRecording) StopRecording();
            else             StartRecording();
        }

        if (IsRecording && WasKeyPressed(stopKey))
            StopRecording();
    }

    void LateUpdate()
    {
        if (!IsRecording || inputSource == null)
            return;

        _frames.Add(new InputFrame
        {
            t      = Time.realtimeSinceStartup - _startTime,
            move   = inputSource.move,
            look   = inputSource.look,
            jump   = inputSource.jump,
            sprint = inputSource.sprint,
            crouch = inputSource.crouch,
        });
    }

    public void StartRecording()
    {
        ResolveReferences();
        if (inputSource == null || playerRoot == null)
        {
            Debug.LogError("[InputRecorder] StarterAssetsInputs/player root not found — cannot record gameplay input.");
            return;
        }

        _frames.Clear();
        _header = new RecordingHeader
        {
            scenePath      = SceneManager.GetActiveScene().path,
            playerPosition = playerRoot.position,
            playerRotation = playerRoot.rotation,
            cameraPitch    = CaptureCameraPitch(),
        };

        _startTime  = Time.realtimeSinceStartup;
        IsRecording = true;

        Debug.Log($"[InputRecorder] Recording gameplay input in {SceneLabel(_header.scenePath)} — press {toggleKey} (or {stopKey}) to stop.");
    }

    public void StopRecording()
    {
        StopRecording(useTimestampedFileNames ? BuildTimestampedSavePath(_header.scenePath) : DefaultSavePath);
    }

    public void StopRecording(string path)
    {
        if (!IsRecording && _frames.Count == 0)
        {
            Debug.LogWarning("[InputRecorder] No gameplay input has been recorded yet.");
            return;
        }

        IsRecording = false;
        WriteBinary(path);
    LastSavedPath = path;

        float duration = _frames.Count > 0 ? _frames[_frames.Count - 1].t : 0f;
        Debug.Log($"[InputRecorder] Saved {_frames.Count} gameplay frames ({duration:F1}s) → {path}");
    }

    private static bool WasKeyPressed(KeyCode key)
    {
        if (key == KeyCode.None)
            return false;

#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            switch (key)
            {
                case KeyCode.R:      return keyboard.rKey.wasPressedThisFrame;
                case KeyCode.Escape: return keyboard.escapeKey.wasPressedThisFrame;
                case KeyCode.Space:  return keyboard.spaceKey.wasPressedThisFrame;
                case KeyCode.C:      return keyboard.cKey.wasPressedThisFrame;
            }
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(key);
#else
        return false;
#endif
    }

    private void ResolveReferences()
    {
        if (inputSource == null || playerRoot == null || controller == null)
        {
            if (GameplayInputResolver.TryResolve(out var resolvedInput, out var resolvedRoot, out var resolvedController))
            {
                inputSource ??= resolvedInput;
                playerRoot ??= resolvedRoot;
                controller ??= resolvedController;
            }
        }
    }

    public static string BuildTimestampedSavePath(string scenePath)
    {
        string sceneLabel = SanitizeFileName(SceneLabel(scenePath));
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string fileName = $"gameplay_input_{sceneLabel}_{timestamp}.bin";
        return Path.Combine(Application.persistentDataPath, fileName);
    }

    private float CaptureCameraPitch()
    {
        if (controller == null || controller.CinemachineCameraTarget == null)
            return 0f;

        return NormalizeSignedAngle(controller.CinemachineCameraTarget.transform.localEulerAngles.x);
    }

    private void WriteBinary(string path)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        using var writer = new BinaryWriter(File.Open(path, FileMode.Create));
        writer.Write(3);  // format version: gameplay input frames + crouch
        writer.Write(_header.scenePath ?? "");
        writer.Write(_header.playerPosition.x);
        writer.Write(_header.playerPosition.y);
        writer.Write(_header.playerPosition.z);
        writer.Write(_header.playerRotation.x);
        writer.Write(_header.playerRotation.y);
        writer.Write(_header.playerRotation.z);
        writer.Write(_header.playerRotation.w);
        writer.Write(_header.cameraPitch);
        writer.Write(_frames.Count);

        foreach (var frame in _frames)
        {
            writer.Write(frame.t);
            writer.Write(frame.move.x);
            writer.Write(frame.move.y);
            writer.Write(frame.look.x);
            writer.Write(frame.look.y);
            writer.Write(frame.jump);
            writer.Write(frame.sprint);
            writer.Write(frame.crouch);
        }
    }

    private static float NormalizeSignedAngle(float angle)
    {
        if (angle > 180f)
            angle -= 360f;
        return angle;
    }

    private static string SceneLabel(string scenePath)
    {
        return string.IsNullOrEmpty(scenePath)
            ? "<unsaved scene>"
            : Path.GetFileNameWithoutExtension(scenePath);
    }

    private static string SanitizeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "scene";

        foreach (char invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value.Replace(' ', '_');
    }

    private struct RecordingHeader
    {
        public string     scenePath;
        public Vector3    playerPosition;
        public Quaternion playerRotation;
        public float      cameraPitch;
    }

    private struct InputFrame
    {
        public float   t;
        public Vector2 move;
        public Vector2 look;
        public bool    jump;
        public bool    sprint;
        public bool    crouch;
    }
}

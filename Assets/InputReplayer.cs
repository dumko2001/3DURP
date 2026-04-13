// InputReplayer.cs
// Replays gameplay input back through StarterAssetsInputs so the real Oasis
// first-person controller, CharacterController, gravity, collisions, and camera
// logic all remain active during the run.
//
// This intentionally keeps the natural noise that comes from live controller
// integration. The comparison target is "what does VRS change during gameplay?",
// not "what does VRS change on a perfectly fixed camera path?"

using System;
using System.IO;
using System.Reflection;
using StarterAssets;
using UnityEngine;
using UnityEngine.SceneManagement;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DefaultExecutionOrder(-1000)]
public class InputReplayer : MonoBehaviour
{
    [Tooltip("StarterAssetsInputs target to drive. Defaults to the first one found in the active scene.")]
    public StarterAssetsInputs targetInput;

    [Tooltip("Player root transform controlled by FirstPersonController. Defaults to targetInput.transform.")]
    public Transform playerRoot;

    [Tooltip("Optional FirstPersonController used to reset controller state and apply recorded pitch.")]
    public FirstPersonController controller;

    [Tooltip("Path to the .bin recording written by InputRecorder. Defaults to InputRecorder.DefaultSavePath if blank.")]
    public string recordingPath;

    [Tooltip("Automatically load the configured recording on Awake.")]
    public bool loadOnAwake;

    [Tooltip("Disable PlayerInput while replay is active so live touches/mouse/controller input cannot interfere.")]
    public bool disableLivePlayerInput = true;

    [Tooltip("Warn if the loaded recording was captured in a different scene.")]
    public bool warnOnSceneMismatch = true;

    public bool IsReplaying { get; private set; }

    public bool IsLoaded => _frames != null && _frames.Length > 0;

    public float Duration => IsLoaded ? _frames[_frames.Length - 1].t : 0f;

    public string RecordingFileName => Path.GetFileName(
        string.IsNullOrEmpty(recordingPath) ? InputRecorder.DefaultSavePath : recordingPath);

    public string RecordingScenePath => _header.scenePath;

    public string RecordingSceneName => SceneLabel(_header.scenePath);

    private RecordingHeader _header;
    private InputFrame[] _frames;
    private int _frameIndex;
    private float _startTime;
    private Action _onComplete;

#if ENABLE_INPUT_SYSTEM
    private PlayerInput _playerInput;
    private bool _restorePlayerInput;
#endif

    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly FieldInfo PitchField = typeof(FirstPersonController).GetField("_cinemachineTargetPitch", PrivateInstance);
    private static readonly FieldInfo SpeedField = typeof(FirstPersonController).GetField("_speed", PrivateInstance);
    private static readonly FieldInfo RotationVelocityField = typeof(FirstPersonController).GetField("_rotationVelocity", PrivateInstance);
    private static readonly FieldInfo VerticalVelocityField = typeof(FirstPersonController).GetField("_verticalVelocity", PrivateInstance);
    private static readonly FieldInfo JumpTimeoutField = typeof(FirstPersonController).GetField("_jumpTimeoutDelta", PrivateInstance);
    private static readonly FieldInfo FallTimeoutField = typeof(FirstPersonController).GetField("_fallTimeoutDelta", PrivateInstance);

    void Awake()
    {
        ResolveReferences();
        if (loadOnAwake)
            Load();
    }

    void Update()
    {
        if (!IsReplaying || !IsLoaded)
            return;

        float elapsed = Time.realtimeSinceStartup - _startTime;
        while (_frameIndex < _frames.Length - 1 && _frames[_frameIndex + 1].t <= elapsed)
            _frameIndex++;

        ApplyFrame(_frames[_frameIndex]);

        if (elapsed >= _frames[_frames.Length - 1].t)
            FinishReplay();
    }

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
            using var reader = new BinaryReader(File.OpenRead(path));
            int version = reader.ReadInt32();
            if (version != 2)
            {
                string detail = version == 1
                    ? "This file was written by the older transform-path recorder and cannot drive gameplay input. Record a new gameplay-input file first."
                    : $"Unknown recording format version {version}.";
                Debug.LogError($"[InputReplayer] {detail}");
                _frames = null;
                return false;
            }

            _header = new RecordingHeader
            {
                scenePath      = reader.ReadString(),
                playerPosition = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                playerRotation = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                cameraPitch    = reader.ReadSingle(),
            };

            int count = reader.ReadInt32();
            if (count <= 0)
            {
                Debug.LogError("[InputReplayer] Recording contains no gameplay frames.");
                _frames = null;
                return false;
            }

            _frames = new InputFrame[count];
            for (int i = 0; i < count; i++)
            {
                _frames[i] = new InputFrame
                {
                    t      = reader.ReadSingle(),
                    move   = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    look   = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                    jump   = reader.ReadBoolean(),
                    sprint = reader.ReadBoolean(),
                };
            }

            recordingPath = path;
            Debug.Log($"[InputReplayer] Loaded {count} gameplay frames ({Duration:F1}s) from {path}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[InputReplayer] Failed to parse recording: {ex.Message}");
            _frames = null;
            return false;
        }
    }

    public bool StartReplay(Action onComplete = null)
    {
        if (!IsLoaded)
        {
            Debug.LogError("[InputReplayer] No gameplay recording loaded — call Load() before StartReplay().");
            return false;
        }

        ResolveReferences();
        if (targetInput == null || playerRoot == null)
        {
            Debug.LogError("[InputReplayer] StarterAssetsInputs/player root not found — cannot replay gameplay input.");
            return false;
        }

        if (warnOnSceneMismatch)
            WarnIfSceneDiffers();

        if (IsReplaying)
            StopReplay();

        SetLiveInputEnabled(false);
        ResetToRecordedStart();

        _frameIndex  = 0;
        _startTime   = Time.realtimeSinceStartup;
        _onComplete  = onComplete;
        IsReplaying  = true;

        ApplyFrame(_frames[0]);
        Debug.Log($"[InputReplayer] Replaying gameplay input in {SceneLabel(_header.scenePath)}.");
        return true;
    }

    public void StopReplay()
    {
        if (!IsReplaying)
            return;

        EndReplay(false);
    }

    private void ResolveReferences()
    {
        if (targetInput == null)
            targetInput = FindObjectOfType<StarterAssetsInputs>(true);

        if (playerRoot == null && targetInput != null)
            playerRoot = targetInput.transform;

        if (controller == null && targetInput != null)
            controller = targetInput.GetComponent<FirstPersonController>();

#if ENABLE_INPUT_SYSTEM
        if (_playerInput == null && targetInput != null)
            _playerInput = targetInput.GetComponent<PlayerInput>();
#endif
    }

    private void WarnIfSceneDiffers()
    {
        string activeScene = SceneManager.GetActiveScene().path;
        if (!string.IsNullOrEmpty(_header.scenePath) && !string.Equals(activeScene, _header.scenePath, StringComparison.Ordinal))
        {
            Debug.LogWarning($"[InputReplayer] Recording was captured in {SceneLabel(_header.scenePath)} but active scene is {SceneLabel(activeScene)}.");
        }
    }

    private void ResetToRecordedStart()
    {
        var characterController = controller != null ? controller.GetComponent<CharacterController>() : null;
        bool reenableController = characterController != null && characterController.enabled;
        if (reenableController)
            characterController.enabled = false;

        playerRoot.SetPositionAndRotation(_header.playerPosition, _header.playerRotation);

        if (controller != null)
        {
            if (controller.CinemachineCameraTarget != null)
                controller.CinemachineCameraTarget.transform.localRotation = Quaternion.Euler(_header.cameraPitch, 0f, 0f);

            SetField(PitchField, controller, _header.cameraPitch);
            SetField(SpeedField, controller, 0f);
            SetField(RotationVelocityField, controller, 0f);
            SetField(VerticalVelocityField, controller, -2f);
            SetField(JumpTimeoutField, controller, controller.JumpTimeout);
            SetField(FallTimeoutField, controller, controller.FallTimeout);
        }

        if (reenableController)
            characterController.enabled = true;

        ApplyNeutralInput();
    }

    private void ApplyFrame(InputFrame frame)
    {
        targetInput.MoveInput(frame.move);
        targetInput.LookInput(frame.look);
        targetInput.JumpInput(frame.jump);
        targetInput.SprintInput(frame.sprint);
    }

    private void ApplyNeutralInput()
    {
        if (targetInput == null)
            return;

        targetInput.MoveInput(Vector2.zero);
        targetInput.LookInput(Vector2.zero);
        targetInput.JumpInput(false);
        targetInput.SprintInput(false);
    }

    private void FinishReplay()
    {
        EndReplay(true);
    }

    private void EndReplay(bool invokeCallback)
    {
        IsReplaying = false;
        ApplyNeutralInput();
        SetLiveInputEnabled(true);

        Action callback = _onComplete;
        _onComplete = null;

        if (invokeCallback)
        {
            callback?.Invoke();
            Debug.Log("[InputReplayer] Gameplay replay complete.");
        }
        else
        {
            Debug.Log("[InputReplayer] Gameplay replay stopped.");
        }
    }

    private static void SetField(FieldInfo field, object target, object value)
    {
        field?.SetValue(target, value);
    }

    private void SetLiveInputEnabled(bool enabled)
    {
#if ENABLE_INPUT_SYSTEM
        if (!disableLivePlayerInput || _playerInput == null)
            return;

        if (!enabled)
        {
            _restorePlayerInput = _playerInput.enabled;
            _playerInput.enabled = false;
        }
        else if (_restorePlayerInput)
        {
            _playerInput.enabled = true;
            _restorePlayerInput = false;
        }
#endif
    }

    private static string SceneLabel(string scenePath)
    {
        return string.IsNullOrEmpty(scenePath)
            ? "<unsaved scene>"
            : Path.GetFileNameWithoutExtension(scenePath);
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
    }
}

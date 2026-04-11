using System.Collections.Generic;
using UnityEngine;

// Attach to Main Camera
// Press R to start recording, S to stop, P to play back
public class CameraRecorder : MonoBehaviour
{
    struct CameraFrame
    {
        public Vector3 position;
        public Quaternion rotation;
        public float timestamp;
    }

    private List<CameraFrame> recordedFrames = new List<CameraFrame>();
    private bool isRecording = false;
    private bool isPlaying = false;
    private float recordingStartTime;
    private float playbackStartTime;
    private int playbackIndex = 0;

    // Called by StartScreenUI when scene begins
    public void StartPlayback()
    {
        if (recordedFrames.Count == 0)
        {
            Debug.LogWarning("No recorded frames! Press R to record first.");
            return;
        }
        isPlaying = true;
        playbackIndex = 0;
        playbackStartTime = Time.time;
        Debug.Log("Playback started.");
    }

    void Update()
    {
        // --- Manual triggers (for your recording session only) ---
        if (Input.GetKeyDown(KeyCode.R) && !isPlaying)
        {
            recordedFrames.Clear();
            isRecording = true;
            recordingStartTime = Time.time;
            Debug.Log("Recording started...");
        }

        if (Input.GetKeyDown(KeyCode.S) && isRecording)
        {
            isRecording = false;
            Debug.Log($"Recording stopped. {recordedFrames.Count} frames saved.");
        }

        if (Input.GetKeyDown(KeyCode.P) && !isRecording)
        {
            StartPlayback();
        }

        // --- Record frame ---
        if (isRecording)
        {
            recordedFrames.Add(new CameraFrame
            {
                position = transform.position,
                rotation = transform.rotation,
                timestamp = Time.time - recordingStartTime
            });
        }

        // --- Playback frame ---
        if (isPlaying && playbackIndex < recordedFrames.Count - 1)
        {
            float elapsed = Time.time - playbackStartTime;
            while (playbackIndex < recordedFrames.Count - 2 &&
                   recordedFrames[playbackIndex + 1].timestamp <= elapsed)
            {
                playbackIndex++;
            }

            CameraFrame a = recordedFrames[playbackIndex];
            CameraFrame b = recordedFrames[playbackIndex + 1];
            float t = Mathf.InverseLerp(a.timestamp, b.timestamp, elapsed);

            transform.position = Vector3.Lerp(a.position, b.position, t);
            transform.rotation = Quaternion.Slerp(a.rotation, b.rotation, t);
        }

        if (isPlaying && playbackIndex >= recordedFrames.Count - 1)
        {
            isPlaying = false;
            Debug.Log("Playback complete.");
        }
    }

    // Save to file so you don't have to re-record every run
    public void SaveRecording()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var f in recordedFrames)
            sb.AppendLine($"{f.timestamp},{f.position.x},{f.position.y},{f.position.z},{f.rotation.x},{f.rotation.y},{f.rotation.z},{f.rotation.w}");
        System.IO.File.WriteAllText(Application.persistentDataPath + "/camera_path.csv", sb.ToString());
        Debug.Log($"Saved to {Application.persistentDataPath}/camera_path.csv");
    }

    public void LoadRecording()
    {
        string path = Application.persistentDataPath + "/camera_path.csv";
        if (!System.IO.File.Exists(path)) { Debug.LogWarning("No saved recording found."); return; }
        recordedFrames.Clear();
        foreach (var line in System.IO.File.ReadAllLines(path))
        {
            var p = line.Split(',');
            if (p.Length < 8) continue;
            recordedFrames.Add(new CameraFrame {
                timestamp = float.Parse(p[0]),
                position  = new Vector3(float.Parse(p[1]), float.Parse(p[2]), float.Parse(p[3])),
                rotation  = new Quaternion(float.Parse(p[4]), float.Parse(p[5]), float.Parse(p[6]), float.Parse(p[7]))
            });
        }
        Debug.Log($"Loaded {recordedFrames.Count} frames.");
    }
}
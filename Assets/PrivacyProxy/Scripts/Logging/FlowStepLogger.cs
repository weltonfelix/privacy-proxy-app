using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

// One line per measured step in logs/etapas.jsonl, for the timing tables of the evaluation tasks.
// capture is the photo number in this run (0 = steps that happen when the app opens).
public static class FlowStepLogger
{
    [Serializable]
    sealed class Entry
    {
        public int capture;
        public string step;
        public double ms;
        public bool first_call; // the first run of a step in this app session is slower
        public string timestamp;
    }

    static readonly HashSet<string> seen = new HashSet<string>();

    public static string LogPath => Path.Combine(Application.persistentDataPath, "logs", "etapas.jsonl");

    public static void Append(int capture, string step, double ms)
    {
        if (!Application.isPlaying) return; // EditMode tests must not write to the real log
        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var entry = new Entry
            {
                capture = capture,
                step = step,
                ms = Math.Round(ms, 1),
                first_call = seen.Add(step),
                timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            File.AppendAllText(path, JsonUtility.ToJson(entry) + Environment.NewLine);
        }
        catch (Exception error)
        {
            Debug.LogError("Flow step log could not be written: " + error.Message);
        }
    }
}

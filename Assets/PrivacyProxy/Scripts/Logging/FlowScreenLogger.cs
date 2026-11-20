using System;
using System.Globalization;
using System.IO;
using UnityEngine;

public static class FlowScreenLogger
{
    [Serializable]
    sealed class Entry
    {
        public string screen;
        public long time_ms;
        public string text;
        public string timestamp;
    }

    public static string LogPath => Path.Combine(Application.persistentDataPath, "logs", "flow-screen-times.jsonl");

    public static void Append(int screenNumber, double elapsedSeconds, string qwenText)
    {
        try
        {
            string path = LogPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var entry = new Entry
            {
                screen = "screen_" + screenNumber,
                time_ms = (long)Math.Round(Math.Max(0, elapsedSeconds) * 1000),
                text = qwenText ?? "",
                timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            File.AppendAllText(path, JsonUtility.ToJson(entry) + Environment.NewLine);
            Debug.Log("Flow screen log written: " + path);
        }
        catch (Exception error)
        {
            Debug.LogError("Flow screen log could not be written: " + error.Message);
        }
    }
}

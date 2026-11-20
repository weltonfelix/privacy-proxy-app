using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

// Checks the Qwen files copied to the headset against Resources/qwen_models.json.
// The first open checks the SHA-256 and writes a stamp; later opens check only the size
// (and the file date, so a re-copied file is hashed again).
public static class ModelCheck
{
    [Serializable]
    public class Entry
    {
        public string name;
        public long size;
        public string sha256;
    }

    [Serializable]
    public class ModelList
    {
        public Entry[] files;
    }

    public static string CopyCommand(string fileName) =>
        $"adb push CAMINHO_LOCAL/{fileName} \"/sdcard/Android/data/{Application.identifier}/files/models/\"";

    static string CopyHint(string fileName) => "Copie este arquivo pelo computador:\n" + CopyCommand(fileName);

    public static ModelList Parse(string json) => JsonUtility.FromJson<ModelList>(json);

    // Fast: existence and size. Returns null when fine, else a message for the screen.
    public static string CheckSizes(string dir, Entry[] files)
    {
        foreach (var f in files)
        {
            var info = new FileInfo(Path.Combine(dir, f.name));
            if (!info.Exists)
                return $"Falta o arquivo {f.name}.\n{CopyHint(f.name)}";
            if (info.Length != f.size)
                return $"O arquivo {f.name} está incompleto ou errado "
                     + $"({info.Length:N0} bytes, o certo é {f.size:N0}).\n{CopyHint(f.name)}";
        }
        return null;
    }

    // Files whose hash was not confirmed yet for this exact copy (size and date).
    public static Entry[] Unverified(string dir, Entry[] files, string stampPath)
    {
        var stamp = ReadStamp(stampPath);
        var todo = new List<Entry>();
        foreach (var f in files)
            if (!stamp.TryGetValue(f.name, out var line) || line != StampLine(dir, f))
                todo.Add(f);
        return todo.ToArray();
    }

    // Slow: reads every byte. Run it off the main thread. Returns null when fine, else a message.
    // Only files with the right hash go into the stamp.
    public static string CheckHashes(string dir, Entry[] files, string stampPath)
    {
        var stamp = ReadStamp(stampPath);
        string error = null;
        foreach (var f in files)
        {
            if (!string.Equals(Sha256(Path.Combine(dir, f.name)), f.sha256, StringComparison.OrdinalIgnoreCase))
            {
                stamp.Remove(f.name);
                error ??= $"O arquivo {f.name} está corrompido (a impressão digital não confere).\n{CopyHint(f.name)}";
                continue;
            }
            stamp[f.name] = StampLine(dir, f);
        }
        try
        {
            File.WriteAllLines(stampPath, stamp.Values);
        }
        catch (Exception e) // the files are fine; without the stamp the next open just hashes again
        {
            Debug.LogWarning($"Model check: could not write {stampPath}: {e.Message}");
        }
        return error;
    }

    public static string Sha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static string StampLine(string dir, Entry f)
    {
        var info = new FileInfo(Path.Combine(dir, f.name));
        return $"{f.name} {info.Length} {info.LastWriteTimeUtc.Ticks} {f.sha256}";
    }

    static Dictionary<string, string> ReadStamp(string path)
    {
        var stamp = new Dictionary<string, string>();
        if (File.Exists(path))
            foreach (var line in File.ReadAllLines(path))
                stamp[line.Split(' ')[0]] = line;
        return stamp;
    }
}

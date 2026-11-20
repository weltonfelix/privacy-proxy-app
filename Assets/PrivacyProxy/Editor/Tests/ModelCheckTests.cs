using System;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

// Window > General > Test Runner > EditMode. Uses small fake model files in a temp folder.
public class ModelCheckTests
{
    static readonly byte[] Content = { 1, 2, 3, 4, 5, 6, 7, 8 };
    string _dir, _stamp;
    ModelCheck.Entry[] _files;

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ModelCheckTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _stamp = Path.Combine(_dir, "verified.txt");
        string path = Path.Combine(_dir, "model.gguf");
        File.WriteAllBytes(path, Content);
        _files = new[] { new ModelCheck.Entry { name = "model.gguf", size = Content.Length, sha256 = ModelCheck.Sha256(path) } };
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_dir, true);

    [Test]
    public void MissingFileIsNamed()
    {
        File.Delete(Path.Combine(_dir, "model.gguf"));
        StringAssert.Contains("Falta o arquivo model.gguf", ModelCheck.CheckSizes(_dir, _files));
    }

    [Test]
    public void TruncatedFileFailsSizeCheck()
    {
        File.WriteAllBytes(Path.Combine(_dir, "model.gguf"), new byte[] { 1, 2, 3, 4 }); // cut in half
        StringAssert.Contains("incompleto", ModelCheck.CheckSizes(_dir, _files));
    }

    [Test]
    public void CorruptedFileFailsHashOnFirstOpen()
    {
        var bad = (byte[])Content.Clone();
        bad[3] ^= 0xFF; // same size, one byte changed
        File.WriteAllBytes(Path.Combine(_dir, "model.gguf"), bad);
        Assert.IsNull(ModelCheck.CheckSizes(_dir, _files));
        Assert.AreEqual(1, ModelCheck.Unverified(_dir, _files, _stamp).Length);
        StringAssert.Contains("corrompido", ModelCheck.CheckHashes(_dir, _files, _stamp));
        Assert.AreEqual(1, ModelCheck.Unverified(_dir, _files, _stamp).Length, "a bad file must not be stamped");
    }

    [Test]
    public void GoodFileIsHashedOnlyOnce()
    {
        Assert.AreEqual(1, ModelCheck.Unverified(_dir, _files, _stamp).Length);
        Assert.IsNull(ModelCheck.CheckHashes(_dir, _files, _stamp));
        Assert.AreEqual(0, ModelCheck.Unverified(_dir, _files, _stamp).Length);
    }

    [Test]
    public void StampWriteFailureDoesNotFailGoodFiles()
    {
        Directory.CreateDirectory(_stamp); // a folder where the stamp file should go: the write throws
        Assert.IsNull(ModelCheck.CheckHashes(_dir, _files, _stamp));
    }

    [Test]
    public void RecopiedFileIsHashedAgain()
    {
        ModelCheck.CheckHashes(_dir, _files, _stamp);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "model.gguf"), DateTime.UtcNow.AddMinutes(1));
        Assert.AreEqual(1, ModelCheck.Unverified(_dir, _files, _stamp).Length);
    }

    [Test]
    public void ShippedListHasBothQwenFiles()
    {
        var files = ModelCheck.Parse(Resources.Load<TextAsset>("qwen_models").text).files;
        Assert.AreEqual(2, files.Length);
        foreach (var f in files)
        {
            Assert.Greater(f.size, 0, f.name);
            Assert.IsTrue(Regex.IsMatch(f.sha256, "^[0-9a-f]{64}$"), f.name);
        }
        // The app loads files[0] as the model and files[1] as the mmproj
        StringAssert.DoesNotStartWith("mmproj", files[0].name);
        StringAssert.StartsWith("mmproj", files[1].name);
    }

    [Test]
    public void NoModelWeightsInsideAssets() // they would end up in the APK
    {
        Assert.IsEmpty(Directory.GetFiles(Application.dataPath, "*.gguf", SearchOption.AllDirectories));
    }
}

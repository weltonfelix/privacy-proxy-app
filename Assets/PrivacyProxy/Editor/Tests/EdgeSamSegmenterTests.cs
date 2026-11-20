using System.Collections;
using System.Threading.Tasks;
using NUnit.Framework;
using Unity.InferenceEngine;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

// Runs the real EdgeSAM models on the CPU. Window > General > Test Runner > EditMode.
public class EdgeSamSegmenterTests
{
    const string Dir = "Assets/PrivacyProxy/";
    static readonly Rect WheelBox = Rect.MinMaxRect(425, 600, 700, 875); // the PoC's box on truck.jpg
    static readonly Rect OtherWheelBox = Rect.MinMaxRect(1375, 550, 1650, 800);

    static EdgeSamSegmenter NewSegmenter() => new(
        AssetDatabase.LoadAssetAtPath<ModelAsset>(Dir + "Models/edge_sam_3x_encoder.onnx"),
        AssetDatabase.LoadAssetAtPath<ModelAsset>(Dir + "Models/edge_sam_3x_decoder.onnx"));

    static Texture2D Load(string name)
    {
        var tex = new Texture2D(2, 2);
        tex.LoadImage(AssetDatabase.LoadAssetAtPath<TextAsset>(Dir + "TestData/" + name).bytes);
        return tex;
    }

    static IEnumerator Wait(Task task)
    {
        while (!task.IsCompleted)
            yield return null;
    }

    [UnityTest]
    public IEnumerator TruckMaskMatchesPocAndNewBoxSkipsEncoder()
    {
        var photo = Load("truck.jpg.bytes");
        var reference = Load("wheel.png.bytes").GetPixels32();
        var sam = NewSegmenter();
        try
        {
            var read = sam.SetPhotoAsync(photo.GetPixels32(), photo.width, photo.height);
            yield return Wait(read);
            Assert.IsTrue(read.Result.Ok, read.Result.Error);

            var wheel = sam.SegmentAsync(WheelBox);
            yield return Wait(wheel);
            Assert.IsTrue(wheel.Result.Ok, wheel.Result.Error);
            Assert.AreEqual(photo.width * photo.height, wheel.Result.Mask.Length);
            int inter = 0, union = 0;
            for (int i = 0; i < reference.Length; i++)
            {
                bool m = wheel.Result.Mask[i], r = reference[i].r > 0;
                if (m && r) inter++;
                if (m || r) union++;
            }
            float iou = (float)inter / union;
            Debug.Log($"EdgeSAM test: IoU vs PoC {iou:F4}, encoder {read.Result.Seconds:F3} s, decoder {wheel.Result.Seconds:F3} s");
            Assert.GreaterOrEqual(iou, 0.98f);

            var other = sam.SegmentAsync(OtherWheelBox);
            yield return Wait(other);
            Assert.IsTrue(other.Result.Ok, other.Result.Error);
            Assert.AreEqual(1, sam.EncoderRuns, "changing only the box ran the encoder again");
        }
        finally
        {
            sam.Dispose();
        }
    }

    [UnityTest]
    public IEnumerator BoxBeforePhotoOrOutsidePhotoIsAnError()
    {
        var sam = NewSegmenter();
        try
        {
            var early = sam.SegmentAsync(WheelBox);
            yield return Wait(early);
            Assert.IsFalse(early.Result.Ok);

            var read = sam.SetPhotoAsync(new Color32[4 * 4], 4, 4);
            var outside = sam.SegmentAsync(Rect.MinMaxRect(0, 0, 10, 10)); // waits for the read
            yield return Wait(outside);
            Assert.IsTrue(read.Result.Ok, read.Result.Error);
            Assert.IsFalse(outside.Result.Ok);

            var nan = sam.SegmentAsync(Rect.MinMaxRect(0, 0, float.NaN, 2));
            yield return Wait(nan);
            Assert.IsFalse(nan.Result.Ok, "a NaN box passed the bounds check");
        }
        finally
        {
            sam.Dispose();
        }
    }

    [Test]
    public void WrongSizePhotoIsAnError()
    {
        var sam = NewSegmenter();
        var r = sam.SetPhotoAsync(new Color32[3], 2, 2).Result; // fails before any model work
        sam.Dispose();
        Assert.IsFalse(r.Ok);
    }

    [Test]
    public void EmptyOrWrongSizeMaskIsAnError()
    {
        var empty = EdgeSamSegmenter.MaskFromLowRes(new float[256 * 256], 10, 10); // all 0 = background
        Assert.AreEqual(EdgeSamSegmenter.EmptyMaskError, empty.Error);
        Assert.IsFalse(EdgeSamSegmenter.MaskFromLowRes(new float[100], 10, 10).Ok);

        var full = new float[256 * 256];
        for (int i = 0; i < full.Length; i++)
            full[i] = 1;
        var ok = EdgeSamSegmenter.MaskFromLowRes(full, 10, 6);
        Assert.IsTrue(ok.Ok, ok.Error);
        Assert.AreEqual(60, ok.Mask.Length);
    }

    [UnityTest]
    public IEnumerator CallsAfterDisposeFailCleanly()
    {
        var photo = Load("truck.jpg.bytes");
        var sam = NewSegmenter();
        var read = sam.SetPhotoAsync(photo.GetPixels32(), photo.width, photo.height);
        sam.Dispose(); // models are freed only after the read ends
        LogAssert.Expect(LogType.Log, "EdgeSAM: modelos liberados");
        var box = sam.SegmentAsync(WheelBox);
        yield return Wait(box);
        Assert.IsTrue(read.IsCompleted);
        Assert.IsFalse(read.Result.Ok, "a read cut short by Dispose must not report success");
        Assert.IsFalse(box.Result.Ok);
        Assert.AreEqual(0, sam.EncoderRuns, "the encoder output was kept after Dispose");
    }
}

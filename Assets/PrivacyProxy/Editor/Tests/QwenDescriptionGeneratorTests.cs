using System;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

// Runs against the Editor fake Qwen (QwenNative), which takes 15 s unless cancelled.
public class QwenDescriptionGeneratorTests
{
    static readonly Color32 Top = new Color32(10, 20, 30, 255), Bottom = new Color32(40, 50, 60, 255);

    // 1 wide, 2 tall. GetPixels32 order: the bottom row comes first.
    static ObjectCrop.Result TwoRowCrop() =>
        ObjectCrop.Create(new[] { Bottom, Top }, new[] { true, true }, 1, 2);

    [Test]
    public void ConvertsToTopDownRgb()
    {
        CollectionAssert.AreEqual(new byte[] { 10, 20, 30, 40, 50, 60 }, QwenDescriptionGenerator.ToRgb(TwoRowCrop()));
    }

    [Test]
    public void ConvertsEveryRowLeftToRight()
    {
        // Bottom row: a, b. Top row: c, d. Expected: c, d, a, b.
        Color32 a = new Color32(1, 1, 1, 255), b = new Color32(2, 2, 2, 255), c = new Color32(3, 3, 3, 255), d = new Color32(4, 4, 4, 255);
        var crop = ObjectCrop.Create(new[] { a, b, c, d }, new[] { true, true, true, true }, 2, 2);
        CollectionAssert.AreEqual(new byte[] { 3, 3, 3, 4, 4, 4, 1, 1, 1, 2, 2, 2 }, QwenDescriptionGenerator.ToRgb(crop));
    }

    [Test]
    public void CancelledTokenStopsTheRunningDescribe()
    {
        using var generator = new QwenDescriptionGenerator();
        Assert.IsNull(generator.LoadAsync("", false).Result);
        using var source = new CancellationTokenSource();
        var describe = generator.DescribeAsync(TwoRowCrop(), source.Token);
        Thread.Sleep(200); // let the describe start, it clears old cancel requests
        source.Cancel();
        // Only QwenClient.Cancel can end the fake before its 15 s.
        var error = Assert.Throws<AggregateException>(() => describe.Wait(2000), "cancel did not reach Qwen");
        Assert.IsInstanceOf<OperationCanceledException>(error.InnerException);
    }

    [Test]
    public void AlreadyCancelledTokenNeverStartsTheDescribe()
    {
        using var generator = new QwenDescriptionGenerator();
        using var source = new CancellationTokenSource();
        source.Cancel();
        var describe = generator.DescribeAsync(TwoRowCrop(), source.Token);
        Assert.IsTrue(describe.IsCanceled);
    }

    [Test]
    public void ReturnsTheTextOnSuccess()
    {
        var result = new QwenClient.Result(QwenNative.Ok, "Circuit board", "", 1);
        Assert.AreEqual("Circuit board", QwenDescriptionGenerator.TextOrThrow(result, CancellationToken.None));
    }

    [TestCase("")]
    [TestCase("  \n")]
    public void EmptyTextIsAnError(string text)
    {
        var result = new QwenClient.Result(QwenNative.Ok, text, "", 1);
        Assert.Throws<InvalidOperationException>(() => QwenDescriptionGenerator.TextOrThrow(result, CancellationToken.None));
    }

    [Test]
    public void QwenErrorKeepsItsMessage()
    {
        var result = new QwenClient.Result(QwenNative.Error, "", "could not encode the image", 1);
        var error = Assert.Throws<InvalidOperationException>(() => QwenDescriptionGenerator.TextOrThrow(result, CancellationToken.None));
        Assert.AreEqual("could not encode the image", error.Message);
    }

    [Test]
    public void CancelledCodeBecomesCancellation()
    {
        var result = new QwenClient.Result(QwenNative.Cancelled, "partial", "", 1);
        Assert.Throws<OperationCanceledException>(() => QwenDescriptionGenerator.TextOrThrow(result, CancellationToken.None));
    }
}

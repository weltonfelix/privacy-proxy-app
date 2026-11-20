using System.Threading;
using NUnit.Framework;

// Runs against the Editor fake Qwen (QwenNative). Window > General > Test Runner > EditMode.
public class QwenClientTests
{
    static readonly byte[] Pixel = new byte[3];

    [Test]
    public void CancelStopsRunningDescribe()
    {
        using var qwen = new QwenClient();
        Assert.AreEqual(QwenNative.Ok, qwen.LoadAsync("", "").Result.Code);
        var describe = qwen.DescribeAsync(Pixel, 1, 1, "x");
        Thread.Sleep(200); // let the describe start, it clears old cancel requests
        qwen.Cancel();
        Assert.IsTrue(describe.Wait(2000), "cancel did not stop the describe");
        Assert.AreEqual(QwenNative.Cancelled, describe.Result.Code);
    }

    [Test]
    public void CancelSkipsQueuedDescribe()
    {
        using var qwen = new QwenClient();
        qwen.LoadAsync("", "").Wait();
        var first = qwen.DescribeAsync(Pixel, 1, 1, "x");
        var queued = qwen.DescribeAsync(Pixel, 1, 1, "x"); // starts only after the first ends
        Thread.Sleep(200);
        qwen.Cancel();
        Assert.IsTrue(queued.Wait(2000), "the queued describe ran instead of being skipped");
        Assert.AreEqual(QwenNative.Cancelled, first.Result.Code);
        Assert.AreEqual(QwenNative.Cancelled, queued.Result.Code);
    }

    [Test]
    public void RejectsImageSmallerThanSize()
    {
        using var qwen = new QwenClient();
        var r = qwen.DescribeAsync(Pixel, 2, 2, "x").Result; // 3 bytes for a 2x2 image
        Assert.AreEqual(QwenNative.Error, r.Code);
    }

    [Test]
    public void DisposeFreesOnlyAfterRunningDescribe()
    {
        var qwen = new QwenClient();
        qwen.LoadAsync("", "").Wait();
        var describe = qwen.DescribeAsync(Pixel, 1, 1, "x");
        Thread.Sleep(200);
        qwen.Dispose();
        Assert.IsTrue(describe.Wait(2000), "dispose did not stop the describe");
        // Error here would mean the model was freed while the describe was still running
        Assert.AreEqual(QwenNative.Cancelled, describe.Result.Code, describe.Result.Error);
    }
}

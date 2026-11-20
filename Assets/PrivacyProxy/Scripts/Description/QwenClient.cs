using System;
using System.Threading.Tasks;

// Runs the blocking QwenNative calls on a background thread, one after the other, so the
// frame never waits on the model. Awaiting from a MonoBehaviour resumes on Unity's main thread.
public sealed class QwenClient : IDisposable
{
    public readonly struct Result
    {
        public readonly int Code; // QwenNative.Ok, Error or Cancelled
        public readonly string Text, Error;
        public readonly double Seconds;

        public Result(int code, string text, string error, double seconds)
        {
            Code = code;
            Text = text;
            Error = error;
            Seconds = seconds;
        }
    }

    // One queue for the whole app: the native model is global, so a new client's load must wait
    // for an old client's free. Only touched from the main thread.
    static Task s_last = Task.CompletedTask;
    volatile bool _disposed;
    int _lastRun;
    volatile int _cancelledRun; // describes with run <= this are skipped or stopped

    public Task<Result> LoadAsync(string modelPath, string mmprojPath) => Enqueue(() =>
    {
        var start = DateTime.UtcNow;
        int code = QwenNative.Load(modelPath, mmprojPath);
        return new Result(code, "", code == QwenNative.Error ? QwenNative.LastError() : "", (DateTime.UtcNow - start).TotalSeconds);
    });

    public Task<Result> DescribeAsync(byte[] rgb, int width, int height, string prompt)
    {
        // the native side reads width*height*3 bytes and cannot check the array length itself
        if (rgb == null || width <= 0 || height <= 0 || rgb.Length < (long)width * height * 3)
            return Task.FromResult(new Result(QwenNative.Error, "",
                $"rgb has {rgb?.Length ?? 0} bytes, expected {width}x{height}x3", 0));
        int run = ++_lastRun;
        return Enqueue(() =>
        {
            // shortcut: a cancel landing between this check and the native start is missed; add a native run id if it shows up
            if (_disposed || run <= _cancelledRun)
                return new Result(QwenNative.Cancelled, "", "", 0);
            int code = QwenNative.Describe(rgb, width, height, prompt, out var text, out var seconds);
            return new Result(code, text, code == QwenNative.Error ? QwenNative.LastError() : "", seconds);
        });
    }

    // Stops the running describe and skips the ones still queued. Call from the main thread.
    public void Cancel()
    {
        _cancelledRun = _lastRun;
        QwenNative.Cancel();
    }

    // Frees the model only after the running call has finished, never during it.
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        s_last = s_last.ContinueWith(_ => QwenNative.Free(), TaskScheduler.Default);
    }

    Task<Result> Enqueue(Func<Result> call)
    {
        if (_disposed)
            return Task.FromResult(new Result(QwenNative.Error, "", "QwenClient was disposed", 0));
        var task = s_last.ContinueWith(_ => call(), TaskScheduler.Default);
        s_last = task;
        return task;
    }
}

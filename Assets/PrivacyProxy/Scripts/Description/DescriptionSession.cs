using System;
using System.Threading;
using System.Threading.Tasks;

public interface IDescriptionGenerator
{
    Task<string> DescribeAsync(ObjectCrop.Result crop, CancellationToken cancellationToken);
}

// A test double only. Fifteen seconds is a configured wait, not a Qwen benchmark.
public sealed class SimulatedDescriptionGenerator : IDescriptionGenerator
{
    public TimeSpan Delay { get; }

    public SimulatedDescriptionGenerator() : this(TimeSpan.FromSeconds(15)) { }

    public SimulatedDescriptionGenerator(TimeSpan delay)
    {
        if (delay < TimeSpan.Zero || delay.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(delay));
        Delay = delay;
    }

    public async Task<string> DescribeAsync(ObjectCrop.Result crop, CancellationToken cancellationToken)
    {
        if (crop == null) throw new ArgumentNullException(nameof(crop));
        await Task.Delay(Delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return "Electronic circuit board";
    }
}

// Owned by Unity's main thread. Awaiting preserves its synchronization context.
// Photo AND request IDs reject stale responses, even if a backend ignores cancel.
public sealed class DescriptionSession : IDisposable
{
    public enum Status { NoPhoto, Ready, Generating, Candidate, Cancelled, Failed, Disposed }
    public enum Outcome { Accepted, Discarded, Failed }

    readonly IDescriptionGenerator generator;
    CancellationTokenSource pending;
    long requestId;

    public long PhotoId { get; private set; }
    public Status State { get; private set; } = Status.NoPhoto;
    public string Candidate { get; private set; } = "";
    public string Error { get; private set; } = "";
    public bool IsGenerating => State == Status.Generating;
    public event Action Changed;

    public DescriptionSession(IDescriptionGenerator generator)
    {
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    public void NewPhoto()
    {
        ThrowIfDisposed();
        Invalidate();
        PhotoId++;
        Candidate = Error = "";
        State = Status.Ready;
        Changed?.Invoke();
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        Invalidate();
        Candidate = Error = "";
        State = PhotoId == 0 ? Status.NoPhoto : Status.Cancelled;
        Changed?.Invoke();
    }

    public async Task<Outcome> GenerateAsync(ObjectCrop.Result crop)
    {
        ThrowIfDisposed();
        if (PhotoId == 0) throw new InvalidOperationException("Capture a photo before generating.");
        if (crop == null) throw new ArgumentNullException(nameof(crop));
        if (IsGenerating) throw new InvalidOperationException("A description is already being generated.");

        var source = new CancellationTokenSource();
        pending = source;
        long photo = PhotoId, request = ++requestId;
        Candidate = Error = "";
        State = Status.Generating;
        try
        {
            Changed?.Invoke();
            string text = await generator.DescribeAsync(crop, source.Token);
            if (!IsCurrent(photo, request, source)) return Outcome.Discarded;
            source.Token.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("The description is empty.");
            Candidate = text.Trim();
            State = Status.Candidate;
            Changed?.Invoke();
            return Outcome.Accepted;
        }
        catch (Exception error)
        {
            // Includes late failures/cancellation from a superseded request.
            if (!IsCurrent(photo, request, source)) return Outcome.Discarded;
            Error = error.Message;
            State = Status.Failed;
            Changed?.Invoke();
            return Outcome.Failed;
        }
        finally
        {
            if (ReferenceEquals(pending, source)) pending = null;
            // The request owns disposal; Cancel must not dispose a token still in use.
            source.Dispose();
        }
    }

    bool IsCurrent(long photo, long request, CancellationTokenSource source) =>
        State != Status.Disposed && PhotoId == photo && requestId == request && ReferenceEquals(pending, source);

    void Invalidate()
    {
        requestId++;
        var previous = pending;
        pending = null;
        previous?.Cancel();
    }

    public void Dispose()
    {
        if (State == Status.Disposed) return;
        Invalidate();
        Candidate = Error = "";
        State = Status.Disposed;
        Changed?.Invoke();
    }

    void ThrowIfDisposed()
    {
        if (State == Status.Disposed) throw new ObjectDisposedException(nameof(DescriptionSession));
    }
}

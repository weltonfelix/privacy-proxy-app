using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;

public class DescriptionSessionTests
{
    // Deliberately ignores cancellation to test backends that finish too late.
    sealed class ControlledGenerator : IDescriptionGenerator
    {
        public readonly List<TaskCompletionSource<string>> Calls = new List<TaskCompletionSource<string>>();
        public readonly List<CancellationToken> Tokens = new List<CancellationToken>();

        public Task<string> DescribeAsync(ObjectCrop.Result crop, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<string>();
            Calls.Add(completion);
            Tokens.Add(cancellationToken);
            return completion.Task;
        }
    }

    static ObjectCrop.Result Crop() => ObjectCrop.Create(new[] { new Color32(1, 2, 3, 255) }, new[] { true }, 1, 1);

    [Test]
    public async Task SuccessfulSimulationProducesCandidateAndStateChanges()
    {
        using var session = new DescriptionSession(new SimulatedDescriptionGenerator(TimeSpan.Zero));
        var states = new List<DescriptionSession.Status>();
        session.Changed += () => states.Add(session.State);
        session.NewPhoto();
        Assert.AreEqual(DescriptionSession.Outcome.Accepted, await session.GenerateAsync(Crop()));
        Assert.AreEqual("Electronic circuit board", session.Candidate);
        CollectionAssert.AreEqual(new[] { DescriptionSession.Status.Ready, DescriptionSession.Status.Generating,
            DescriptionSession.Status.Candidate }, states);
        session.NewPhoto();
        Assert.AreEqual(2, session.PhotoId);
        Assert.AreEqual("", session.Candidate);
    }

    [Test]
    public async Task DefaultSimulatorWaitIsFifteenSecondsAndCancellationStopsIt()
    {
        var generator = new SimulatedDescriptionGenerator();
        Assert.AreEqual(TimeSpan.FromSeconds(15), generator.Delay);
        using var cancellation = new CancellationTokenSource();
        var pending = generator.DescribeAsync(Crop(), cancellation.Token);
        Assert.IsFalse(pending.IsCompleted, "The simulated delay blocked or returned immediately.");
        cancellation.Cancel();
        try
        {
            await pending;
            Assert.Fail("Cancellation was ignored by the simulator.");
        }
        catch (OperationCanceledException) { }
    }

    [Test]
    public async Task NewPhotoDiscardsOldResponseEvenAfterNewResponseWasAccepted()
    {
        var generator = new ControlledGenerator();
        using var session = new DescriptionSession(generator);
        session.NewPhoto();
        var old = session.GenerateAsync(Crop());
        session.NewPhoto();
        Assert.IsTrue(generator.Tokens[0].IsCancellationRequested);
        Assert.AreEqual("", session.Candidate);
        var current = session.GenerateAsync(Crop());
        generator.Calls[1].SetResult("New category");
        Assert.AreEqual(DescriptionSession.Outcome.Accepted, await current);
        generator.Calls[0].SetResult("Old category");
        Assert.AreEqual(DescriptionSession.Outcome.Discarded, await old);
        Assert.AreEqual("New category", session.Candidate);
        Assert.AreEqual(DescriptionSession.Status.Candidate, session.State);
    }

    [Test]
    public async Task CancelDiscardsLateSuccessAndClearsTheCandidate()
    {
        var generator = new ControlledGenerator();
        using var session = new DescriptionSession(generator);
        session.NewPhoto();
        var pending = session.GenerateAsync(Crop());
        session.Cancel();
        Assert.IsFalse(session.IsGenerating);
        Assert.IsTrue(generator.Tokens[0].IsCancellationRequested);
        generator.Calls[0].SetResult("Late result");
        Assert.AreEqual(DescriptionSession.Outcome.Discarded, await pending);
        Assert.AreEqual("", session.Candidate);
        Assert.AreEqual(DescriptionSession.Status.Cancelled, session.State);
    }

    [Test]
    public async Task RetryOnSamePhotoUsesRequestIdentityToRejectOldResponse()
    {
        var generator = new ControlledGenerator();
        using var session = new DescriptionSession(generator);
        session.NewPhoto();
        var old = session.GenerateAsync(Crop());
        session.Cancel();
        var current = session.GenerateAsync(Crop());
        generator.Calls[0].SetResult("Old request on same photo");
        Assert.AreEqual(DescriptionSession.Outcome.Discarded, await old);
        Assert.IsTrue(session.IsGenerating);
        Assert.AreEqual("", session.Candidate);
        generator.Calls[1].SetResult("Current request");
        Assert.AreEqual(DescriptionSession.Outcome.Accepted, await current);
        Assert.AreEqual("Current request", session.Candidate);
        Assert.AreEqual(1, session.PhotoId);
    }

    [Test]
    public async Task LateFailureDoesNotReplaceTheCurrentPhotoState()
    {
        var generator = new ControlledGenerator();
        using var session = new DescriptionSession(generator);
        session.NewPhoto();
        var old = session.GenerateAsync(Crop());
        session.NewPhoto();
        generator.Calls[0].SetException(new InvalidOperationException("Old failure"));
        Assert.AreEqual(DescriptionSession.Outcome.Discarded, await old);
        Assert.AreEqual(DescriptionSession.Status.Ready, session.State);
        Assert.AreEqual("", session.Error);
    }

    [Test]
    public async Task CurrentFailureAndEmptyDescriptionAllowRetryWithoutCandidate()
    {
        var generator = new ControlledGenerator();
        using var session = new DescriptionSession(generator);
        session.NewPhoto();
        var failed = session.GenerateAsync(Crop());
        generator.Calls[0].SetException(new InvalidOperationException("Example failure"));
        Assert.AreEqual(DescriptionSession.Outcome.Failed, await failed);
        Assert.AreEqual("Example failure", session.Error);
        Assert.IsFalse(session.IsGenerating);
        var empty = session.GenerateAsync(Crop());
        generator.Calls[1].SetResult("   ");
        Assert.AreEqual(DescriptionSession.Outcome.Failed, await empty);
        Assert.AreEqual("", session.Candidate);
        Assert.AreEqual(DescriptionSession.Status.Failed, session.State);
    }

    [Test]
    public async Task DuplicateGenerateCannotStartAnotherCall()
    {
        var generator = new ControlledGenerator();
        using var session = new DescriptionSession(generator);
        session.NewPhoto();
        var pending = session.GenerateAsync(Crop());
        Assert.ThrowsAsync<InvalidOperationException>(async () => await session.GenerateAsync(Crop()));
        Assert.AreEqual(1, generator.Calls.Count);
        session.Cancel();
        generator.Calls[0].SetResult("Discarded");
        Assert.AreEqual(DescriptionSession.Outcome.Discarded, await pending);
    }

    [Test]
    public async Task DisposeCancelsAndRejectsLateResult()
    {
        var generator = new ControlledGenerator();
        var session = new DescriptionSession(generator);
        session.NewPhoto();
        var pending = session.GenerateAsync(Crop());
        session.Dispose();
        session.Dispose();
        Assert.IsTrue(generator.Tokens[0].IsCancellationRequested);
        generator.Calls[0].SetResult("Late disposed result");
        Assert.AreEqual(DescriptionSession.Outcome.Discarded, await pending);
        Assert.AreEqual(DescriptionSession.Status.Disposed, session.State);
        Assert.AreEqual("", session.Candidate);
        Assert.Throws<ObjectDisposedException>(() => session.NewPhoto());
    }

    [Test]
    public void RequiresPhotoAndCropBeforeGenerating()
    {
        using var session = new DescriptionSession(new SimulatedDescriptionGenerator(TimeSpan.Zero));
        Assert.ThrowsAsync<InvalidOperationException>(async () => await session.GenerateAsync(Crop()));
        session.NewPhoto();
        Assert.ThrowsAsync<ArgumentNullException>(async () => await session.GenerateAsync(null));
    }
}

using AiFramework.Application.Abstractions;
using AiFramework.Application.Maintenance;
using AiFramework.Application.Orders;
using AiFramework.Infrastructure.Jobs;
using FluentAssertions;
using NSubstitute;
using Wolverine;

namespace AiFramework.Infrastructure.Tests.Jobs;

/// <summary>
/// The middleware's own contract, away from a host. Every test here pins something the generated
/// adapter cannot check and that a reasonable-looking edit would break.
/// </summary>
public sealed class JobRunMiddlewareTests
{
    private readonly IJobRunRecorder _recorder = Substitute.For<IJobRunRecorder>();

    private static Envelope AnEnvelope(object message, int attempts = 1) =>
        new() { Id = Guid.NewGuid(), Message = message, Attempts = attempts };

    [Fact]
    public async Task OnExceptionAsync_RethrowsTheSameException()
    {
        var envelope = AnEnvelope(new SendOrderConfirmation(Guid.NewGuid(), "SKU-1", 1));
        var thrown = new InvalidOperationException("the handler gave up");

        var act = async () => await JobRunMiddleware.OnExceptionAsync(
            thrown, envelope, _recorder, CancellationToken.None);

        // THE test of this file. Wolverine's generated catch block emits no rethrow, so without
        // the ExceptionDispatchInfo call at the end of OnExceptionAsync this middleware swallows
        // every job failure - and with it the whole retry and dead-letter policy in
        // JobRegistration (ADR 0016). Jobs would look successful, never retry, and never reach
        // the error queue, with a green build and no warning. Nothing else would catch that.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Should().BeSameAs(thrown, "the original exception must reach Wolverine intact");
    }

    [Fact]
    public async Task OnExceptionAsync_PreservesTheOriginalStackTrace()
    {
        var envelope = AnEnvelope(new SendOrderConfirmation(Guid.NewGuid(), "SKU-1", 1));
        var thrown = Catch(() => throw new InvalidOperationException("deep"));
        var originalStack = thrown.StackTrace;

        var act = async () => await JobRunMiddleware.OnExceptionAsync(
            thrown, envelope, _recorder, CancellationToken.None);

        var rethrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;

        // `throw exception;` would reset this to the rethrow site, which is why CA2200 exists and
        // why ExceptionDispatchInfo is the mechanism. A stack trace that stops at the middleware
        // tells an operator nothing about the handler that actually failed.
        originalStack.Should().NotBeNullOrEmpty();
        rethrown.StackTrace.Should().Contain(originalStack.Trim().Split('\n')[0].Trim());
    }

    [Fact]
    public async Task OnExceptionAsync_RecordsTheFailureBeforeRethrowing()
    {
        var envelope = AnEnvelope(new SendOrderConfirmation(Guid.NewGuid(), "SKU-1", 1), attempts: 3);
        var thrown = new InvalidOperationException("the handler gave up");

        try
        {
            await JobRunMiddleware.OnExceptionAsync(thrown, envelope, _recorder, CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // Expected: the rethrow above is the point of the other tests.
        }

        await _recorder.Received(1).FailedAsync(
            envelope.Id,
            3,
            "InvalidOperationException: the handler gave up",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeforeAsync_RecordsTheLaneFromTheRegistration()
    {
        var envelope = AnEnvelope(new BuildOrderExport(Guid.NewGuid(), Guid.NewGuid()));

        await JobRunMiddleware.BeforeAsync(envelope, _recorder, CancellationToken.None);

        await _recorder.Received(1).StartedAsync(
            Arg.Is<JobRunAttempt>(a =>
                a.JobName == nameof(BuildOrderExport) && a.Lane == JobLane.Heavy),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeforeAsync_RecordsTheOwnerOfAUserScopedJob()
    {
        var ownerId = Guid.NewGuid();
        var envelope = AnEnvelope(new BuildOrderExport(Guid.NewGuid(), ownerId));

        await JobRunMiddleware.BeforeAsync(envelope, _recorder, CancellationToken.None);

        await _recorder.Received(1).StartedAsync(
            Arg.Is<JobRunAttempt>(a => a.OwnerId == ownerId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeforeAsync_LeavesTheOwnerNullForAJobThatCarriesNone()
    {
        var envelope = AnEnvelope(new PruneProcessedOutbox());

        await JobRunMiddleware.BeforeAsync(envelope, _recorder, CancellationToken.None);

        await _recorder.Received(1).StartedAsync(
            Arg.Is<JobRunAttempt>(a => a.OwnerId == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BeforeAsync_ReportsAtLeastAttemptOne()
    {
        // Wolverine leaves Attempts at 0 on an envelope that has not been through a retry, and a
        // run labelled "attempt 0" reads as a bug in the page rather than as the first try.
        var envelope = AnEnvelope(new PruneProcessedOutbox(), attempts: 0);

        await JobRunMiddleware.BeforeAsync(envelope, _recorder, CancellationToken.None);

        await _recorder.Received(1).StartedAsync(
            Arg.Is<JobRunAttempt>(a => a.Attempt == 1), Arg.Any<CancellationToken>());
    }

    private static InvalidOperationException Catch(Action act)
    {
        try
        {
            act();
        }
        catch (InvalidOperationException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("the action did not throw");
    }
}

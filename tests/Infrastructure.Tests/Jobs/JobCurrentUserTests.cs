using AiFramework.Infrastructure.Jobs;
using FluentAssertions;
using Wolverine;

namespace AiFramework.Infrastructure.Tests.Jobs;

/// <summary>
/// The worker's answer to "who is calling", and the middleware that fills it in.
/// </summary>
/// <remarks>
/// Worth testing directly rather than only through the worker's integration suite: the integration
/// test proves the happy path end to end, but the failure modes here — a second set, a job that is
/// not user-scoped — are invisible at every call site and would otherwise only show up as a job
/// that silently reads nothing.
/// </remarks>
public sealed class JobCurrentUserTests
{
    [Fact]
    public void Id_BeforeAnythingSetsIt_IsNull()
    {
        new JobCurrentUser().Id.Should().BeNull(
            "an unset caller must read as unauthenticated, so an ownership-scoped query returns " +
            "nothing rather than everything");
    }

    [Fact]
    public void Set_MakesTheOwnerTheCurrentUser()
    {
        var ownerId = Guid.NewGuid();
        var currentUser = new JobCurrentUser();

        currentUser.Set(ownerId);

        currentUser.Id.Should().Be(ownerId);
    }

    [Fact]
    public void Set_CalledTwice_Throws()
    {
        var currentUser = new JobCurrentUser();
        currentUser.Set(Guid.NewGuid());

        var act = () => currentUser.Set(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>(
            "a scope serves exactly one job, so a second set means a scope was reused — a wiring " +
            "bug that must not quietly win. ICurrentUser's contract also requires a stable id for " +
            "the life of the scope");
    }

    [Fact]
    public void Middleware_PopulatesTheCallerFromAUserScopedJob()
    {
        var ownerId = Guid.NewGuid();
        var currentUser = new JobCurrentUser();
        var envelope = new Envelope(new UserScopedStub(ownerId));

        JobUserMiddleware.Before(envelope, currentUser);

        currentUser.Id.Should().Be(ownerId);
    }

    [Fact]
    public void Middleware_LeavesTheCallerUnsetForAJobThatIsNotUserScoped()
    {
        var currentUser = new JobCurrentUser();
        var envelope = new Envelope(new NotUserScopedStub());

        JobUserMiddleware.Before(envelope, currentUser);

        currentUser.Id.Should().BeNull(
            "the chain predicate already restricts this middleware to user-scoped jobs, so a " +
            "non-match is nothing to do rather than an error");
    }

    private sealed record UserScopedStub(Guid OwnerId) : Application.Abstractions.IUserScopedJob
    {
        public static Application.Abstractions.JobLane Lane =>
            Application.Abstractions.JobLane.Heavy;
    }

    private sealed record NotUserScopedStub : Application.Abstractions.IJob
    {
        public static Application.Abstractions.JobLane Lane =>
            Application.Abstractions.JobLane.Light;
    }
}

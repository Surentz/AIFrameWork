using AiFramework.Application.Abstractions;
using AiFramework.Application.Maintenance;
using FluentAssertions;
using NSubstitute;

namespace AiFramework.Application.Tests.Maintenance;

public sealed class PruneProcessedOutboxHandlerTests
{
    private readonly IOutboxRetention _retention = Substitute.For<IOutboxRetention>();

    [Fact]
    public async Task Handle_PrunesThroughThePort()
    {
        _retention.PruneProcessedAsync(Arg.Any<CancellationToken>()).Returns(3);

        await new PruneProcessedOutboxHandler(_retention)
            .Handle(new PruneProcessedOutbox(), CancellationToken.None);

        await _retention.Received(1).PruneProcessedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheJob_RunsOnTheLightLane()
    {
        // A single DELETE with an indexed predicate: milliseconds, no CPU to speak of.
        PruneProcessedOutbox.Lane.Should().Be(JobLane.Light);
    }
}

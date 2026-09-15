using AiFramework.Application.Abstractions;
using AiFramework.Infrastructure.Jobs;
using FluentAssertions;

namespace AiFramework.Infrastructure.Tests.Jobs;

/// <summary>
/// Pure logic, tested directly — the same category as <c>CacheDuration.Clamp</c>, which root
/// <c>CLAUDE.md</c> singles out for exactly this treatment.
/// </summary>
/// <remarks>
/// These matter more than their size suggests: <c>Jobs:Queues</c> is the one setting that decides
/// whether a host consumes anything at all, and every failure mode here is silent. A lane nobody
/// listens on is a queue that fills with no error and no log.
/// </remarks>
public sealed class JobOptionsTests
{
    [Fact]
    public void ParseQueues_WhenEmpty_ReturnsNoLanes()
    {
        var options = new JobOptions { Queues = string.Empty };

        options.ParseQueues().Should().BeEmpty(
            "an empty Jobs:Queues is the API's configuration — it publishes and listens to nothing");
    }

    [Theory]
    [InlineData("light", JobLane.Light)]
    [InlineData("LIGHT", JobLane.Light)]
    [InlineData("  heavy  ", JobLane.Heavy)]
    public void ParseQueues_IsCaseInsensitiveAndTrims(string configured, JobLane expected)
    {
        var options = new JobOptions { Queues = configured };

        options.ParseQueues().Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public void ParseQueues_WithBothLanes_ReturnsBoth()
    {
        var options = new JobOptions { Queues = "light,heavy" };

        options.ParseQueues().Should().BeEquivalentTo([JobLane.Light, JobLane.Heavy]);
    }

    [Fact]
    public void ParseQueues_WithARepeatedLane_ReturnsItOnce()
    {
        var options = new JobOptions { Queues = "light,light" };

        options.ParseQueues().Should().ContainSingle(
            "two listeners on one queue would double that lane's effective parallelism against a " +
            "number an operator set deliberately");
    }

    [Fact]
    public void ParseQueues_WithAnUnknownLane_ThrowsNamingIt()
    {
        var options = new JobOptions { Queues = "light,nonsense" };

        var act = () => options.ParseQueues();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*nonsense*", "the message must name the offending value")
            .WithMessage("*Light*", "and list what is actually accepted");
    }

    /// <summary>
    /// The trap <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> sets on its own: it
    /// parses any numeric string, so without an <see cref="Enum.IsDefined{TEnum}(TEnum)"/> check
    /// this would "succeed" with <c>(JobLane)7</c>, pass validation, and kill the host deeper in
    /// with an <c>ArgumentOutOfRangeException</c> naming no configuration key at all.
    /// </summary>
    [Fact]
    public void ParseQueues_WithANumericLane_ThrowsRatherThanFabricatingALane()
    {
        var options = new JobOptions { Queues = "7" };

        var act = () => options.ParseQueues();

        act.Should().Throw<InvalidOperationException>().WithMessage("*7*");
    }

    [Fact]
    public void ParallelismFor_ReturnsTheLanesOwnValue()
    {
        var options = new JobOptions { LightParallelism = 8, HeavyParallelism = 2 };

        options.ParallelismFor(JobLane.Light).Should().Be(8);
        options.ParallelismFor(JobLane.Heavy).Should().Be(2);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(8, 0)]
    [InlineData(-1, 2)]
    public void Validate_WithALaneAtZeroOrLess_Throws(int light, int heavy)
    {
        var options = new JobOptions
        {
            Queues = "light,heavy",
            LightParallelism = light,
            HeavyParallelism = heavy,
        };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>(
            "a lane at 0 registers a listener that consumes nothing, with no error and no log");
    }

    [Fact]
    public void Validate_WithAnUnknownLane_Throws()
    {
        var options = new JobOptions { Queues = "nonsense" };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>().WithMessage("*nonsense*");
    }

    [Fact]
    public void Validate_WithTheWorkersOwnDefaults_Passes()
    {
        // Mirrors src/Worker/appsettings.json. If this ever fails, the shipped defaults are
        // themselves invalid and the worker would not start.
        var options = new JobOptions { Queues = "light,heavy" };

        var act = options.Validate;

        act.Should().NotThrow();
    }
}

using AiFramework.Application.Monitoring;
using AiFramework.Infrastructure.ExternalSystems.Health;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AiFramework.Infrastructure.Tests.ExternalSystems;

public sealed class ExternalSystemStatusPublisherTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    private static HealthReportEntry Entry(
        string system, HealthStatus status, string description, Exception? exception = null,
        IReadOnlyDictionary<string, object>? data = null) =>
        new(status, description, TimeSpan.FromMilliseconds(5), exception, data, [ExternalSystemHealth.Tag, system]);

    private static HealthReport Report(params (string Name, HealthReportEntry Entry)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => e.Entry, StringComparer.Ordinal), TimeSpan.FromMilliseconds(20));

    [Fact]
    public void Summarize_TakesTheWorstCheckOfEachSystem()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner", Entry("Partner", HealthStatus.Healthy, "Partner reachable (401)")),
            ("Partner:certificate", Entry("Partner", HealthStatus.Degraded, "client certificate expires in 10 days"))), At);

        var row = rows.Should().ContainSingle().Subject;
        row.State.Should().Be(ExternalSystemState.Degraded);
        row.Description.Should().Be("client certificate expires in 10 days");
        row.CheckedAt.Should().Be(At);
    }

    [Fact]
    public void Summarize_ReadsNotAfterFromTheCertificateCheck()
    {
        var notAfter = At.AddDays(200);

        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner", Entry("Partner", HealthStatus.Healthy, "ok")),
            ("Partner:certificate", Entry("Partner", HealthStatus.Healthy, "valid",
                data: new Dictionary<string, object>(StringComparer.Ordinal) { [ExternalSystemHealth.CertificateNotAfterKey] = notAfter }))), At);

        rows.Single().CertificateNotAfter.Should().Be(notAfter);
    }

    [Theory]
    [InlineData(HealthStatus.Healthy, true)]
    [InlineData(HealthStatus.Unhealthy, false)]
    public void Summarize_ReportsWhetherTheTokenCheckPassed(HealthStatus token, bool expected)
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner", Entry("Partner", HealthStatus.Healthy, "ok")),
            ("Partner:token", Entry("Partner", token, "token"))), At);

        rows.Single().TokenOk.Should().Be(expected);
    }

    [Fact]
    public void Summarize_ForASystemWithoutATokenCheck_LeavesTokenUnknown()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(
            Report(("Partner", Entry("Partner", HealthStatus.Healthy, "ok"))), At);

        rows.Single().TokenOk.Should().BeNull();
    }

    [Fact]
    public void Summarize_WhenACheckThrew_DescribesTheExceptionTypeOnly()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("Partner:token", Entry("Partner", HealthStatus.Unhealthy,
                "Connection refused (idp.internal:8443)",
                new InvalidOperationException("No TokenEndpoint configured for idp.internal")))), At);

        rows.Single().Description.Should().Be("check failed: InvalidOperationException");
    }

    [Fact]
    public void Summarize_GroupsEachSystemSeparately()
    {
        var rows = ExternalSystemStatusPublisher.Summarize(Report(
            ("A", Entry("A", HealthStatus.Healthy, "ok")),
            ("B", Entry("B", HealthStatus.Unhealthy, "B unreachable: ConnectionError"))), At);

        rows.Select(r => (r.Name, r.State)).Should().BeEquivalentTo(
            [("A", ExternalSystemState.Healthy), ("B", ExternalSystemState.Unhealthy)]);
    }
}

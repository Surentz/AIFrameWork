using AiFramework.Application.Assistant;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant;

public sealed class OperationalTextTests
{
    [Fact]
    public void Scrub_Null_ReturnsNull()
    {
        OperationalText.Scrub(null).Should().BeNull();
    }

    [Theory]
    [InlineData("Mail to jane.doe+ops@example.co.uk failed", "Mail to [email] failed")]
    [InlineData("from 192.168.10.4 and 10.0.0.1", "from [ip] and [ip]")]
    [InlineData("peer 2001:0db8:85a3:0000:0000:8a2e:0370:7334 reset", "peer [ip] reset")]
    [InlineData("peer fe80::1 reset", "peer [ip] reset")]
    [InlineData("bound to ::1 only", "bound to [ip] only")]
    public void Scrub_PersonalPatterns_AreMasked(string input, string expected)
    {
        OperationalText.Scrub(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("Timed out after 00:00:30")]
    [InlineData("Order 3f2504e0-4f89-11d3-9a0c-0305e82c3301 not found")]
    [InlineData("Npgsql.NpgsqlException: connection refused")]
    [InlineData("Version 1.2.3.4.5 is not supported")]
    public void Scrub_OperationalText_IsLeftAlone(string input)
    {
        OperationalText.Scrub(input).Should().Be(input);
    }

    [Fact]
    public void Scrub_LongText_IsTruncatedToMaxLengthWithAnEllipsis()
    {
        var scrubbed = OperationalText.Scrub(new string('x', 2000));

        scrubbed.Should().HaveLength(OperationalText.MaxLength).And.EndWith("…");
    }

    [Fact]
    public void Scrub_TextAtMaxLength_IsUnchanged()
    {
        var text = new string('x', OperationalText.MaxLength);

        OperationalText.Scrub(text).Should().Be(text);
    }
}

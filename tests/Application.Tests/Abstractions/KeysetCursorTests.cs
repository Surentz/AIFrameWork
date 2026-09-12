using AiFramework.Application.Abstractions;
using FluentAssertions;

namespace AiFramework.Application.Tests.Abstractions;

/// <summary>
/// The cursor format is a public API contract and a cache key fragment, shared by GetOrders and
/// GetProducts, so it is tested once here rather than through either.
/// </summary>
public sealed class KeysetCursorTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 9, 1, 12, 34, 56, 789, TimeSpan.Zero);

    [Fact]
    public void Encode_ThenDecode_RoundTrips()
    {
        var id = Guid.NewGuid();

        KeysetCursor.TryDecode(KeysetCursor.Encode(Timestamp, id), out var decoded)
            .Should().BeTrue();

        decoded.Timestamp.Should().Be(Timestamp);
        decoded.Id.Should().Be(id);
    }

    [Fact]
    public void Encode_PreservesSubSecondPrecision()
    {
        // Round-trip ("O") rather than a second-granularity format: two rows created in the same
        // second would otherwise decode to the same keyset and the page boundary would slip.
        var precise = Timestamp.AddTicks(1234);

        KeysetCursor.TryDecode(KeysetCursor.Encode(precise, Guid.NewGuid()), out var decoded)
            .Should().BeTrue();

        decoded.Timestamp.Should().Be(precise);
    }

    [Fact]
    public void Encode_PreservesANonZeroOffset()
    {
        var offset = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.FromHours(5.5));

        KeysetCursor.TryDecode(KeysetCursor.Encode(offset, Guid.NewGuid()), out var decoded)
            .Should().BeTrue();

        decoded.Timestamp.Should().Be(offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!")]
    [InlineData("bm90LWEtY3Vyc29y")]          // base64 of "not-a-cursor" - decodes, no separator
    [InlineData("Zm9vfGJhcg==")]              // base64 of "foo|bar" - two parts, neither parses
    [InlineData("MjAyNi0wOS0wMXxub3QtYS1ndWlk")] // valid-ish date, unparseable guid
    public void TryDecode_WithAMalformedCursor_ReturnsFalse(string cursor)
    {
        KeysetCursor.TryDecode(cursor, out var decoded).Should().BeFalse();

        decoded.Should().Be(default((DateTimeOffset, Guid)));
    }

    [Fact]
    public void TryDecode_WithNull_Throws()
    {
        var act = () => KeysetCursor.TryDecode(null!, out _);

        act.Should().Throw<ArgumentNullException>();
    }
}

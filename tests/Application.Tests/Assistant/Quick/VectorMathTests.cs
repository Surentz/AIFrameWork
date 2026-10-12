using AiFramework.Application.Assistant.Quick;
using FluentAssertions;

namespace AiFramework.Application.Tests.Assistant.Quick;

public sealed class VectorMathTests
{
    [Fact]
    public void Normalize_ScalesToUnitLength()
    {
        var v = VectorMath.Normalize([3f, 4f]);

        v.Should().Equal(0.6f, 0.8f);
    }

    [Fact]
    public void Normalize_LeavesAZeroVectorAlone()
    {
        VectorMath.Normalize([0f, 0f]).Should().Equal(0f, 0f);
    }

    [Fact]
    public void Dot_OfUnitVectors_IsTheirCosine()
    {
        VectorMath.Dot([1f, 0f], [0f, 1f]).Should().Be(0);
        VectorMath.Dot([0.6f, 0.8f], [0.6f, 0.8f]).Should().BeApproximately(1, 1e-6);
    }

    [Fact]
    public void Dot_RefusesVectorsOfDifferentLength()
    {
        var act = () => VectorMath.Dot([1f], [1f, 2f]);

        act.Should().Throw<ArgumentException>();
    }
}

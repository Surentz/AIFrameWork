using AiFramework.Application.Abstractions;
using FluentAssertions;

namespace AiFramework.Application.Tests.Abstractions;

public sealed class ResultTests
{
    [Fact]
    public void Success_WithValue_ExposesTheValue()
    {
        var result = Result.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_WithError_ExposesTheError()
    {
        var error = new Error(ErrorKind.NotFound, "order.not_found", "No such order.");

        var result = Result.Failure<int>(error);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(error);
    }

    [Fact]
    public void Value_OnFailure_Throws()
    {
        var result = Result.Failure<int>(new Error(ErrorKind.Conflict, "c", "m"));

        var act = () => _ = result.Value;

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Error_OnSuccess_Throws()
    {
        var result = Result.Success(1);

        var act = () => _ = result.Error;

        act.Should().Throw<InvalidOperationException>();
    }
}

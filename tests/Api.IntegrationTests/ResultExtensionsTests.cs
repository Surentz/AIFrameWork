using AiFramework.Application.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace AiFramework.Api.IntegrationTests;

/// <summary>
/// Exercises <see cref="ResultExtensions.Problem{T}"/> directly against a bare
/// <see cref="DefaultHttpContext"/> rather than over a live host: the method is a pure mapping
/// from <see cref="Result{T}"/> to an <see cref="ActionResult"/> plus a response header, with no
/// dependency this project's other tests need a WebApplicationFactory for. The 503 branch is
/// additionally proven over real HTTP in <see cref="Diagnostics.ProblemMappingTests"/>, the same
/// two-level split <c>AuthRateLimitTests</c> and this file's neighbours already use for the 429
/// case: a targeted unit-style test for the branches, and one HTTP round trip proving the header
/// actually reaches the wire through the exact code path a controller uses.
/// </summary>
public sealed class ResultExtensionsTests
{
    private static Error MakeError(ErrorKind kind, TimeSpan? retryAfter = null) =>
        new(kind, "test.code", "test message", RetryAfter: retryAfter);

    [Theory]
    [InlineData(ErrorKind.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorKind.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorKind.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorKind.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ErrorKind.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    public void Problem_MapsEachErrorKindToItsStatusCode(ErrorKind kind, int expectedStatus)
    {
        var result = Result.Failure<string>(MakeError(kind));
        var httpContext = new DefaultHttpContext();

        var action = result.Problem(httpContext);

        var objectResult = action.Should().BeOfType<ObjectResult>().Subject;
        objectResult.StatusCode.Should().Be(expectedStatus);
        var problemDetails = objectResult.Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Status.Should().Be(expectedStatus);
    }

    [Fact]
    public void Problem_ForEveryKind_CarriesATraceId()
    {
        var result = Result.Failure<string>(MakeError(ErrorKind.NotFound));
        var httpContext = new DefaultHttpContext { TraceIdentifier = "trace-123" };

        var action = result.Problem(httpContext);

        var problemDetails = ((ObjectResult)action).Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Extensions["traceId"].Should().Be("trace-123");
    }

    [Fact]
    public void Problem_ForANonUnavailableFailure_SetsNoRetryAfterHeader()
    {
        var result = Result.Failure<string>(MakeError(ErrorKind.NotFound));
        var httpContext = new DefaultHttpContext();

        result.Problem(httpContext);

        httpContext.Response.Headers.RetryAfter.Should().BeNullOrEmpty(
            "a client is not being asked to wait on a failure that retrying cannot fix");
    }

    [Fact]
    public void Problem_ForAnUnavailableFailureWithNoRetryAfterSet_FallsBackToTheDefaultFloor()
    {
        var result = Result.Failure<string>(MakeError(ErrorKind.Unavailable));
        var httpContext = new DefaultHttpContext();

        result.Problem(httpContext);

        httpContext.Response.Headers.RetryAfter.ToString().Should().Be("5",
            "a producer that does not know a better number is not exempt from the header - " +
            "the same reasoning AuthRateLimitTests applies to the 429 case");
    }

    [Fact]
    public void Problem_ForAnUnavailableFailureWithARetryAfterSet_UsesIt()
    {
        var result = Result.Failure<string>(
            MakeError(ErrorKind.Unavailable, TimeSpan.FromSeconds(30)));
        var httpContext = new DefaultHttpContext();

        result.Problem(httpContext);

        httpContext.Response.Headers.RetryAfter.ToString().Should().Be("30");
    }

    [Fact]
    public void Problem_ForAnUnavailableFailureWithAFractionalRetryAfter_RoundsUp()
    {
        // Truncating the remaining fraction of a second is exactly how "Retry-After: 0" happens
        // and sends a well-behaved client straight back into another failure - the same
        // reasoning the rate limiter's OnRejected handler carries for the 429 case.
        var result = Result.Failure<string>(
            MakeError(ErrorKind.Unavailable, TimeSpan.FromMilliseconds(2100)));
        var httpContext = new DefaultHttpContext();

        result.Problem(httpContext);

        httpContext.Response.Headers.RetryAfter.ToString().Should().Be("3");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Problem_ForAnUnavailableFailureWithANonPositiveRetryAfter_FallsBackToTheDefaultFloor(
        int seconds)
    {
        var result = Result.Failure<string>(
            MakeError(ErrorKind.Unavailable, TimeSpan.FromSeconds(seconds)));
        var httpContext = new DefaultHttpContext();

        result.Problem(httpContext);

        // A non-positive value is worse than no value at all - "Retry-After: 0" or a negative
        // header - so it is treated the same as a producer that set nothing.
        httpContext.Response.Headers.RetryAfter.ToString().Should().Be("5");
    }

    [Fact]
    public void Problem_WithNoDetails_DoesNotAddAnErrorsExtension()
    {
        var result = Result.Failure<string>(MakeError(ErrorKind.Validation));
        var httpContext = new DefaultHttpContext();

        var action = result.Problem(httpContext);

        var problemDetails = ((ObjectResult)action).Value.Should().BeOfType<ProblemDetails>().Subject;
        problemDetails.Extensions.Should().NotContainKey("errors");
    }

    [Fact]
    public void Problem_WithANullResult_Throws()
    {
        Result<string>? result = null;

        var act = () => result!.Problem(new DefaultHttpContext());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Problem_WithANullHttpContext_Throws()
    {
        var result = Result.Failure<string>(MakeError(ErrorKind.NotFound));

        var act = () => result.Problem(null!);

        act.Should().Throw<ArgumentNullException>();
    }
}

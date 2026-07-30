using System.Net;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// The retry policy is all classification, and a wrong classification is silent: treating a 503 as
/// permanent makes CPMigrate report "no newer version" for a package it never asked about, and
/// treating a 404 as transient wastes seconds per missing package across a large solution.
/// </summary>
public class NuGetRetryPolicyTests
{
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    public void IsTransient_ClassifiesStatusCodes(HttpStatusCode status, bool expected)
    {
        NuGetRetryPolicy.IsTransient(status).Should().Be(expected);
    }

    [Fact]
    public void IsTransient_TransportFailure_IsRetryable()
    {
        NuGetRetryPolicy
            .IsTransient(new HttpRequestException("reset"), cancellationRequested: false)
            .Should()
            .BeTrue();
    }

    [Fact]
    public void IsTransient_TimeoutIsRetryableButRealCancellationIsNot()
    {
        // HttpClient surfaces its own timeout as TaskCanceledException, which is the same type a
        // Ctrl-C produces. Retrying a genuine cancellation would ignore the user.
        var cancelled = new TaskCanceledException();

        NuGetRetryPolicy.IsTransient(cancelled, cancellationRequested: false).Should().BeTrue();
        NuGetRetryPolicy.IsTransient(cancelled, cancellationRequested: true).Should().BeFalse();
    }

    [Fact]
    public void IsTransient_UnrelatedException_IsNotRetryable()
    {
        NuGetRetryPolicy
            .IsTransient(new InvalidOperationException(), cancellationRequested: false)
            .Should()
            .BeFalse();
    }

    [Fact]
    public void GetDelay_GrowsWithEachAttempt()
    {
        var first = NuGetRetryPolicy.GetDelay(1, retryAfter: null, jitter: 1.0);
        var second = NuGetRetryPolicy.GetDelay(2, retryAfter: null, jitter: 1.0);
        var third = NuGetRetryPolicy.GetDelay(3, retryAfter: null, jitter: 1.0);

        second.Should().BeGreaterThan(first);
        third.Should().BeGreaterThan(second);
    }

    [Fact]
    public void GetDelay_IsCapped()
    {
        // A CLI that appears to hang is worse than one that gives up and says so.
        NuGetRetryPolicy
            .GetDelay(20, retryAfter: null, jitter: 1.0)
            .Should()
            .BeLessThanOrEqualTo(NuGetRetryPolicy.MaxDelay);
    }

    [Fact]
    public void GetDelay_JitterSpreadsRetriesWithoutMakingThemImmediate()
    {
        // Decorrelating fifty packages matters, but a retry that arrives instantly is likely to fail
        // again for the same reason.
        var low = NuGetRetryPolicy.GetDelay(3, retryAfter: null, jitter: 0.0);
        var high = NuGetRetryPolicy.GetDelay(3, retryAfter: null, jitter: 0.999);

        low.Should().BeLessThan(high);
        low.Should().BeGreaterThan(TimeSpan.Zero);
        low.Should().BeGreaterThanOrEqualTo(high / 2.5);
    }

    [Fact]
    public void GetDelay_HonoursRetryAfter()
    {
        NuGetRetryPolicy
            .GetDelay(1, TimeSpan.FromSeconds(2), jitter: 0.5)
            .Should()
            .Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void GetDelay_CapsAnExcessiveRetryAfter()
    {
        // nuget.org can advertise a long wait under load; the cap keeps the CLI responsive.
        NuGetRetryPolicy
            .GetDelay(1, TimeSpan.FromMinutes(10), jitter: 0.5)
            .Should()
            .Be(NuGetRetryPolicy.MaxDelay);
    }

    [Fact]
    public void GetDelay_IgnoresANonPositiveRetryAfter()
    {
        NuGetRetryPolicy
            .GetDelay(1, TimeSpan.Zero, jitter: 1.0)
            .Should()
            .BeGreaterThan(TimeSpan.Zero);
    }
}

using System.Net;

namespace CPMigrate.Services;

/// <summary>
/// Decides whether a failed NuGet request is worth retrying, and how long to wait.
///
/// Extracted from the lookup service so the policy can be tested without a network: the interesting
/// cases are all classification decisions, and a wrong one is silent. Treating a 503 as permanent
/// makes CPMigrate report "no newer version" for a package it never managed to ask about; treating a
/// 404 as transient wastes seconds per missing package across a large solution.
/// </summary>
public static class NuGetRetryPolicy
{
    /// <summary>How many times a single lookup is attempted, including the first.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Base delay, doubled per attempt.</summary>
    private static readonly TimeSpan BaseDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Upper bound on a single wait. nuget.org can advertise a long <c>Retry-After</c> under load, and
    /// a CLI that appears to hang is worse than one that gives up and says so.
    /// </summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether a status code represents a failure that may succeed on a retry.
    ///
    /// 404 is deliberately excluded: a package that does not exist will not start existing, and the
    /// caller needs that answer immediately rather than after three waits.
    /// </summary>
    /// <param name="statusCode">The response status.</param>
    public static bool IsTransient(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.RequestTimeout => true,
            HttpStatusCode.TooManyRequests => true,
            >= HttpStatusCode.InternalServerError => true,
            _ => false,
        };
    }

    /// <summary>
    /// Whether an exception represents a transient transport failure.
    ///
    /// A cancelled <see cref="TaskCanceledException"/> from an <see cref="HttpClient"/> timeout is
    /// indistinguishable from user cancellation by type alone, so the caller passes its own token
    /// state to tell them apart — retrying a genuine cancellation would ignore a Ctrl-C.
    /// </summary>
    /// <param name="exception">The exception thrown by the request.</param>
    /// <param name="cancellationRequested">Whether the caller's own cancellation was requested.</param>
    public static bool IsTransient(Exception exception, bool cancellationRequested)
    {
        return exception switch
        {
            HttpRequestException => true,
            TaskCanceledException or OperationCanceledException => !cancellationRequested,
            _ => false,
        };
    }

    /// <summary>
    /// How long to wait before the next attempt: exponential backoff, honouring a server-provided
    /// <c>Retry-After</c> when it is shorter than the cap, and jittered so a solution with fifty
    /// packages does not retry them all in lockstep.
    /// </summary>
    /// <param name="attempt">1-based number of the attempt that just failed.</param>
    /// <param name="retryAfter">A server-provided hint, when present.</param>
    /// <param name="jitter">A value in [0,1) used to spread retries; injected so tests are deterministic.</param>
    public static TimeSpan GetDelay(int attempt, TimeSpan? retryAfter, double jitter)
    {
        if (retryAfter.HasValue && retryAfter.Value > TimeSpan.Zero)
        {
            // The server said when to come back; respect it, but never wait longer than the cap.
            return retryAfter.Value < MaxDelay ? retryAfter.Value : MaxDelay;
        }

        var exponential = BaseDelay * Math.Pow(2, Math.Max(0, attempt - 1));
        var capped = exponential < MaxDelay ? exponential : MaxDelay;

        // Full jitter over [0.5, 1.0] of the computed delay: enough to decorrelate, without making a
        // retry arrive so early that it is likely to fail again for the same reason.
        return capped * (0.5 + (Math.Clamp(jitter, 0, 0.999) / 2));
    }
}

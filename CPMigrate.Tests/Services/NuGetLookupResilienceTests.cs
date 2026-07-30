using System.Net;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// A version lookup that fails returns the same null as a package that is genuinely current, so a
/// transient 503 used to be reported as "up to date" — and a run could silently skip half a
/// solution's updates. These tests pin the retry behaviour, the caching, and the distinction between
/// "no newer version" and "could not ask".
/// </summary>
public class NuGetLookupResilienceTests
{
    [Fact]
    public async Task GetLatestVersion_TransientFailureThenSuccess_Recovers()
    {
        var handler = new ScriptedHandler(
            Respond(HttpStatusCode.ServiceUnavailable),
            RespondWithVersions("1.0.0", "2.0.0")
        );
        using var service = CreateService(handler);

        var latest = await service.GetLatestVersionAsync("Newtonsoft.Json");

        latest!.ToString().Should().Be("2.0.0");
        handler.Requests.Should().Be(2);
        service.GetFailedLookups().Should().BeEmpty("the retry succeeded");
    }

    [Fact]
    public async Task GetLatestVersion_PersistentTransientFailure_IsRecordedNotSilentlySwallowed()
    {
        // The whole point: the caller must be able to tell this apart from a current package.
        var handler = new ScriptedHandler(
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable)
        );
        using var service = CreateService(handler);

        var latest = await service.GetLatestVersionAsync("Newtonsoft.Json");

        latest.Should().BeNull();
        handler.Requests.Should().Be(NuGetRetryPolicy.MaxAttempts);
        service.GetFailedLookups().Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public async Task GetLatestVersion_NotFound_IsDefinitiveAndNotRetried()
    {
        // A package that does not exist will not start existing; three waits would be pure latency.
        var handler = new ScriptedHandler(Respond(HttpStatusCode.NotFound));
        using var service = CreateService(handler);

        var latest = await service.GetLatestVersionAsync("Does.Not.Exist");

        latest.Should().BeNull();
        handler.Requests.Should().Be(1);
        service.GetFailedLookups().Should().BeEmpty("a missing package is an answer, not a failure");
    }

    [Fact]
    public async Task GetLatestVersion_ClientError_IsNotRetried()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.BadRequest));
        using var service = CreateService(handler);

        await service.GetLatestVersionAsync("Newtonsoft.Json");

        handler.Requests.Should().Be(1);
        service.GetFailedLookups().Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public async Task GetLatestVersion_TransportFailure_IsRetried()
    {
        var handler = new ScriptedHandler(
            () => throw new HttpRequestException("connection reset"),
            RespondWithVersions("3.0.0")
        );
        using var service = CreateService(handler);

        var latest = await service.GetLatestVersionAsync("Newtonsoft.Json");

        latest!.ToString().Should().Be("3.0.0");
        handler.Requests.Should().Be(2);
    }

    [Fact]
    public async Task GetLatestVersion_MalformedBody_IsNotRetried()
    {
        // Retrying will not make it parse.
        var handler = new ScriptedHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ not json") }
        );
        using var service = CreateService(handler);

        await service.GetLatestVersionAsync("Newtonsoft.Json");

        handler.Requests.Should().Be(1);
        service.GetFailedLookups().Should().Contain("Newtonsoft.Json");
    }

    [Fact]
    public async Task GetLatestVersion_SamePackageTwice_IssuesOneRequest()
    {
        // A solution referencing one package from thirty projects previously made thirty requests.
        var handler = new ScriptedHandler(RespondWithVersions("1.0.0", "2.0.0"));
        using var service = CreateService(handler);

        await service.GetLatestVersionAsync("Newtonsoft.Json");
        await service.GetLatestVersionAsync("Newtonsoft.Json");
        await service.GetLatestVersionInMajorAsync("Newtonsoft.Json", 1);

        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task GetLatestVersion_CachesTheAbsenceOfAPackageToo()
    {
        var handler = new ScriptedHandler(Respond(HttpStatusCode.NotFound));
        using var service = CreateService(handler);

        await service.GetLatestVersionAsync("Does.Not.Exist");
        await service.GetLatestVersionAsync("Does.Not.Exist");

        handler.Requests.Should().Be(1, "a definitive answer is worth caching");
    }

    [Fact]
    public async Task GetLatestVersion_DoesNotCacheATransientFailure()
    {
        // Caching it would make one bad moment this run's settled view of the package.
        var handler = new ScriptedHandler(
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            RespondWithVersions("5.0.0")
        );
        using var service = CreateService(handler);

        await service.GetLatestVersionAsync("Newtonsoft.Json");
        var second = await service.GetLatestVersionAsync("Newtonsoft.Json");

        second!.ToString().Should().Be("5.0.0");
    }

    [Fact]
    public async Task GetLatestVersion_HonoursRetryAfter()
    {
        var waits = new List<TimeSpan>();
        var response = Respond(HttpStatusCode.TooManyRequests);
        var handler = new ScriptedHandler(
            () =>
            {
                var message = response();
                message.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(2)
                );
                return message;
            },
            RespondWithVersions("1.0.0")
        );

        using var service = new NuGetVersionLookupService(
            new HttpClient(handler),
            logger: null,
            delay: wait =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            jitter: () => 0.5
        );

        await service.GetLatestVersionAsync("Newtonsoft.Json");

        waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task GetLatestVersion_PrereleaseIsExcludedUnlessRequested()
    {
        var handler = new ScriptedHandler(RespondWithVersions("1.0.0", "2.0.0-beta.1"));
        using var service = CreateService(handler);

        (await service.GetLatestVersionAsync("Pkg"))!.ToString().Should().Be("1.0.0");
        (await service.GetLatestVersionAsync("Pkg", includePrerelease: true))!
            .ToString()
            .Should()
            .Be("2.0.0-beta.1");
    }

    [Fact]
    public async Task GetLatestVersion_ManyPackagesConcurrently_DoesNotCorruptSharedState()
    {
        // The update command runs eight lookups at once. An unsynchronised Dictionary and HashSet can
        // corrupt or throw under exactly that load, and it would do so intermittently.
        var handler = new ScriptedHandler(RespondWithVersions("1.0.0", "2.0.0"));
        using var service = CreateService(handler);

        var lookups = Enumerable
            .Range(0, 200)
            .Select(i => service.GetLatestVersionAsync($"Package{i % 20}"))
            .ToList();

        await Task.WhenAll(lookups);

        lookups.Should().AllSatisfy(t => t.Result!.ToString().Should().Be("2.0.0"));
        service.GetFailedLookups().Should().BeEmpty();
        handler.Requests.Should().BeLessThanOrEqualTo(20, "each distinct package is fetched once");
    }

    [Fact]
    public async Task GetLatestVersion_RecoveredPackage_IsNoLongerReportedAsFailed()
    {
        // Otherwise the caller keeps warning that a package could not be checked after it succeeded.
        var handler = new ScriptedHandler(
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.ServiceUnavailable),
            RespondWithVersions("4.0.0")
        );
        using var service = CreateService(handler);

        await service.GetLatestVersionAsync("Newtonsoft.Json");
        service.GetFailedLookups().Should().Contain("Newtonsoft.Json");

        await service.GetLatestVersionAsync("Newtonsoft.Json");

        service.GetFailedLookups().Should().BeEmpty("the package recovered");
    }

    [Fact]
    public async Task GetLatestVersion_ValidJsonThatIsNotAVersionIndex_IsRecordedAsAFailure()
    {
        // Returning null silently would let the package be reported as cleanly checked — the exact
        // silent-incomplete result this work exists to eliminate.
        var handler = new ScriptedHandler(() =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"unexpected":"shape"}"""),
            }
        );
        using var service = CreateService(handler);

        var latest = await service.GetLatestVersionAsync("Newtonsoft.Json");

        latest.Should().BeNull();
        service.GetFailedLookups().Should().Contain("Newtonsoft.Json");
    }

    private static NuGetVersionLookupService CreateService(ScriptedHandler handler)
    {
        // No real waiting, and fixed jitter, so retry tests are fast and deterministic.
        return new NuGetVersionLookupService(
            new HttpClient(handler),
            logger: null,
            delay: _ => Task.CompletedTask,
            jitter: () => 0.5
        );
    }

    private static Func<HttpResponseMessage> Respond(HttpStatusCode status)
    {
        return () => new HttpResponseMessage(status) { Content = new StringContent("{}") };
    }

    private static Func<HttpResponseMessage> RespondWithVersions(params string[] versions)
    {
        var body = $"{{\"versions\":[{string.Join(',', versions.Select(v => $"\"{v}\""))}]}}";
        return () =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    /// <summary>
    /// Returns queued responses in order, repeating the last one once exhausted, and counts requests.
    /// </summary>
    private sealed class ScriptedHandler(params Func<HttpResponseMessage>[] responses)
        : HttpMessageHandler
    {
        private int _index;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            var responder = responses[Math.Min(_index, responses.Length - 1)];
            _index++;

            return Task.FromResult(responder());
        }
    }
}

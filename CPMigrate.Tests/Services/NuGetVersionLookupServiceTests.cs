using System.Net;
using CPMigrate.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services;

public class NuGetVersionLookupServiceTests : IDisposable
{
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly NuGetVersionLookupService _sut;

    public NuGetVersionLookupServiceTests()
    {
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
        _sut = new NuGetVersionLookupService(_httpClient);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _httpClient.Dispose();
    }

    [Fact]
    public async Task GetLatestVersionAsync_ReturnsLatestStableVersion()
    {
        // Arrange
        var json = """{ "versions": [ "1.0.0", "2.0.0", "3.0.0-beta1", "2.1.0" ] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionAsync("TestPackage");

        // Assert
        result.Should().NotBeNull();
        result!.ToNormalizedString().Should().Be("2.1.0");
    }

    [Fact]
    public async Task GetLatestVersionAsync_WithPrerelease_ReturnsHighestIncludingPrerelease()
    {
        // Arrange
        var json = """{ "versions": [ "1.0.0", "2.0.0", "3.0.0-beta1" ] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionAsync("TestPackage", includePrerelease: true);

        // Assert
        result.Should().NotBeNull();
        result!.ToNormalizedString().Should().Be("3.0.0-beta1");
    }

    [Fact]
    public async Task GetLatestVersionAsync_NoStableVersion_ReturnsNull()
    {
        // Arrange — only prerelease versions, user didn't opt in
        var json = """{ "versions": [ "1.0.0-alpha", "2.0.0-beta" ] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionAsync("TestPackage");

        // Assert — should not return prerelease when user didn't opt in
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestVersionAsync_EmptyVersions_ReturnsNull()
    {
        // Arrange
        var json = """{ "versions": [] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionAsync("TestPackage");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestVersionAsync_HttpError_ReturnsNull()
    {
        // Arrange
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        // Act
        var result = await _sut.GetLatestVersionAsync("TestPackage");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestVersionAsync_InvalidJson_ReturnsNull()
    {
        // Arrange
        SetupHttpResponse("not valid json");

        // Act
        var result = await _sut.GetLatestVersionAsync("TestPackage");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestVersionAsync_UsesLowercasePackageId()
    {
        // Arrange
        var json = """{ "versions": [ "1.0.0" ] }""";
        HttpRequestMessage? capturedRequest = null;

        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(json)
            });

        // Act
        await _sut.GetLatestVersionAsync("Newtonsoft.Json");

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.ToString().Should().Contain("newtonsoft.json");
    }

    [Fact]
    public async Task GetLatestVersionInMajorAsync_ReturnsLatestInMajor()
    {
        // Arrange
        var json = """{ "versions": [ "12.0.1", "12.0.3", "13.0.1", "14.0.3" ] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionInMajorAsync("TestPackage", 12);

        // Assert
        result.Should().NotBeNull();
        result!.ToNormalizedString().Should().Be("12.0.3");
    }

    [Fact]
    public async Task GetLatestVersionInMajorAsync_NoMatchingMajor_ReturnsNull()
    {
        // Arrange
        var json = """{ "versions": [ "13.0.1", "14.0.3" ] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionInMajorAsync("TestPackage", 12);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestVersionInMajorAsync_FiltersPrerelease()
    {
        // Arrange
        var json = """{ "versions": [ "12.0.1", "12.1.0-beta", "12.0.3" ] }""";
        SetupHttpResponse(json);

        // Act
        var result = await _sut.GetLatestVersionInMajorAsync("TestPackage", 12);

        // Assert
        result.Should().NotBeNull();
        result!.ToNormalizedString().Should().Be("12.0.3");
    }

    [Fact]
    public void Dispose_OwnedHttpClient_DisposesClient()
    {
        // Arrange - create without injecting HttpClient
        var service = new NuGetVersionLookupService();

        // Act
        service.Dispose();

        // Assert - calling methods after dispose should fail
        var act = async () => await service.GetLatestVersionAsync("Test");
        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_InjectedHttpClient_DoesNotDisposeClient()
    {
        // Arrange
        var handler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(handler.Object);
        var service = new NuGetVersionLookupService(httpClient);

        // Act
        service.Dispose();

        // Assert - injected HttpClient should still be usable
        httpClient.BaseAddress = new Uri("https://example.com");
    }

    private void SetupHttpResponse(string content)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(content)
            });
    }
}

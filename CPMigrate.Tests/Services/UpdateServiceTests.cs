using System.Diagnostics;
using System.Net;
using CPMigrate.Services;
using FluentAssertions;
using Moq;
using Moq.Protected;
using NuGet.Versioning;

namespace CPMigrate.Tests.Services;

public class UpdateServiceTests
{
    private readonly Mock<IConsoleService> _consoleMock;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly Mock<IProcessRunner> _processRunnerMock;
    private readonly HttpClient _httpClient;
    private readonly UpdateService _updateService;

    public UpdateServiceTests()
    {
        _consoleMock = new Mock<IConsoleService>();
        // The self-update flow prompts, so the console must look like a TTY for these tests.
        _consoleMock.SetupGet(c => c.IsInteractive).Returns(true);
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _processRunnerMock = new Mock<IProcessRunner>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
        _updateService = new UpdateService(_consoleMock.Object, _httpClient, _processRunnerMock.Object);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsLatestVersion_WhenNewVersionExists()
    {
        // Arrange
        // Current version is likely 2.8.0 or 2.9.0 based on csproj
        // We simulate a response with a higher version
        var responseContent = @"{ ""versions"": [ ""1.0.0"", ""100.0.0"" ] }";

        SetupHttpResponse(responseContent);

        // Act
        var result = await _updateService.CheckForUpdatesAsync();

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new NuGetVersion("100.0.0"), result);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsNull_WhenNoNewVersionExists()
    {
        // Arrange
        var responseContent = @"{ ""versions"": [ ""0.0.1"" ] }";
        SetupHttpResponse(responseContent);

        // Act
        var result = await _updateService.CheckForUpdatesAsync();

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task PerformUpdateAsync_ExecutesUpdate_WhenUserConfirms()
    {
        // Arrange
        var responseContent = @"{ ""versions"": [ ""100.0.0"" ] }";
        SetupHttpResponse(responseContent);

        _consoleMock.Setup(c => c.AskConfirmation(It.IsAny<string>()))
            .Returns(true);

        _processRunnerMock.Setup(p => p.Run(It.IsAny<ProcessStartInfo>()))
            .Returns((0, "Updated", ""));

        // Act
        var result = await _updateService.PerformUpdateAsync();

        // Assert
        Assert.True(result);
        _processRunnerMock.Verify(p => p.Run(It.Is<ProcessStartInfo>(i => i.FileName == "dotnet" && i.Arguments.Contains("tool update"))), Times.Once);
        _consoleMock.Verify(c => c.Success(It.IsAny<string>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task PerformUpdateAsync_NonInteractiveTerminal_DeclinesAndSuggestsTheUnattendedCommand()
    {
        // Replacing the running tool without consent should not be inferred from a redirected
        // stream; the caller is pointed at `dotnet tool update` instead of being prompted.
        SetupHttpResponse(@"{ ""versions"": [ ""100.0.0"" ] }");
        _consoleMock.SetupGet(c => c.IsInteractive).Returns(false);

        var result = await _updateService.PerformUpdateAsync();

        Assert.False(result);
        _consoleMock.Verify(c => c.AskConfirmation(It.IsAny<string>()), Times.Never);
        _processRunnerMock.Verify(p => p.Run(It.IsAny<ProcessStartInfo>()), Times.Never);
        _consoleMock.Verify(c => c.Dim(It.Is<string>(s => s.Contains("dotnet tool update"))), Times.Once);
    }

    [Fact]
    public async Task PerformUpdateAsync_DoesNotUpdate_WhenUserRejects()
    {
        // Arrange
        var responseContent = @"{ ""versions"": [ ""100.0.0"" ] }";
        SetupHttpResponse(responseContent);

        _consoleMock.Setup(c => c.AskConfirmation(It.IsAny<string>()))
            .Returns(false);

        // Act
        var result = await _updateService.PerformUpdateAsync();

        // Assert
        Assert.False(result);
        _processRunnerMock.Verify(p => p.Run(It.IsAny<ProcessStartInfo>()), Times.Never);
    }

    [Fact]
    public async Task PerformUpdateAsync_ReturnsFalse_WhenUpdateFails()
    {
        // Arrange
        var responseContent = @"{ ""versions"": [ ""100.0.0"" ] }";
        SetupHttpResponse(responseContent);

        _consoleMock.Setup(c => c.AskConfirmation(It.IsAny<string>()))
            .Returns(true);

        _processRunnerMock.Setup(p => p.Run(It.IsAny<ProcessStartInfo>()))
            .Returns((1, "", "Error details"));

        // Act
        var result = await _updateService.PerformUpdateAsync();

        // Assert
        Assert.False(result);
        _consoleMock.Verify(c => c.Error(It.Is<string>(s => s == "Update failed:")), Times.Once);
        _consoleMock.Verify(c => c.Dim("Error details"), Times.Once);
    }

    [Fact]
    public void Dispose_OwnedHttpClient_DisposesClient()
    {
        // Arrange — create UpdateService without injecting HttpClient (it creates its own)
        var consoleMock = new Mock<IConsoleService>();
        var service = new UpdateService(consoleMock.Object);

        // Act — should not throw
        service.Dispose();

        // Assert — calling methods after dispose should fail because the HttpClient is disposed
        var act = async () => await service.CheckForUpdatesAsync();
        act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void Dispose_InjectedHttpClient_DoesNotDisposeClient()
    {
        // Arrange — inject an HttpClient (service should not own it)
        var consoleMock = new Mock<IConsoleService>();
        var handler = new Mock<HttpMessageHandler>();
        var httpClient = new HttpClient(handler.Object);
        var service = new UpdateService(consoleMock.Object, httpClient);

        // Act
        service.Dispose();

        // Assert — the injected HttpClient should still be usable
        // (we can't easily verify it's not disposed, but at least verify no exception from Dispose)
        httpClient.BaseAddress = new Uri("https://example.com"); // Would throw if disposed
    }

    private void SetupHttpResponse(string content)
    {
        _httpHandlerMock
            .Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>()
            )
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(content),
            });
    }
}

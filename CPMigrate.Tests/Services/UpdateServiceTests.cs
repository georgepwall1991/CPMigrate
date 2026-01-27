using System.Diagnostics;
using System.Net;
using CPMigrate.Services;
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

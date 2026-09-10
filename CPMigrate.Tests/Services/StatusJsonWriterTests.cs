using System.Text.Json;
using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// <c>--status --output Json</c> promises CI scripts one parseable document carrying the same
/// workspace facts the dashboard renders — CPM state, config, git, backups, frameworks — so a
/// consumer never has to parse prose.
/// </summary>
public class StatusJsonWriterTests
{
    [Fact]
    public void Serialize_EmitsTheDocumentShape()
    {
        var root = Parse(Serialize(Status()));

        root.GetProperty("operation").GetString().Should().Be("status");
        root.GetProperty("outputSchemaVersion").GetString().Should().Be(OutputMetadata.SchemaVersion);
        root.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("exitCode").GetInt32().Should().Be(ExitCodes.Success);
        root.GetProperty("directory").GetString().Should().Be("/ws");
    }

    [Fact]
    public void Serialize_CpmDisabled_OmitsThePackageCount()
    {
        // A count for a file that does not exist would be a number the document invented; absent
        // is the honest answer.
        var root = Parse(Serialize(Status(cpmEnabled: false, centralPackageCount: null)));

        root.GetProperty("cpmEnabled").GetBoolean().Should().BeFalse();
        root.TryGetProperty("centralPackageCount", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_CpmEnabled_CarriesThePackageCount()
    {
        var root = Parse(Serialize(Status(cpmEnabled: true, centralPackageCount: 7)));

        root.GetProperty("cpmEnabled").GetBoolean().Should().BeTrue();
        root.GetProperty("centralPackageCount").GetInt32().Should().Be(7);
    }

    [Fact]
    public void Serialize_NotAGitRepo_OmitsGitDirty()
    {
        // Dirty is meaningless outside a repository; absent rather than false, so a consumer does
        // not read "clean" into a directory git never saw.
        var root = Parse(Serialize(Status(gitRepository: false, gitDirty: false)));

        root.GetProperty("gitRepository").GetBoolean().Should().BeFalse();
        root.TryGetProperty("gitDirty", out _).Should().BeFalse();
    }

    [Fact]
    public void Serialize_GitRepo_CarriesGitDirty()
    {
        var root = Parse(Serialize(Status(gitRepository: true, gitDirty: true)));

        root.GetProperty("gitRepository").GetBoolean().Should().BeTrue();
        root.GetProperty("gitDirty").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Serialize_TargetFrameworks_SerializeAsAnObject()
    {
        var root = Parse(Serialize(Status(targetFrameworks: new Dictionary<string, int>
        {
            ["net10.0"] = 2,
            ["net8.0"] = 1,
        })));

        var frameworks = root.GetProperty("targetFrameworks");
        frameworks.GetProperty("net10.0").GetInt32().Should().Be(2);
        frameworks.GetProperty("net8.0").GetInt32().Should().Be(1);
    }

    private static string Serialize(WorkspaceStatus status) =>
        StatusJsonWriter.Serialize(status, ExitCodes.Success);

    private static WorkspaceStatus Status(
        bool cpmEnabled = true,
        int? centralPackageCount = 3,
        bool gitRepository = true,
        bool gitDirty = false,
        IReadOnlyDictionary<string, int>? targetFrameworks = null
    ) =>
        new(
            "/ws",
            ["App.sln"],
            ProjectCount: 2,
            cpmEnabled,
            centralPackageCount,
            ConfigPresent: true,
            gitRepository,
            gitDirty,
            BackupSets: 1,
            targetFrameworks ?? new Dictionary<string, int> { ["net10.0"] = 2 }
        );

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;
}

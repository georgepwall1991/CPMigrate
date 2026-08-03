using CPMigrate;
using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Services.Verify;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Verify;

/// <summary>
/// What happens to the working tree when verification cannot vouch for the migration.
///
/// The snapshot service is scripted rather than restored, because the interesting case — a graph that
/// moves for a reason nothing accounts for — is precisely the one a correct migration does not
/// produce on demand. What is under test is the response, not the detection.
/// </summary>
public class UnverifiedRollbackTests : IDisposable
{
    private readonly string _root;

    public UnverifiedRollbackTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"CPMigrateRollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RestoresTheProjectFile_InTheConditionsEveryCiRunHas()
    {
        // The gap this test exists for. The rollback handler declines on a non-interactive terminal
        // without --force, and declines outright under --quiet with a machine-readable format —
        // between them, every CI run. Inheriting the caller's --force would have left the unverified
        // change on disk in exactly the place the protection is worth having, while the payload said
        // it had been undone. Passing --verify is the consent.
        var original = WriteProject();

        var result = await Migrate(quiet: true, force: false, OutputFormat.Json);

        result.ExitCode.Should().Be(ExitCodes.GraphDrift);
        result.Verification!.RolledBack.Should().BeTrue();
        (await File.ReadAllTextAsync(ProjectPath))
            .Should()
            .Be(original, "the tree must be byte-identical to what the run found");
        File.Exists(Path.Combine(_root, "Directory.Packages.props"))
            .Should()
            .BeFalse("a props file this run created must not survive its own rollback");
    }

    [Fact]
    public async Task ReportsHonestly_WhenThereIsNoBackupToRestoreFrom()
    {
        // --no-backup removes the only way back. Claiming a rollback that could not happen is worse
        // than admitting it: the first tells someone not to look.
        WriteProject();

        var result = await Migrate(quiet: true, force: true, OutputFormat.Json, noBackup: true);

        result.ExitCode.Should().Be(ExitCodes.GraphDrift);
        result.Verification!.RolledBack.Should().BeFalse();
        VerificationPayload
            .From(result.Verification, strict: false)!
            .RolledBack.Should()
            .BeFalse("the payload reports what happened, not what was intended");
    }

    private string ProjectPath => Path.Combine(_root, "Api.csproj");

    private string WriteProject()
    {
        var content = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Serilog" Version="4.3.0" />
              </ItemGroup>
            </Project>
            """;

        File.WriteAllText(ProjectPath, content);
        return content;
    }

    private async Task<MigrationResult> Migrate(
        bool quiet,
        bool force,
        OutputFormat output,
        bool noBackup = false
    )
    {
        var console = new FakeConsoleService();

        var service = new MigrationService(
            console,
            quietMode: quiet,
            verifier: new MigrationVerifier(new DriftingSnapshotService())
        );

        return await service.ExecuteAsync(
            new Options
            {
                ProjectFileDir = ProjectPath,
                OutputDir = _root,
                BackupDir = _root,
                GitignoreDir = _root,
                Verify = true,
                Quiet = quiet,
                Force = force,
                Output = output,
                NoBackup = noBackup,
            }
        );
    }

    /// <summary>
    /// Reports a package moving for no reason the migration gave. Both captures cover the same
    /// project and framework, so the comparison is made rather than refused — the verdict has to come
    /// from the change itself.
    /// </summary>
    private sealed class DriftingSnapshotService : IGraphSnapshotService
    {
        private int _captures;

        public Task<GraphSnapshotResult> CaptureAsync(
            string restoreTargetPath,
            IReadOnlyList<string> projectPaths,
            string? basePath
        )
        {
            var version = _captures++ == 0 ? "4.3.0" : "9.9.9";

            return Task.FromResult(
                new GraphSnapshotResult(
                    RestoreSucceeded: true,
                    "restored",
                    new ResolvedGraphSnapshot(
                        [
                            new ProjectResolvedGraph(
                                "Api.csproj",
                                [
                                    new ResolvedFramework(
                                        "net10.0",
                                        Resolved: true,
                                        [new ResolvedPackage("Serilog", version, IsDirect: true)]
                                    ),
                                ]
                            ),
                        ],
                        []
                    )
                )
            );
        }
    }
}

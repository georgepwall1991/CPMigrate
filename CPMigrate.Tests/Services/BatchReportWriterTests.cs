using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// Tests for the Markdown rollup <see cref="BatchReportWriter"/> writes after a --batch run:
/// header metadata, per-solution rows, totals math, and the empty-batch edge. The layout is a
/// diff surface — teams attach these to CI and PRs — so every assertion pins exact rendered
/// lines, not just substrings.
/// </summary>
public class BatchReportWriterTests : IDisposable
{
    private readonly string _testDirectory =
        Path.Combine(Path.GetTempPath(), $"CPMigrateBatchReportTest_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [Fact]
    public void Render_HeaderCarriesOperationDateVersionAndDryRunFlag()
    {
        var result = new BatchResult
        {
            Operation = "batch-analyze",
            DryRun = true,
            Timestamp = "2026-08-25T12:00:00.0000000Z",
        };

        var report = BatchReportWriter.Render(result);

        report.Should().StartWith("# CPMigrate Batch Report\n");
        report.Should().Contain("- Operation: batch-analyze");
        report.Should().Contain("- Date: 2026-08-25T12:00:00.0000000Z");
        report.Should().Contain($"- Tool version: {OutputMetadata.CurrentVersion}");
        report.Should().Contain("- Dry run: yes");
    }

    [Fact]
    public void Render_DryRunFalse_SaysNo()
    {
        var result = new BatchResult { DryRun = false };

        BatchReportWriter.Render(result).Should().Contain("- Dry run: no");
    }

    [Fact]
    public void Render_TableHasOneRowPerSolutionInBatchOrder()
    {
        var result = new BatchResult
        {
            Solutions =
            {
                new SolutionResult
                {
                    Name = "Alpha",
                    Success = true,
                    ExitCode = ExitCodes.Success,
                    Summary = new OperationSummary { ProjectsProcessed = 3, PackagesFound = 12 },
                    PropsFile = "src/Alpha/Directory.Packages.props",
                },
                new SolutionResult
                {
                    Name = "Beta",
                    Success = false,
                    ExitCode = ExitCodes.ValidationError,
                    Summary = new OperationSummary { ProjectsProcessed = 1, PackagesFound = 2 },
                },
            },
        };

        var report = BatchReportWriter.Render(result);
        var rows = report
            .Split('\n')
            .Where(line => line.StartsWith("| ", StringComparison.Ordinal))
            .ToList();

        // Header, separator, one row per solution in batch order, totals.
        rows.Should().HaveCount(5);
        rows[0]
            .Should()
            .Be("| Solution | Exit Code | Projects Processed | Packages Found | Props File |");
        rows[2].Should().Contain("| Alpha | ").And.Contain(" 3 ").And.EndWith("Directory.Packages.props |");
        rows[3]
            .Should()
            .Be($"| Beta | {ExitCodes.ValidationError} | 1 | 2 |  |");
        rows[4].Should().StartWith("| **Total** |");
    }

    [Fact]
    public void Render_TotalsRowSumsAcrossSolutions()
    {
        var result = new BatchResult
        {
            Solutions =
            {
                new SolutionResult
                {
                    Name = "Alpha",
                    ExitCode = ExitCodes.Success,
                    Summary = new OperationSummary { ProjectsProcessed = 3, PackagesFound = 12 },
                },
                new SolutionResult
                {
                    Name = "Beta",
                    ExitCode = ExitCodes.Success,
                    Summary = new OperationSummary { ProjectsProcessed = 4, PackagesFound = 8 },
                },
            },
        };

        var report = BatchReportWriter.Render(result);

        report.Should().Contain("| **Total** |  | 7 | 20 |  |");
    }

    [Fact]
    public void Render_FailuresSectionNamesFailedSolutionsWithExitCodes()
    {
        var result = new BatchResult
        {
            Solutions =
            {
                new SolutionResult
                {
                    Name = "Alpha",
                    Success = true,
                    ExitCode = ExitCodes.Success,
                },
                new SolutionResult
                {
                    Name = "Beta",
                    Success = false,
                    ExitCode = ExitCodes.ValidationError,
                },
                new SolutionResult
                {
                    Name = "Gamma",
                    Success = false,
                    ExitCode = ExitCodes.UnexpectedError,
                },
            },
        };

        var report = BatchReportWriter.Render(result);

        report.Should().Contain("## Failures");
        report.Should().Contain($"- Beta (exit code {ExitCodes.ValidationError})");
        report.Should().Contain($"- Gamma (exit code {ExitCodes.UnexpectedError})");
        report.Should().NotContain("Alpha (exit code");
    }

    [Fact]
    public void Render_AllSolutionsSucceeded_OmitsFailuresSection()
    {
        var result = new BatchResult
        {
            Solutions =
            {
                new SolutionResult { Name = "Alpha", Success = true, ExitCode = ExitCodes.Success },
            },
        };

        BatchReportWriter.Render(result).Should().NotContain("## Failures");
    }

    [Fact]
    public void Render_EmptyBatch_RendersZerosWithoutSolutionRowsOrFailures()
    {
        var result = new BatchResult();

        var report = BatchReportWriter.Render(result);

        report.Should().Contain("| **Total** |  | 0 | 0 |  |");
        report.Should().Contain("| Solution | Exit Code |");
        report.Split('\n').Count(line => line.StartsWith("| ", StringComparison.Ordinal)).Should().Be(3);
        report.Should().NotContain("## Failures");
    }

    [Fact]
    public void Render_IsDeterministicForTheSameResult()
    {
        var result = new BatchResult
        {
            Operation = "batch-migrate",
            Timestamp = "2026-08-25T12:00:00.0000000Z",
            Solutions =
            {
                new SolutionResult
                {
                    Name = "Alpha",
                    ExitCode = ExitCodes.Success,
                    Summary = new OperationSummary { ProjectsProcessed = 1 },
                },
            },
        };

        BatchReportWriter.Render(result).Should().Be(BatchReportWriter.Render(result));
    }

    [Fact]
    public void Write_CreatesTheFileIncludingMissingParentDirectories()
    {
        var path = Path.Combine(_testDirectory, "nested", "dir", "batch-report.md");
        var result = new BatchResult
        {
            Solutions =
            {
                new SolutionResult { Name = "Alpha", Success = true, ExitCode = ExitCodes.Success },
            },
        };

        BatchReportWriter.Write(result, path);

        File.ReadAllText(path).Should().Contain("| Alpha |");
    }

    [Fact]
    public void Render_PipeInASolutionNameCannotBreakTheTable()
    {
        var result = new BatchResult
        {
            Solutions =
            {
                new SolutionResult
                {
                    Name = "Weird|Name",
                    ExitCode = ExitCodes.Success,
                    Summary = new OperationSummary(),
                },
            },
        };

        var report = BatchReportWriter.Render(result);

        report.Should().Contain("Weird\\|Name");
    }
}

using CPMigrate.Models;

namespace CPMigrate.Analyzers;

/// <summary>
/// Checks package references against a built-in license classification table
/// and flags packages with copyleft or unknown licenses that may need legal review.
/// </summary>
internal sealed class LicenseAnalyzer : IAnalyzer
{
    public string Name => "Package Licenses";

    private static readonly Dictionary<string, (string License, LicenseRisk Risk)> KnownLicenses = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Newtonsoft.Json"] = ("MIT", LicenseRisk.Permissive),
        ["Serilog"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["AutoMapper"] = ("MIT", LicenseRisk.Permissive),
        ["FluentValidation"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["MediatR"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["Polly"] = ("BSD-3-Clause", LicenseRisk.Permissive),
        ["Dapper"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["Moq"] = ("BSD-3-Clause", LicenseRisk.Permissive),
        ["xunit"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["NUnit"] = ("MIT", LicenseRisk.Permissive),
        ["Spectre.Console"] = ("MIT", LicenseRisk.Permissive),
        ["CommandLineParser"] = ("MIT", LicenseRisk.Permissive),
        ["Microsoft.Extensions.Logging"] = ("MIT", LicenseRisk.Permissive),
        ["Microsoft.Extensions.DependencyInjection"] = ("MIT", LicenseRisk.Permissive),
        ["Microsoft.EntityFrameworkCore"] = ("MIT", LicenseRisk.Permissive),
        ["Swashbuckle.AspNetCore"] = ("MIT", LicenseRisk.Permissive),
        ["RestSharp"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["NLog"] = ("BSD-3-Clause", LicenseRisk.Permissive),
        ["log4net"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["MySql.Data"] = ("GPL-2.0", LicenseRisk.Copyleft),
        ["MySqlConnector"] = ("MIT", LicenseRisk.Permissive),
        ["Oracle.ManagedDataAccess"] = ("Oracle", LicenseRisk.Proprietary),
        ["System.Data.SqlClient"] = ("MIT", LicenseRisk.Permissive),
        ["iTextSharp"] = ("AGPL-3.0", LicenseRisk.Copyleft),
        ["iText7"] = ("AGPL-3.0", LicenseRisk.Copyleft),
        ["Ghostscript.NET"] = ("AGPL-3.0", LicenseRisk.Copyleft),
        ["MongoDB.Driver"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["StackExchange.Redis"] = ("MIT", LicenseRisk.Permissive),
        ["RabbitMQ.Client"] = ("Apache-2.0", LicenseRisk.Permissive),
        ["Confluent.Kafka"] = ("Apache-2.0", LicenseRisk.Permissive),
    };

    public AnalyzerResult Analyze(ProjectPackageInfo packageInfo)
    {
        var issues = new List<AnalysisIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in packageInfo.References)
        {
            if (!seen.Add(reference.PackageName))
            {
                continue;
            }

            var projectId = packageInfo.ProjectId(reference.ProjectPath);

            if (KnownLicenses.TryGetValue(reference.PackageName, out var info))
            {
                if (info.Risk == LicenseRisk.Copyleft)
                {
                    issues.Add(new AnalysisIssue(
                        reference.PackageName,
                        $"{info.License} license — copyleft; derivative works must use the same license",
                        new[] { projectId },
                        AnalysisIssueCode.LicenseRisk,
                        AnalysisSeverity.High,
                        false,
                        new Dictionary<string, string> { ["license"] = info.License, ["risk"] = "copyleft" }));
                }
                else if (info.Risk == LicenseRisk.Proprietary)
                {
                    issues.Add(new AnalysisIssue(
                        reference.PackageName,
                        $"{info.License} license — proprietary; review terms before distribution",
                        new[] { projectId },
                        AnalysisIssueCode.LicenseRisk,
                        AnalysisSeverity.Moderate,
                        false,
                        new Dictionary<string, string> { ["license"] = info.License, ["risk"] = "proprietary" }));
                }
            }
        }

        return new AnalyzerResult(Name, issues);
    }
}

internal enum LicenseRisk
{
    Permissive,
    Copyleft,
    Proprietary,
}

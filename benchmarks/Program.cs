using System.Diagnostics;
using System.Text;
using CPMigrate.Services;

// CPMigrate scan benchmark.
//
// Generates a synthetic solution of real projects, restores them for real, and measures the two
// performance features end to end:
//
//   1. Concurrent per-project scans (--tree / --why scheduling): the CLI is driven twice over the
//      same synthetic layout with --max-parallelism 1 and then with a parallel cap. The difference
//      is what the directory-grouped scheduler buys on real `dotnet package list` subprocesses.
//   2. The shared list-package payload cache: DotNetPackageQueryService scans every project once
//      (uncached, one subprocess each) and then again through the same service instance (cached,
//      zero subprocesses).
//
// See README.md in this directory for methodology and interpretation.

var projectCount = 12;
var groupCount = 4;
var keepOutput = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--projects" when i + 1 < args.Length && int.TryParse(args[++i], out var n):
            projectCount = Math.Max(1, n);
            break;
        case "--groups" when i + 1 < args.Length && int.TryParse(args[++i], out var g):
            groupCount = Math.Max(1, g);
            break;
        case "--keep":
            keepOutput = true;
            break;
        default:
            Console.WriteLine($"Unknown argument: {args[i]}");
            Console.WriteLine("Usage: dotnet run [--projects N] [--groups D] [--keep]");
            return 2;
    }
}

var cliPath = Path.Combine(AppContext.BaseDirectory, "CPMigrate.dll");
if (!File.Exists(cliPath))
{
    Console.Error.WriteLine($"CPMigrate.dll not found next to the harness ({cliPath}).");
    Console.Error.WriteLine("Build the solution first (the ProjectReference usually does this).");
    return 1;
}

Console.WriteLine($"CPMigrate scan benchmark — {projectCount} projects in {groupCount} directories");

var root = Path.Combine(
    Path.GetTempPath(),
    "cpmigrate-bench",
    $"run-{DateTime.Now:yyyyMMdd-HHmmss}"
);
var projectPaths = GenerateSolution(root, projectCount, groupCount);
Console.WriteLine($"Synthetic solution: {root}");

try
{
    // Warm every cache that is not under test: SDK first-run, NuGet package download, and restore
    // of all N projects. Without this the first timed run measures cold-start noise, not the scan.
    Console.WriteLine("Warm-up (first full analyze, restores everything)…");
    var warmUp = await RunCliAsync(cliPath, root, maxParallelism: 2);
    if (warmUp.ExitCode != 0)
    {
        Console.Error.WriteLine(
            $"Warm-up run exited {warmUp.ExitCode}; results below may be meaningless."
        );
    }

    var rows = new List<BenchmarkRow>();

    var machineParallelism = Environment.ProcessorCount;
    foreach (var (label, parallelism) in new[]
             {
                 ("Analyze scan, sequential (--max-parallelism 1)", 1),
                 (
                     $"Analyze scan, concurrent (--max-parallelism {machineParallelism})",
                     machineParallelism
                 ),
             })
    {
        Console.Write($"{label} … ");
        var result = await RunCliAsync(cliPath, root, maxParallelism: parallelism);
        Console.WriteLine(FormatSeconds(result.Elapsed));
        rows.Add(new BenchmarkRow(label, projectCount, result.Elapsed, result.ExitCode));
    }

    var queryService = new DotNetPackageQueryService(SilentConsoleService.Instance);

    Console.Write("Resolved query pass, uncached (one subprocess per project) … ");
    var (uncachedElapsed, uncachedFailures) = await ScanAllAsync(queryService, projectPaths);
    Console.WriteLine(FormatSeconds(uncachedElapsed));
    if (uncachedFailures > 0)
    {
        return Fail(
            $"{uncachedFailures} of {projectCount} uncached queries failed — NuGet or restore is "
                + "unavailable; the timings above are not a benchmark."
        );
    }

    Console.Write("Resolved query pass, cached (payload cache, no subprocess) … ");
    var (cachedElapsed, cachedFailures) = await ScanAllAsync(queryService, projectPaths);
    Console.WriteLine(FormatSeconds(cachedElapsed));
    // A failed query is never cached, so any failure here means the "cached" pass launched
    // subprocesses and measured nothing about the cache.
    if (cachedFailures > 0)
    {
        return Fail(
            $"{cachedFailures} of {projectCount} cached queries failed — the second pass was not "
                + "served from the cache; the comparison is invalid."
        );
    }

    rows.Add(new BenchmarkRow("Resolved queries, uncached", projectCount, uncachedElapsed, 0));
    rows.Add(new BenchmarkRow("Resolved queries, cached", projectCount, cachedElapsed, 0));

    Console.WriteLine();
    Console.WriteLine("| Scenario | Projects | Wall time | ms/project | Exit |");
    Console.WriteLine("|---|---:|---:|---:|---:|");
    foreach (var row in rows)
    {
        Console.WriteLine(
            $"| {row.Scenario} | {row.Projects} | {row.Elapsed.TotalSeconds:F2}s "
            + $"| {row.Elapsed.TotalMilliseconds / row.Projects:F0} | {(row.ExitCode == 0 ? "ok" : row.ExitCode)} |"
        );
    }

    Console.WriteLine();
    Console.WriteLine(
        cachedElapsed < uncachedElapsed
            ? $"Payload cache saved {FormatSeconds(uncachedElapsed - cachedElapsed)} on the second pass."
            : "Warning: the cached pass was not faster — investigate before trusting these numbers."
    );

    return 0;
}
finally
{
    if (keepOutput)
    {
        Console.WriteLine($"Generated solution kept at {root}");
    }
    else
    {
        Directory.Delete(root, recursive: true);
    }
}

static List<string> GenerateSolution(string root, int projectCount, int groupCount)
{
    // Projects are spread across `groupCount` directories so the concurrent path exercises exactly
    // what it exists for: several groups running together while same-directory projects take turns.
    const string packageId = "Newtonsoft.Json";
    const string packageVersion = "13.0.3";

    Directory.CreateDirectory(Path.Combine(root, "src"));

    var paths = new List<string>(projectCount);
    for (var i = 0; i < projectCount; i++)
    {
        var group = i % groupCount;
        var directory = Path.Combine(root, "src", $"Group{group}");
        Directory.CreateDirectory(directory);

        var name = $"Project{i}";
        var path = Path.Combine(directory, $"{name}.csproj");
        File.WriteAllText(
            path,
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <Nullable>enable</Nullable>
               </PropertyGroup>
               <ItemGroup>
                 <PackageReference Include="{packageId}" Version="{packageVersion}" />
               </ItemGroup>
             </Project>
             """
        );
        paths.Add(path);
    }

    // A solution file is what `-s` discovery keys on; .slnx lists one line per project.
    var slnx = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
    slnx.AppendLine().AppendLine("<Solution>");
    foreach (var path in paths)
    {
        slnx.AppendLine(
            $"  <Project Path=\"{Path.GetRelativePath(root, path).Replace('\\', '/')}\" />"
        );
    }

    slnx.AppendLine("</Solution>");
    File.WriteAllText(Path.Combine(root, "Benchmark.slnx"), slnx.ToString());

    return paths;
}

static async Task<(int ExitCode, TimeSpan Elapsed)> RunCliAsync(
    string cliPath,
    string solutionDir,
    int maxParallelism
)
{
    var arguments = $"exec \"{cliPath}\" --analyze -s \"{solutionDir}\" --quiet";
    if (maxParallelism > 0)
    {
        arguments += $" --max-parallelism {maxParallelism}";
    }

    var start = Stopwatch.StartNew();
    using var process = Process.Start(
        new ProcessStartInfo("dotnet", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }
    )!;

    // Both pipes must be drained while the child runs: enough restore diagnostics fill either
    // buffer and the child blocks writing forever while this process waits for an exit that
    // cannot happen.
    var drainOut = process.StandardOutput.ReadToEndAsync();
    var drainErr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    await Task.WhenAll(drainOut, drainErr);
    return (process.ExitCode, start.Elapsed);
}

static async Task<(TimeSpan Elapsed, int Failures)> ScanAllAsync(
    DotNetPackageQueryService service,
    IReadOnlyList<string> projectPaths
)
{
    var start = Stopwatch.StartNew();
    var failures = 0;
    foreach (var path in projectPaths)
    {
        var (_, success) = await service.ScanResolvedPackagesAsync(path);
        if (!success)
        {
            failures++;
        }
    }

    return (start.Elapsed, failures);
}

static string FormatSeconds(TimeSpan elapsed) => $"{elapsed.TotalSeconds:F2}s";

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

internal sealed record BenchmarkRow(string Scenario, int Projects, TimeSpan Elapsed, int ExitCode);

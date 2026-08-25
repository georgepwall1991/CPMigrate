# CPMigrate scan benchmarks

A manual measurement tool for the two scan performance features. It is **not** part of
`CPMigrate.sln` and never runs in CI — it shells out to real `dotnet` subprocesses against a
synthetic solution with real NuGet restores, which is exactly what CI cannot afford.

- **Concurrent per-project scans** (`--tree` / `--why` scheduling): the CLI analyzes the same
  synthetic layout twice, once with `--max-parallelism 1` and once with a parallel cap. The gap is
  what the directory-grouped scheduler (`GroupedScanScheduler`) buys on real
  `dotnet list package` restores.
- **Shared list-package payload cache**: `DotNetPackageQueryService` scans every project once
  (uncached — one restore-backed subprocess per project) and then again through the same service
  instance (cached — zero subprocesses).

The regression guards for the same code live in the test suite as deterministic,
timing-free assertions (`GroupedScanSchedulerTests`,
`DotNetPackageQueryServiceCacheTests.ConcurrentResolvedScansOfOneProject_InvokeSubprocessOnce`).
This harness complements them with wall-clock numbers; it does not replace them.

## Usage

```sh
dotnet run --project benchmarks --configuration Release
```

Options:

| Flag | Default | Meaning |
|---|---|---|
| `--projects N` | `12` | Synthetic projects to generate |
| `--groups D` | `4` | Distinct directories they are spread across |
| `--keep` | off | Keep the generated solution instead of deleting it |

Requirements: the .NET 10 SDK and network access on the first run (the synthetic projects
reference `Newtonsoft.Json 13.0.3`, so the first analyze restores from nuget.org). Later runs hit
the local package cache.

## Methodology

1. Generate N minimal SDK-style projects spread over D directories (same-directory pairs are the
   case the directory grouping serializes), each referencing one pinned package.
2. Run one untimed warm-up analyze: it pays SDK first-run cost, downloads the package, and
   restores all N projects, so none of that lands in a measurement.
3. Time full CLI analyze passes at parallelism 1 and at the machine's processor count.
4. In-process, time one uncached resolved-query pass over all projects, then an identical pass
   through the same `DotNetPackageQueryService` instance, whose payload cache serves the second
   pass without spawning a single subprocess.
5. Print one table: scenario, project count, wall time, ms/project, exit code.

Each timing is a single cold measurement of end-to-end wall time — this tool answers "how much
does the feature save on a machine like mine", not "is there a statistically significant
difference". Variance is dominated by process startup (~0.5 s per CLI invocation); compare rows to
each other within one run, not across runs.

## Sample output

```
CPMigrate scan benchmark — 12 projects in 4 directories
Synthetic solution: /var/folders/…/T/cpmigrate-bench/run-20260825-164543
Warm-up (first full analyze, restores everything)…
Analyze scan, sequential (--max-parallelism 1) … 8.84s
Analyze scan, concurrent (--max-parallelism 18) … 3.13s
Resolved query pass, uncached (one subprocess per project) … 8.56s
Resolved query pass, cached (payload cache, no subprocess) … 0.00s

| Scenario | Projects | Wall time | ms/project | Exit |
|---|---:|---:|---:|---:|
| Analyze scan, sequential (--max-parallelism 1) | 12 | 8.84s | 737 | ok |
| Analyze scan, concurrent (--max-parallelism 18) | 12 | 3.13s | 261 | ok |
| Resolved queries, uncached | 12 | 8.56s | 714 | ok |
| Resolved queries, cached | 12 | 0.00s | 0 | ok |

Payload cache saved 8.56s on the second pass.
```

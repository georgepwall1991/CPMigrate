# Benchmarks

The table below tracks representative command latency for example repositories.

> Run environment: local developer machine, warm SDK cache, no network throttling.

| Scenario | Command | Projects | References | Typical Time |
|---|---|---:|---:|---:|
| Small solution analyze | `cpmigrate --analyze -s examples/small-solution --quiet` | 2 | ~4 | < 2s |
| Small solution migrate dry-run | `cpmigrate -s examples/small-solution --dry-run --quiet` | 2 | ~4 | < 2s |
| Monorepo batch analyze | `cpmigrate --batch examples/monorepo --analyze --quiet` | 2 solutions | ~4 | < 3s |
| JSON CI contract (analyze) | `cpmigrate --analyze -s . --output Json --quiet` | repo-dependent | repo-dependent | ~2-6s |
| Real repo migrate (Serilog) | `cpmigrate -s ./serilog --force --quiet` | 6 | 221 resolved | ~2s |
| Real repo migrate + **verify** | `cpmigrate -s ./serilog --verify --force --quiet` | 6 | 221 resolved | **~50s** |

## What `--verify` costs

The two rows above are the same migration on the same clone, and the difference is the whole cost of
the feature: **two full `dotnet restore` passes**, one for the baseline and one for the result. Every
other measurement here is a file-reading operation; this one waits on MSBuild and the feed twice.

That is why it is opt-in rather than default. Budget it as roughly *two restores of your solution* —
on Serilog, ~2s becomes ~50s — and expect it to scale with restore time rather than with project
count. On a warm cache the second restore is usually the cheaper of the two, because the migration
changes where versions are declared rather than which packages are needed.

The exception is a migration that unifies a version conflict: the packages on the winning side of the
unification have to be fetched if they were not already local. Serilog's `PolySharp` unification is
one package; a repository with fifty conflicts will pay for fifty.

If you run these in CI, track p50/p95 over time and publish deltas in release notes.

## Measuring the concurrent scan and the payload cache

The repository also carries a runnable harness, `benchmarks/`, that measures the two scan
performance features on a synthetic solution it generates itself: N real projects with real
restores, analyzed once serially (`--max-parallelism 1`) and once concurrently, then resolved-query
passes timed uncached against cached through the shared payload cache. It is manual tooling — not
part of the solution, never run in CI — so its wall-clock numbers stay out of the test suite:

```sh
dotnet run --project benchmarks --configuration Release [--projects 12] [--groups 4] [--keep]
```

See `benchmarks/README.md` for the full methodology. The regression guards for the same code are
deterministic and timing-free: `GroupedScanSchedulerTests` and the concurrency tests under
`CPMigrate.Tests/Services/`.

# Benchmarks

The table below tracks representative command latency for example repositories.

> Run environment: local developer machine, warm SDK cache, no network throttling.

| Scenario | Command | Projects | References | Typical Time |
|---|---|---:|---:|---:|
| Small solution analyze | `cpmigrate --analyze -s examples/small-solution --quiet` | 2 | ~4 | < 2s |
| Small solution migrate dry-run | `cpmigrate -s examples/small-solution --dry-run --quiet` | 2 | ~4 | < 2s |
| Monorepo batch analyze | `cpmigrate --batch examples/monorepo --analyze --quiet` | 2 solutions | ~4 | < 3s |
| JSON CI contract (analyze) | `cpmigrate --analyze -s . --output Json --quiet` | repo-dependent | repo-dependent | ~2-6s |

If you run these in CI, track p50/p95 over time and publish deltas in release notes.

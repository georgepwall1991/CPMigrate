# Monorepo Example

This example simulates a repository with multiple solutions so you can exercise batch mode and CI contracts.

## Structure

```
monorepo/
  services/
    ServiceA/ServiceA.sln
    ServiceA/src/ServiceA/ServiceA.csproj
    ServiceB/ServiceB.sln
    ServiceB/src/ServiceB/ServiceB.csproj
```

## Try it

```bash
cd examples/monorepo

# Batch dry run
cpmigrate --batch . --dry-run

# Batch analyze with JSON output
cpmigrate --batch . --analyze --output Json --quiet > batch-analyze.json
```

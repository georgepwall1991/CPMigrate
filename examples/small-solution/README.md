# Small Solution Example

This example models a tiny pre-CPM repository with two projects and explicit package versions in project files.

## Structure

```
small-solution/
  SmallSolution.sln
  src/
    Api/Api.csproj
    Worker/Worker.csproj
```

## Try it

```bash
cd examples/small-solution

# Preview migration
cpmigrate -s . --dry-run

# Preview a single project
cpmigrate --project ./src/Api/Api.csproj --dry-run

# Analyze dependency health
cpmigrate --analyze --audit --outdated --deprecated
```

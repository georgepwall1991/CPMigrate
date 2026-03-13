# CPMigrate — NuGet Central Package Management Migration Tool for .NET Teams

<div align="center">
  <img src="./docs/images/logo.png" alt="CPMigrate Logo — .NET NuGet Central Package Management CLI Tool" width="128" />
  <br/>
  <img src="./docs/images/banner.png" alt="CPMigrate Banner — Migrate, Analyze, and Update NuGet Packages" width="100%" />
</div>

<div align="center">

[![.NET](https://img.shields.io/badge/.NET-10.0+-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/CPMigrate.svg?style=flat-square&logo=nuget)](https://www.nuget.org/packages/CPMigrate/)
[![Downloads](https://img.shields.io/nuget/dt/CPMigrate.svg?style=flat-square&color=blue)](https://www.nuget.org/packages/CPMigrate/)

**Migrate .NET solutions to NuGet Central Package Management | Analyze dependency health | Update packages safely with rollback**

</div>

![CPMigrate Interactive Wizard](./docs/images/cpmigrate-interactive.gif)

---

## Why CPMigrate?

Managing NuGet dependencies across large .NET solutions is painful. Version drift, duplicated references, transitive conflicts, and security vulnerabilities accumulate silently until they break your build or compromise your supply chain.

**CPMigrate** is a .NET dependency analysis CLI and Central Package Management migration tool for teams adopting [`Directory.Packages.props`](https://learn.microsoft.com/nuget/consume-packages/central-package-management). It helps you migrate existing solutions, analyze dependency health, auto-fix common package issues, and update packages with rollback protection when tests fail.

**Canonical value proposition:** Migrate .NET solutions to NuGet Central Package Management, analyze dependency health, and update packages safely with rollback.

Learn more in the search-focused docs hub: `https://georgepwall1991.github.io/CPMigrate/`

### What it does

- **Migrates** any .NET solution to CPM by generating `Directory.Packages.props` and cleaning `.csproj` files
- **Analyzes** dependency health: version inconsistencies, duplicates, redundant references, transitive conflicts, framework alignment, and security vulnerabilities
- **Auto-fixes** detected issues with a single command
- **Updates** NuGet packages to latest versions, runs `dotnet test`, and rolls back automatically on failure
- **Unifies** repeated project properties into `Directory.Build.props`
- **Batch processes** monorepos with dozens of solutions in parallel
- Supports `.sln` and the new `.slnx` format (Visual Studio 17.10+)

### Who this is for

- .NET solution owners migrating to `Directory.Packages.props`
- App teams modernizing NuGet package management without hand-editing every project file
- Monorepo and multi-solution teams trying to standardize dependency policy
- CI/CD maintainers who need machine-readable dependency analysis and safe update workflows

### Why use CPMigrate instead of doing it by hand?

| Approach | What you get | Where it breaks down |
|----------|--------------|----------------------|
| **Manual CPM migration** | Full control over `Directory.Packages.props` and project edits | Slow, easy to miss references, hard to repeat across many solutions |
| **Ad hoc scripts** | Team-specific automation for one repository shape | Brittle logic, poor reuse, weak docs, and little rollback or analysis support |
| **Raw `dotnet package list`** | Useful package inventory and vulnerability data | No migration, no fixers, no rollback, no central props generation |
| **CPMigrate** | Migration, dependency analysis, auto-fix, safe package updates, batch processing, and CI-friendly JSON | Purpose-built for this exact modernization path |

---

## Table of Contents

- [Installation](#installation)
- [Quick Start](#quick-start)
- [60-Second Quickstart](#60-second-quickstart)
- [Who This Is For](#who-this-is-for)
- [Features](#features)
  - [CPM Migration](#cpm-migration)
  - [Dependency Analysis](#dependency-analysis)
  - [Auto-Fix](#auto-fix)
  - [Package Updates with Test Verification](#package-updates-with-test-verification)
  - [Directory.Build.props Unification](#directorybuildprops-unification)
  - [Batch Processing](#batch-processing)
  - [Backup & Rollback](#backup--rollback)
  - [Configuration File](#configuration-file)
- [CLI Reference](#cli-reference)
- [Exit Codes](#exit-codes)
- [CI/CD Integration](#cicd-integration)
- [Examples & Benchmarks](#examples--benchmarks)
- [Release Cadence](#release-cadence)
- [Telemetry (Opt-in)](#telemetry-opt-in)
- [Community Growth](#community-growth)
- [Gallery](#gallery)
- [Contributing](#contributing)
- [License](#license)

---

## Installation

### .NET Global Tool (Recommended)

Requires **.NET SDK 8.0** or later. Targets .NET 10 with `LatestMajor` roll-forward.

```bash
dotnet tool install --global CPMigrate
```

**Update to the latest version:**

```bash
cpmigrate --update
```

Or via the .NET CLI:

```bash
dotnet tool update --global CPMigrate
```

### Other Install Paths

- Docs hub: `https://georgepwall1991.github.io/CPMigrate/install/`
- Homebrew for macOS/Linux: `brew tap georgepwall1991/cpmigrate && brew install cpmigrate`
- Winget for Windows: `winget install GeorgeWall.CPMigrate` after the Winget manifest is indexed
- Windows portable release package: download `CPMigrate-portable-win-x64.zip` from GitHub Releases and run `CPMigrate.exe`
- Source build: clone the repo and run `dotnet build`

> **Note:** NuGet indexing may take up to 15 minutes after a new release. Clear your HTTP cache if updates aren't found immediately:
> `dotnet nuget locals http-cache --clear`

### From Source

```bash
git clone https://github.com/georgepwall1991/CPMigrate.git
cd CPMigrate
dotnet build
```

---

## Quick Start

### Interactive Mode (Recommended for first-time users)

```bash
cpmigrate
```

Launches the Mission Control dashboard — a step-by-step wizard that guides you through migration, analysis, package updates, rollback, and more. The wizard adapts to your environment: when CPM is already enabled, it offers **Update NuGet Packages** as a quick action with prompts for transitive dependencies, pre-release versions, and dry-run mode.

### Migrate a solution to CPM

```bash
cpmigrate -s ./MySolution.sln
```

### Preview changes without modifying files

```bash
cpmigrate -s ./MySolution.sln --dry-run
```

### Migrate a single project

```bash
cpmigrate --project ./src/Api/Api.csproj --dry-run
```

### Analyze dependency health

```bash
cpmigrate --analyze
```

### Update all packages to latest versions

```bash
cpmigrate --update-packages

# Include transitive dependencies
cpmigrate --update-packages --transitive
```

### Use in CI from the first run

```bash
# JSON-only output for CI parsers
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analysis.json

# Safe preview before migration
cpmigrate -s ./MySolution.sln --dry-run --output Json --quiet > migration-preview.json
```

Use the dedicated CI guide for GitHub Actions snippets and machine-readable workflows:
`https://georgepwall1991.github.io/CPMigrate/guides/ci-cd/`

---

## 60-Second Quickstart

```bash
# 1) Scan for issues (CI-safe exit codes)
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analysis.json

# 2) Preview migration
cpmigrate -s ./MySolution.sln --dry-run

# 3) Migrate to CPM
cpmigrate -s ./MySolution.sln

# 4) Safely update packages with rollback protection
cpmigrate --update-packages --dry-run
```

---

## Features

### CPM Migration

Scans your `.sln` or `.slnx` file, extracts all `<PackageReference>` entries from `.csproj` / `.fsproj` / `.vbproj` files, resolves version conflicts, and generates a centralized `Directory.Packages.props`.

```bash
# Standard migration
cpmigrate -s ./MySolution.sln

# Merge into an existing Directory.Packages.props
cpmigrate -s ./MySolution.sln --merge

# Fail if any version conflicts exist (strict mode)
cpmigrate -s ./MySolution.sln --conflict-strategy Fail

# Prompt for each conflict interactively
cpmigrate -s ./MySolution.sln --interactive-conflicts
```

**Conflict resolution strategies:**

| Strategy | Behavior |
|----------|----------|
| `Highest` | Use the highest version found across projects (default) |
| `Lowest` | Use the lowest version found |
| `Fail` | Exit with error if any package has conflicting versions |

---

### Dependency Analysis

Run 7 built-in analyzers without modifying any files:

```bash
cpmigrate --analyze
```

| Analyzer | What it detects |
|----------|----------------|
| **Version Inconsistencies** | Same package with different versions across projects |
| **Duplicate Packages** | Same package referenced with different casing (e.g., `Newtonsoft.Json` vs `newtonsoft.json`) |
| **Redundant References** | Same package referenced multiple times within a single project |
| **Transitive Conflicts** | Transitive dependencies with divergent versions across projects |
| **Framework Alignment** | Projects targeting different `TargetFramework` values |
| **Redundant Direct References** | Explicit references already provided transitively (lifting candidates) |
| **Security Vulnerabilities** | Known CVEs in direct and transitive dependencies (requires `--audit`) |

```bash
# Include transitive dependencies in analysis
cpmigrate --analyze --transitive

# Include security vulnerability scanning
cpmigrate --analyze --audit

# Full analysis with all checks
cpmigrate --analyze --transitive --audit

# Include outdated + deprecated checks
cpmigrate --analyze --outdated --deprecated
```

---

### Auto-Fix

Automatically fix detected issues:

```bash
# Fix all auto-fixable issues
cpmigrate --analyze --fix

# Preview what fixes would be applied
cpmigrate --analyze --fix-dry-run
```

**Available fixers:**

| Fixer | What it fixes |
|-------|---------------|
| **Version Inconsistency Fixer** | Standardizes package versions across projects using the configured conflict strategy |
| **Duplicate Package Casing Fixer** | Normalizes package name casing to the most common variant |
| **Redundant Reference Fixer** | Removes duplicate `<PackageReference>` entries within the same project |
| **Transitive Conflict Pinner** | Pins divergent transitive dependencies in `Directory.Packages.props` |

---

### Package Updates with Test Verification

**New in v3.0.** Update all NuGet packages to their latest versions with automatic test verification and rollback. **v3.2** adds full support in the interactive wizard — run `cpmigrate -i` and select "Update NuGet packages" from the maintenance menu.

```bash
# Preview available updates
cpmigrate --update-packages --dry-run

# Update packages, run tests, rollback on failure
cpmigrate --update-packages

# Include pre-release versions
cpmigrate --update-packages --include-prerelease

# Or use the interactive wizard (v3.2+)
cpmigrate -i
```

#### Transitive Dependency Pinning (v3.1)

**New in v3.1.** Add `--transitive` to also scan and pin transitive (indirect) dependencies:

```bash
# Preview direct + transitive updates
cpmigrate --update-packages --transitive --dry-run

# Update direct packages and pin transitive deps
cpmigrate --update-packages --transitive

# With pre-release versions
cpmigrate --update-packages --transitive --include-prerelease
```

When `--transitive` is enabled, CPMigrate:
- Scans all projects via `dotnet list package --include-transitive`
- Deduplicates across projects (picks the highest resolved version)
- Excludes transitive deps already managed as direct dependencies
- Queries NuGet for the latest version of each transitive dep
- Shows separate **DIRECT UPDATES** and **TRANSITIVE UPDATES** sections
- Pins accepted transitive updates as new `<PackageVersion>` entries in `Directory.Packages.props`

Per-project scan failures are logged and skipped gracefully. If all scans fail, the tool continues with direct-only updates.

**How it works:**

1. Reads current versions from `Directory.Packages.props`
2. Queries the NuGet API for latest versions (8 concurrent lookups)
3. Optionally scans transitive dependencies (`--transitive`)
4. Shows a table of available updates (separate sections for direct and transitive)
5. For **major version bumps**, prompts you interactively: accept or skip
6. Minor/patch updates are auto-accepted
7. Creates a backup of `Directory.Packages.props`
8. Applies version updates and transitive pins atomically
9. Runs `dotnet restore` then `dotnet test`
10. **Tests pass** — keeps updates, cleans up backup
11. **Tests fail** — rolls back all changes (including transitive pins) automatically

> Requires CPM to be enabled. If `Directory.Packages.props` doesn't exist, run `cpmigrate` first to migrate.
> Transitive scanning requires `dotnet restore` to have been run beforehand.

---

### Directory.Build.props Unification

Promote repeated properties and items from individual project files into a shared `Directory.Build.props`:

```bash
# Preview what would be unified
cpmigrate --unify-props --dry-run

# Apply unification
cpmigrate --unify-props

# Skip confirmation prompt
cpmigrate --unify-props --force
```

Identifies properties and items present in at least 60% of projects with the same value (e.g., `TargetFramework`, `ImplicitUsings`, `Nullable`, `Authors`) and migrates them to the root-level file. Individual project files are cleaned up automatically.

---

### Batch Processing

Process multiple solutions in a monorepo:

```bash
# Sequential processing
cpmigrate --batch /path/to/repo

# Parallel processing (uses all CPU cores)
cpmigrate --batch /path/to/repo --batch-parallel

# Continue on failure
cpmigrate --batch /path/to/repo --batch-parallel --batch-continue
```

Recursively discovers `.sln` and `.slnx` files, excluding common non-project directories (`node_modules`, `bin`, `obj`, `.git`, etc.). Each solution gets an isolated backup directory to prevent collisions.

---

### Backup & Rollback

Every migration creates a timestamped backup. Roll back at any time:

```bash
# Rollback to previous state
cpmigrate --rollback

# List all backups
cpmigrate --list-backups

# Prune old backups, keeping the last 3
cpmigrate --prune-backups --retention 3

# Delete all backups
cpmigrate --prune-all
```

Backups are stored in `.cpmigrate_backup/` with a JSON manifest for reliable restoration. Use `--add-gitignore` to automatically add the backup directory to `.gitignore`.

---

### Configuration File

Create a `.cpmigrate.json` in your repository root to set default options:

```json
{
  "$schema": "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate.schema.json",
  "ConflictStrategy": "Highest",
  "Backup": true,
  "BackupDir": ".",
  "AddGitignore": true,
  "MergeExisting": false,
  "OutputFormat": "Terminal",
  "Retention": {
    "Enabled": true,
    "MaxBackups": 5
  }
}
```

The config file is discovered by walking up from the selected solution/project path, or from the current directory when no path is provided. CLI arguments always take precedence over config file values.

---

## CLI Reference

### Migration & Core

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--solution` | `-s` | current directory | Path to `.sln` / `.slnx` file or directory |
| `--project` | `-p` | | Path to a specific project file, or a directory containing one project |
| `--output-dir` | `-o` | `.` | Output directory for `Directory.Packages.props` |
| `--dry-run` | `-d` | `false` | Preview changes without modifying files |
| `--merge` | | `false` | Merge into existing `Directory.Packages.props` |
| `--conflict-strategy` | | `Highest` | Version conflict resolution: `Highest`, `Lowest`, `Fail` |
| `--interactive-conflicts` | | `false` | Prompt for each version conflict |
| `--keep-attrs` | `-k` | `false` | Keep `Version` attributes in project files |
| `--interactive` | `-i` | `false` | Launch the interactive Mission Control wizard (migration, analysis, package updates, rollback, batch, backups) |

### Analysis & Auto-Fix

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--analyze` | `-a` | `false` | Run dependency health analysis |
| `--transitive` | | `false` | Include transitive dependencies |
| `--audit` | | `false` | Include security vulnerability scanning |
| `--outdated` | | `false` | Include outdated package checks |
| `--deprecated` | | `false` | Include deprecated package checks |
| `--fix` | | `false` | Apply auto-fixes (requires `--analyze`) |
| `--fix-dry-run` | | `false` | Preview auto-fixes without applying |

### Package Updates (v3.0+)

| Option | Default | Description |
|--------|---------|-------------|
| `--update-packages` | `false` | Update all packages to latest, run tests, rollback on failure |
| `--transitive` | `false` | Also scan and pin transitive dependencies (v3.1) |
| `--include-prerelease` | `false` | Include pre-release versions when updating |

### Modernization

| Option | Default | Description |
|--------|---------|-------------|
| `--unify-props` | `false` | Migrate common properties to `Directory.Build.props` |
| `--force` | `false` | Skip confirmation prompts |

### Batch Processing

| Option | Default | Description |
|--------|---------|-------------|
| `--batch` | | Directory to scan recursively for solutions |
| `--batch-parallel` | `false` | Process solutions in parallel |
| `--batch-continue` | `false` | Continue even if a solution fails |

### Backup & Rollback

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--rollback` | `-r` | `false` | Restore from most recent backup |
| `--no-backup` | `-n` | `false` | Disable backup creation |
| `--backup-dir` | | `.` | Backup directory location |
| `--list-backups` | | `false` | List all available backups |
| `--prune-backups` | | `false` | Delete old backups based on `--retention` |
| `--prune-all` | | `false` | Delete all backups |
| `--retention` | | `5` | Number of backups to keep when pruning |
| `--add-gitignore` | | `false` | Add backup directory to `.gitignore` |

### Output & Logging

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--output` | | `Terminal` | Output format: `Terminal` or `Json` |
| `--output-file` | | | Write JSON output to a file |
| `--quiet` | `-q` | `false` | Suppress non-essential output |
| `--verbose` | `-v` | `false` | Enable diagnostic logging to `cpmigrate.log` |

### Self-Update

| Option | Default | Description |
|--------|---------|-------------|
| `--update` | `false` | Check for and install the latest version of CPMigrate |

---

## Exit Codes

| Code | Name | Meaning |
|------|------|---------|
| `0` | Success | Operation completed successfully |
| `1` | ValidationError | Invalid command-line options |
| `2` | FileOperationError | File I/O or permission failure |
| `3` | VersionConflict | Unresolvable version conflict (with `--conflict-strategy Fail`) |
| `4` | NoProjectsFound | No `.csproj` / `.fsproj` / `.vbproj` files discovered |
| `5` | AnalysisIssuesFound | Analysis detected issues (useful for CI gates) |
| `6` | UnexpectedError | Unhandled exception |
| `7` | TestFailure | Tests failed after package update (rollback performed) |

---

## CI/CD Integration

### Strict JSON Contract Mode

Use `--output Json --quiet` to guarantee JSON-only stdout, including when CPMigrate discovers a `.cpmigrate.json` file. That makes it safe for CI parsing without stripping banners, config notices, or other preamble.

```bash
# analyze
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analyze.json

# migrate
cpmigrate -s ./MySolution.sln --dry-run --output Json --quiet > migrate.json

# rollback
cpmigrate --rollback --backup-dir . --output Json --quiet > rollback.json

# update-packages
cpmigrate --update-packages --dry-run --output Json --quiet > update-packages.json
```

### GitHub Actions Example

```yaml
- name: Install CPMigrate
  run: dotnet tool install --global CPMigrate

- name: Check dependency health
  run: cpmigrate --analyze --audit --output Json --output-file analysis.json --quiet

- name: Fail on issues
  run: |
    EXIT_CODE=$(cpmigrate --analyze --audit --quiet; echo $?)
    if [ $EXIT_CODE -eq 5 ]; then
      echo "::error::Dependency issues detected"
      exit 1
    fi
```

### JSON Output

Use `--output Json` to produce machine-readable output for CI/CD pipelines:

```bash
cpmigrate --analyze --output Json --output-file report.json
```

---

## Examples & Benchmarks

- Starter example: `examples/small-solution/`
- Monorepo example: `examples/monorepo/`
- Benchmark table: `docs/benchmarks.md`

These sample repositories are designed for onboarding, CI templates, and reproducible before/after conversion demos.

---

## Release Cadence

- **Stable releases:** weekly, versioned and changeloged
- **Release candidates (RC):** published for fast feedback before stable promotion
- **Change log source of truth:** `CHANGELOG.md`
- **Policy details:** `docs/release-cadence.md`

---

## Telemetry (Opt-in)

CPMigrate supports privacy-first telemetry that is **disabled by default**.

- Enable by setting `CPMIGRATE_TELEMETRY_OPT_IN=true`
- Captures only command-level metrics (operation, duration, exit code category, high-level flags)
- Captures **no** project paths, package names, file contents, or source code
- Stores local events at `~/.cpmigrate/telemetry/events.ndjson`

---

## Community Growth

- Enable **GitHub Discussions** and pin:
  - `Start here` (first-run flow + CI JSON mode)
  - `Success stories` (before/after migration outcomes)
- Use Discussions as the primary intake for usage feedback and roadmap voting.

---

## Gallery

### Mission Control Dashboard
![CPMigrate Interactive](./docs/images/cpmigrate-interactive.gif)
*The interactive wizard assessing migration risk and guiding you through each step.*

### Risk Analysis & Dry Run
![CPMigrate Demo](./docs/images/cpmigrate-demo.gif)
*Previewing changes safely before committing.*

### Security & Package Analysis
![CPMigrate Analyze](./docs/images/cpmigrate-analyze.gif)
*Scanning for vulnerabilities and redundant dependencies.*

---

## Contributing

Contributions are welcome. To get started:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Write tests for your changes
4. Ensure all 487+ tests pass (`dotnet test`)
5. Open a Pull Request

---

## License

Distributed under the MIT License. See `LICENSE` for more information.

## Author

**George Wall** — [@georgepwall1991](https://github.com/georgepwall1991)

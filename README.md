# CPMigrate — NuGet Central Package Management Migration Tool for .NET Teams

<div align="center">
  <img src="https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/logo.png" alt="CPMigrate Logo — .NET NuGet Central Package Management CLI Tool" width="128" />
  <br/>
  <img src="https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/banner.png" alt="CPMigrate Banner — Migrate, Analyze, and Update NuGet Packages" width="100%" />
</div>

<div align="center">

[![.NET](https://img.shields.io/badge/.NET-10.0+-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/CPMigrate.svg?style=flat-square&logo=nuget)](https://www.nuget.org/packages/CPMigrate/)
[![Downloads](https://img.shields.io/nuget/dt/CPMigrate.svg?style=flat-square&color=blue)](https://www.nuget.org/packages/CPMigrate/)

**Migrate .NET solutions to NuGet Central Package Management (CPM) · Analyze dependency health · Update packages safely with rollback**

</div>

**CPMigrate** is a .NET global tool that migrates solutions to [`Directory.Packages.props`](https://learn.microsoft.com/nuget/consume-packages/central-package-management), analyzes NuGet dependency health, auto-fixes common package issues, and updates packages with test verification and rollback.

Docs hub: [georgepwall1991.github.io/CPMigrate](https://georgepwall1991.github.io/CPMigrate/)

---

## The problem

Managing NuGet dependencies across large .NET solutions is painful. Version drift, duplicated references, transitive conflicts, and security vulnerabilities accumulate silently until they break your build or compromise your supply chain. Hand-editing every `.csproj` into Central Package Management is slow and easy to get wrong; ad-hoc scripts rarely include dry-run, fixers, or test-backed rollback.

## What it catches

- **Version inconsistencies** — same package at different versions across projects
- **Duplicate / redundant PackageReferences** — casing duplicates and repeated refs in one project
- **Transitive conflicts** — divergent transitive dependency graphs (optional pin + update)
- **Framework misalignment** — projects on different `TargetFramework` values
- **Security vulnerabilities** — known CVEs via `--audit` (direct and transitive)
- **Outdated / deprecated packages** — inventory checks with `--outdated` / `--deprecated`
- **Scattered versions** — still living in `.csproj` files instead of `Directory.Packages.props`
- **CPM drift** — after migrating: inline versions that override the central one, references with no version at all, orphaned pins, a props file with central management switched off

## Install

Requires **.NET SDK 8.0** or later. Targets .NET 10 with `LatestMajor` roll-forward.

```bash
dotnet tool install --global CPMigrate --version 3.18.0
```

```bash
# update
dotnet tool update --global CPMigrate
# or
cpmigrate --update
```

**Other install paths**

- Docs hub: https://georgepwall1991.github.io/CPMigrate/install/
- Homebrew: `brew tap georgepwall1991/cpmigrate && brew install cpmigrate`
- Winget: `winget install GeorgeWall.CPMigrate` (after the manifest is indexed)
- Windows portable: `CPMigrate-portable-win-x64.zip` from [GitHub Releases](https://github.com/georgepwall1991/CPMigrate/releases)
- From source: `git clone https://github.com/georgepwall1991/CPMigrate.git && dotnet build`

> NuGet indexing may take a few minutes after a new release. If the version is missing: `dotnet nuget locals http-cache --clear`

## See it work

Product-flow diagrams from real CLI output (not stock screenshots). Absolute URLs so NuGet.org and GitHub both render them.

### Dependency analysis scoreboard

![CPMigrate dependency analysis scoreboard — version inconsistencies and health share meters](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-analyze-scoreboard.svg)

### CPM migration before / after

![CPMigrate Central Package Management migration — Directory.Packages.props before and after](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-cpm-migration.svg)

### Safe package updates with --bisect

![CPMigrate package update bisect — keep the largest green update subset with rollback](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-update-bisect.svg)

Terminal recordings (also absolute HTTPS for NuGet PackageReadmeFile):

![CPMigrate Interactive Wizard Mission Control dashboard](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/cpmigrate-interactive.gif)

## 30-second path

```bash
# 1) Scan for issues (CI-safe exit codes)
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analysis.json

# 2) Preview migration to Directory.Packages.props
cpmigrate -s ./MySolution.sln --dry-run

# 3) Migrate to Central Package Management (CPM)
cpmigrate -s ./MySolution.sln

# 4) Safely update packages with rollback protection
cpmigrate --update-packages --dry-run

# 5) Keep the largest green subset when something breaks tests
cpmigrate --update-packages --bisect
```

Interactive first run: `cpmigrate` launches Mission Control (wizard). Single project: `cpmigrate --project ./src/Api/Api.csproj --dry-run`.

## Feature snapshot

| Surface | What you get |
|---------|----------------|
| **CPM migration** | Generate `Directory.Packages.props`, clean Version attributes from projects, conflict strategies |
| **Dependency analysis** | 10 built-in analyzers + scoreboard; JSON, SARIF, and Markdown for CI |
| **Auto-fix** | Version, casing, redundant refs, transitive pin |
| **Package updates** | Latest versions + `dotnet test` + automatic rollback |
| **`--bisect`** | Largest green update subset; names held-back packages |
| **`Directory.Build.props`** | Unify repeated properties across projects |
| **Batch / monorepo** | Sequential or parallel multi-solution runs |
| **Backup & rollback** | On-disk backups for migration and update paths |
| **`.sln` + `.slnx`** | Classic solutions and Visual Studio 17.10+ `.slnx` |

### Why use CPMigrate instead of doing it by hand?

| Approach | What you get | Where it breaks down |
|----------|--------------|----------------------|
| **Manual CPM migration** | Full control over `Directory.Packages.props` | Slow, easy to miss references, hard to repeat |
| **Ad hoc scripts** | Team-specific automation | Brittle, weak rollback and analysis |
| **Raw `dotnet package list`** | Inventory and vulnerability data | No migration, fixers, or central props generation |
| **CPMigrate** | Migration, analysis, auto-fix, safe updates, batch, CI JSON | Purpose-built for this path |

### Who this is for

- .NET solution owners migrating to `Directory.Packages.props`
- App teams modernizing NuGet package management without hand-editing every project
- Monorepo and multi-solution teams standardizing dependency policy
- CI/CD maintainers who need machine-readable dependency analysis and safe update workflows

## Compatibility

- **.NET SDK:** 8.0+ (tool targets `net10.0` with `LatestMajor` roll-forward)
- **Project types:** `.csproj` / `.fsproj` / `.vbproj`
- **Solutions:** `.sln` and `.slnx`
- **CPM:** generates and consumes standard NuGet Central Package Management files

---

## Table of Contents

- [The problem](#the-problem)
- [What it catches](#what-it-catches)
- [Install](#install)
- [See it work](#see-it-work)
- [30-second path](#30-second-path)
- [Feature snapshot](#feature-snapshot)
- [Compatibility](#compatibility)
- [Quick Start](#quick-start)
- [Features](#features)
  - [CPM Migration](#cpm-migration)
  - [Dependency Analysis](#dependency-analysis)
  - [Auto-Fix](#auto-fix)
  - [Package Updates with Test Verification](#package-updates-with-test-verification)
  - [Bisecting Updates](#bisecting-updates-v36)
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

Every run ends with a scoreboard tallying each analyzer's findings, so a long scroll of
individual tables resolves into one scannable summary:

```text
────────────────────────── ANALYSIS COMPLETE: 2 ISSUES ──────────────────────────

╭─────────────────────────────────────────┬────────┬────────────╮
│ ANALYZER                                │ ISSUES │ SHARE      │
├─────────────────────────────────────────┼────────┼────────────┤
│ ! Version Inconsistencies               │      2 │ ██████████ │
│ ✔ Duplicate Packages (Casing)           │      0 │ ░░░░░░░░░░ │
│ ✔ Transitive Conflicts                  │      0 │ ░░░░░░░░░░ │
│ ✔ Security Vulnerabilities              │      0 │ ░░░░░░░░░░ │
╰─────────────────────────────────────────┴────────┴────────────╯
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

#### Bisecting Updates (v3.6)

**New in v3.6.** Step 11 above is all-or-nothing: one bad package in a set of 38 reverts the other 37 and tells you nothing about which one broke. `--bisect` instead applies the **largest subset that keeps tests green** and names the packages it held back.

```bash
# Keep what works, hold back what doesn't
cpmigrate --update-packages --bisect

# Bound the search, and probe against a fast slice of the suite
cpmigrate --update-packages --bisect --bisect-budget 24 --bisect-test-filter "Category=Unit"

# Follow up on the packages it named
cpmigrate --update-packages --only Serilog,AutoMapper
```

```text
✔ 38 updates applied → tests FAILED
  38 update(s) failed together — narrowing...
  Holding back Serilog 3.1.1 → 4.2.0 (tests failed).

────────────────────────────── BISECT RESULT ──────────────────────────────

  HELD    Serilog: 3.1.1 → 4.2.0 (left at 3.1.1)
  HELD    AutoMapper: 12.0.1 → 14.0.0 (left at 12.0.1)
  APPLIED Polly: 8.4.1 → 8.6.4
  …

Kept 36/38 update(s) with tests green (9 verification run(s)).
  Investigate with: cpmigrate --update-packages --only AutoMapper,Serilog
```

**How the search works:**

- The **whole set is verified first**, so a healthy update run costs exactly one verification and pays no bisection overhead. Only a failure triggers the split.
- On failure the set is halved. A half that verifies clean is **banked** and becomes part of the baseline every later probe builds on; a half that fails is split again until it is a single package, which is then held back.
- Probing against the banked-good set — rather than testing each package in isolation — is what lets it resolve failures that need **two packages together** (a library plus its own updated dependency). A plain binary search assumes one independent culprit and gets these wrong.
- If nothing at all can be kept, CPMigrate verifies the **zero-update baseline** before blaming the packages, so an already-red test suite is reported as such.

**Cost.** Expect roughly `2·log₂(n)` restore+test cycles for a single culprit — about 9–12 runs for a 40-package set. `--bisect-budget` (default 16) caps it. When the budget runs out, whatever is still unresolved is held back and the banked-good set is **still applied**, so an interrupted search delivers partial progress rather than nothing.

**Exit codes.** `0` when the tree ends green and at least one update was applied — check `summary.packagesHeldBack` in `--output Json` to see whether it was a clean sweep or a partial one. `7` (test failure) when nothing could be applied.

> `--bisect` cannot be combined with `--dry-run`: the search has to observe real test runs.

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
| `--fail-on` | | `Info` | Lowest severity that fails the build: `Info`, `Low`, `Moderate`, `High`, `Critical`, or `Never` |
| `--max-parallelism` | | processors (max 8) | Projects queried at once during `--audit` / `--outdated` / `--deprecated` |
| `--baseline` | | | Path to a file of accepted findings; they are reported but do not fail the build |
| `--write-baseline` | | `false` | Record current findings as the accepted baseline, then exit |

#### Gating on severity with `--fail-on`

By default any finding fails the build. That is unusable for a repository with existing debt: the
gate fires on every run, so it gets switched off — and the vulnerability you actually cared about
goes with it. `--fail-on` narrows the gate without narrowing the report.

```bash
# Fail only on High and Critical findings; everything else is still reported
cpmigrate --analyze --audit --outdated --fail-on High

# Report everything, gate on nothing (useful when SARIF upload is the signal)
cpmigrate --analyze --audit --output Sarif --output-file cpmigrate.sarif --fail-on Never
```

Findings below the threshold still appear in terminal, JSON, and SARIF output — only the exit code
changes. Each rule's default severity is listed in [the rule reference](docs/rules.md).

`--fail-on` **cannot** suppress exit `8` ([IncompleteAnalysis](#exit-codes)). A severity threshold
says which findings matter; it does not make an unexamined project safe.

#### Adopting a gate on an existing codebase with `--baseline`

Severity is one axis; the other is *which* findings. A repository that already has debt cannot turn
on a gate that fails on all of it, so record the current state once and gate on what is new:

```bash
# Once, on a green branch — writes .cpmigrate-baseline.json; commit it
cpmigrate --analyze --audit --outdated --write-baseline

# Every run after that: existing debt is reported, new debt fails
cpmigrate --analyze --audit --outdated --baseline .cpmigrate-baseline.json
```

`--baseline` needs an explicit path. To apply it to every run without repeating it, set it in
`.cpmigrate.json` (below).

Baselined findings **stay in every report** — terminal, JSON (`suppressed: true`), and SARIF (as a
`suppressions` entry with `kind: "external"`). The debt stays visible; it just stops blocking.

A finding is identified by its rule, package, and affected projects — deliberately **not** by the
versions in its description. A version inconsistency drifting from `13.0.1, 12.0.3` to
`13.0.2, 12.0.3` is the same unresolved finding, so the suppression holds. Spreading to another
project is new information, so it does not.

When baseline entries stop matching anything — the findings were fixed — CPMigrate says so and
suggests regenerating, which is what stops a baseline growing forever and quietly suppressing a
finding that came back.

A baseline is never recorded from an incomplete scan: if a project fails to scan or an `--audit`
query fails, `--write-baseline` refuses and exits `8` rather than writing a file that permanently
accepts findings nobody looked for.

Set the path once for the team in `.cpmigrate.json`:

```json
{ "baseline": ".cpmigrate-baseline.json", "failOn": "High" }
```

Set it once for the whole team in `.cpmigrate.json`:

```json
{ "failOn": "High" }
```

The JSON payload reports the policy alongside the findings, so a consumer never has to re-derive it:

```json
"summary": {
  "issuesFound": 12,
  "failOnSeverity": "High",
  "issuesAtOrAboveThreshold": 0,
  "highestSeverity": "Moderate",
  "scanFailures": 0,
  "deepScanFailures": 0
}
```

### Package Updates (v3.0+)

| Option | Default | Description |
|--------|---------|-------------|
| `--update-packages` | `false` | Update all packages to latest, run tests, rollback on failure |
| `--transitive` | `false` | Also scan and pin transitive dependencies (v3.1) |
| `--include-prerelease` | `false` | Include pre-release versions when updating |
| `--bisect` | `false` | On failure, keep the largest subset that stays green instead of reverting everything (v3.6) |
| `--bisect-budget` | `16` | Max restore+test cycles a bisection may spend (v3.6) |
| `--bisect-test-filter` | | `dotnet test --filter` expression used for each bisection probe (v3.6) |
| `--only` | | Comma-separated package IDs to restrict the update to (v3.6) |

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
| `--output` | | `Terminal` | Output format: `Terminal`, `Json`, `Sarif`, or `Markdown` (the last two require `--analyze`) |
| `--output-file` | | | Write `Json`, `Sarif`, or `Markdown` output to a file |
| `--quiet` | `-q` | `false` | Suppress non-essential output |
| `--verbose` | `-v` | `false` | Enable diagnostic logging to `cpmigrate.log` |

### Explain a rule

| Option | Description |
|--------|-------------|
| `--explain <RuleId>` | Print what a rule means, why it matters, and how to resolve it |
| `--explain all` | List every rule with a one-line summary |

A rule ID in a build log or a SARIF annotation is exactly where someone needs to know what the rule
means — and exactly where they will not go looking for a docs site. The same IDs appear as
`issueCode` in JSON and `ruleId` in SARIF, so whatever a report names can be pasted straight back:

```bash
cpmigrate --explain InlineVersionUnderCpm
```

A near miss suggests the real rule, and an unrecognised ID exits non-zero so a typo in CI is visible.
Full reference: [docs/rules.md](docs/rules.md).

### Shell Completions

| Option | Default | Description |
|--------|---------|-------------|
| `--completions` | | Print a completion script and exit: `Bash`, `Zsh`, `Fish`, or `PowerShell` |

Generated from the option list rather than hand-written, so it cannot drift out of step with the
CLI — a completion script that suggests flags which no longer exist is worse than none at all.
Enum-valued options complete their values (`--output` offers `Terminal`, `Json`, `Sarif`, `Markdown`),
and path options complete filenames.

```bash
# bash
cpmigrate --completions bash > /usr/local/etc/bash_completion.d/cpmigrate

# zsh — into any directory on $fpath
cpmigrate --completions zsh > "${fpath[1]}/_cpmigrate"

# fish
cpmigrate --completions fish > ~/.config/fish/completions/cpmigrate.fish

# PowerShell
cpmigrate --completions powershell >> $PROFILE
```

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
| `7` | TestFailure | Tests failed after package update (rollback performed). With `--bisect`, returned only when *no* update could be kept |
| `8` | IncompleteAnalysis | A requested scan did not finish, so the findings are incomplete — nothing was necessarily found wrong, but part of the solution went unexamined |

**On `8` (IncompleteAnalysis):** if a project fails to scan, or a `--audit` / `--outdated` /
`--deprecated` query fails, the run produces no findings for the part it could not read. Before
3.7.0 that exited `0`, which a CI gate reads as "clean" — the one failure mode a security gate
exists to prevent. Treat `8` as "re-run or investigate", not as "no issues".

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

### Non-Interactive Terminals

CPMigrate detects when stdout is redirected — a CI runner, a pipe, `> log.txt` — and never
attempts a prompt it cannot service:

- Post-migration verification (`dotnet restore`) is skipped rather than prompted for. It is also
  skipped after `--dry-run`, since nothing was written to verify.
- `--rollback` declines instead of prompting, and tells you to re-run with `--force` to roll back
  unattended.
- Unicode status glyphs (`✔ ✖ ➜ █`) fall back to ASCII equivalents when the terminal reports no
  Unicode support, so build logs stay readable instead of filling with replacement characters.

### No Sub-Commands

CPMigrate is flag-driven. There is no `cpmigrate analyze` verb — the analysis command is
`cpmigrate --analyze`. A leading bare word is rejected rather than ignored, because a discarded
verb would otherwise fall through to the default action and start a real migration:

```text
✖ Unrecognized argument 'analyze'. CPMigrate takes flags, not sub-commands.
› Did you mean: cpmigrate --analyze -s ./MySolution.sln
```

### SARIF for GitHub code scanning

`--output Sarif` emits a [SARIF 2.1.0](https://docs.github.com/code-security/code-scanning/integrating-with-code-scanning/sarif-support-for-code-scanning)
log, so findings appear as annotations on the pull request diff instead of buried in build logs.
Each result points at the project file **and the line declaring the offending `PackageReference`**,
carries the rule's description and a link to [the rule reference](docs/rules.md), and includes a
stable fingerprint so code scanning tracks a finding across runs rather than reopening it.

```bash
cpmigrate --analyze --audit --outdated --deprecated \
  --output Sarif --output-file cpmigrate.sarif --quiet
```

SARIF describes analyzer findings, so `--output Sarif` requires `--analyze`. Severities map to SARIF
levels as `Critical`/`High` → `error`, `Moderate` → `warning`, `Low`/`Info` → `note`.

### Markdown for a job summary or PR comment

SARIF only surfaces findings that map to a line in the diff under review, and a dependency problem
is usually about the solution as a whole — so it never appears on the diff, and nobody goes digging
in build logs for it. `--output Markdown` puts the report where a reviewer will actually see it:

```bash
cpmigrate --analyze --audit --outdated --output Markdown --quiet >> "$GITHUB_STEP_SUMMARY"
```

The report leads with the verdict — did anything reach the `--fail-on` threshold — then scan totals,
a severity breakdown, and a table of findings linking each one to [its rule](docs/rules.md).
Baselined findings are marked. An incomplete scan gets a prominent warning, because "no findings"
from a scan that did not finish reads exactly like a clean result. Long finding lists collapse behind
a `<details>` disclosure so they do not bury the rest of the summary.

To post it as a PR comment instead:

```yaml
- name: Analyze dependencies
  id: analyze
  run: |
    set +e
    cpmigrate --analyze --audit --output Markdown --output-file report.md --quiet
    echo "exit_code=$?" >> "$GITHUB_OUTPUT"

- name: Comment on the PR
  if: github.event_name == 'pull_request'
  run: gh pr comment "${{ github.event.number }}" --body-file report.md
  env:
    GH_TOKEN: ${{ github.token }}
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

Or upload SARIF and let code scanning annotate the diff. Capture the exit code rather than using
`continue-on-error`: that would swallow **every** failure, including exit `8`
([IncompleteAnalysis](#exit-codes)) — leaving the job green on exactly the unexamined-dependency
case the upload is meant to catch. Exit `5` is expected here, because code scanning is the gate:

```yaml
- name: Install CPMigrate
  run: dotnet tool install --global CPMigrate

- name: Analyze dependencies
  id: analyze
  run: |
    set +e
    cpmigrate --analyze --audit --outdated --deprecated \
      --output Sarif --output-file cpmigrate.sarif --quiet
    echo "exit_code=$?" >> "$GITHUB_OUTPUT"

- name: Upload SARIF
  if: always() && hashFiles('cpmigrate.sarif') != ''
  uses: github/codeql-action/upload-sarif@v3
  with:
    sarif_file: cpmigrate.sarif

- name: Require a completed scan
  run: |
    code="${{ steps.analyze.outputs.exit_code }}"
    # 0 = clean. 5 = issues found, already annotated on the diff by code scanning.
    # Anything else (8 = incomplete scan, 1/2/6 = errors) means the results cannot be trusted.
    case "$code" in
      0|5) ;;
      *) echo "::error::cpmigrate exited $code - the analysis did not complete"; exit 1 ;;
    esac
```

### JSON Output

Use `--output Json` to produce machine-readable output for CI/CD pipelines:

```bash
cpmigrate --analyze --output Json --output-file report.json
```

The payload has a **published schema** at
[`schemas/cpmigrate-output.schema.json`](schemas/cpmigrate-output.schema.json), so a parser can be
validated rather than guessed at, and editors will complete a recorded fixture. Key off
`outputSchemaVersion` rather than the tool version — additive changes bump its minor, and the one
field whose meaning has ever changed is called out in the [CHANGELOG](CHANGELOG.md).

Two things worth knowing before writing a parser:

- **`success: true` does not mean "no findings".** Findings below the `--fail-on` threshold, and
  findings a baseline accepted, both leave it true. Check `summary.issuesFound` for whether anything
  was reported at all, and `summary.issuesAtOrAboveThreshold` for what the exit code reflects.
- **Absent fields are meaningful.** `summary` omits counters irrelevant to the command that ran, so
  a missing `issuesBaselined` means no baseline was used — not zero suppressions.

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

### Migration Pipeline

The pipeline renders as a connected stepper — completed stages and the rails behind them light up,
so progress is legible at a glance:

```text
 ✔ DISCOVERY  ───  ✔ ANALYSIS  ───  ▶ BACKUP  ───  ○ MIGRATION  ───  ○ VERIFICATION
```

### Risk Assessment

Before anything is written, CPMigrate scores the migration and shows the reasoning:

```text
┌─ ASSESSMENT ──────────────────────────────────────────────────────────┐
│ Migration Risk: ████████░░░░░░ HIGH (58/100)                          │
│ Impact Area:    12 projects • 7 conflicting package(s)                │
│ Assessment:     Significant version conflicts. Review recommended.    │
└───────────────────────────────────────────────────────────────────────┘
```

```text
┌─ ASSESSMENT ──────────────────────────────────┐
│ Migration Risk: ░░░░░░░░░░░░░░ LOW (0/100)    │
│ Impact Area:    12 projects                   │
│ Assessment:     Clean migration path.         │
└───────────────────────────────────────────────┘
```

### Conflict Resolution

Version conflict tables bold the winning version and dim the ones being dropped, so the resolution
reads without comparing strings:

```text
               VERSION CONFLICTS
╭─────────────────┬────────────────┬──────────╮
│ PACKAGE         │ VERSIONS       │ RESOLVED │
├─────────────────┼────────────────┼──────────┤
│ Newtonsoft.Json │ 13.0.3, 13.0.1 │ ➜ 13.0.3 │
│ Serilog         │ 3.1.1, 3.0.0   │ ➜ 3.1.1  │
╰─────────────────┴────────────────┴──────────╯
```

### Mission Control Dashboard
![CPMigrate Interactive Mission Control wizard for Central Package Management](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/cpmigrate-interactive.gif)
*The interactive wizard assessing migration risk and guiding you through each step.*

### Risk Analysis & Dry Run
![CPMigrate Demo dry-run migration preview](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/cpmigrate-demo.gif)
*Previewing changes safely before committing.*

### Security & Package Analysis
![CPMigrate Analyze dependency and vulnerability scan](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/cpmigrate-analyze.gif)
*Scanning for vulnerabilities and redundant dependencies.*

---

## Contributing

Contributions are welcome. To get started:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Write tests for your changes
4. Ensure all 571 tests pass (`dotnet test`)
5. Open a Pull Request

---

## License

Distributed under the MIT License. See `LICENSE` for more information.

## Author

**George Wall** — [@georgepwall1991](https://github.com/georgepwall1991)

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

<a href="https://github.com/georgepwall1991/CPMigrate/raw/main/assets/video/cpmigrate-hero.mp4">
  <img src="https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/video/cpmigrate-hero-poster.png" alt="CPMigrate in action — migrating a .NET solution to Directory.Packages.props, scoring migration risk, and bisecting NuGet package updates to keep tests green" width="100%" />
</a>

**CPMigrate** is a .NET global tool that migrates solutions to [`Directory.Packages.props`](https://learn.microsoft.com/nuget/consume-packages/central-package-management), analyzes NuGet dependency health, auto-fixes common package issues, and updates packages with test verification and rollback. One command generates the central props file; one more keeps your packages current without breaking the build.

Docs hub: [georgepwall1991.github.io/CPMigrate](https://georgepwall1991.github.io/CPMigrate/)

**Contents:** [The problem](#the-problem) · [What it catches](#what-it-catches) · [Install](#install) · [30-second path](#30-second-path) · [See it work](#see-it-work) · [Feature snapshot](#feature-snapshot) · [FAQ](#faq) · [Features](#features) · [CLI reference](#cli-reference) · [Exit codes](#exit-codes) · [CI/CD integration](#cicd-integration) · [Compatibility](#compatibility)

---

## The problem

Managing NuGet dependencies across large .NET solutions is painful. Version drift, duplicated references, transitive conflicts, and security vulnerabilities accumulate silently until they break your build or compromise your supply chain. Hand-editing every `.csproj` into Central Package Management is slow and easy to get wrong — and "update everything and pray" is not a package strategy. CPMigrate replaces both with a dry-run-first migration, a dependency health scoreboard, and updates that roll themselves back when tests fail.

## What it catches

- **Version inconsistencies** — same package at different versions across projects
- **Duplicate / redundant PackageReferences** — casing duplicates and repeated refs in one project
- **Transitive conflicts** — divergent transitive dependency graphs (optional pin + update)
- **Framework misalignment** — projects on different `TargetFramework` values
- **Security vulnerabilities** — known CVEs via `--audit` (direct and transitive)
- **Outdated / deprecated packages** — inventory checks with `--outdated` / `--deprecated`
- **Scattered versions** — still living in `.csproj` files instead of `Directory.Packages.props`
- **CPM drift** — inline versions that override the central one, references with no version at all, orphaned pins, a props file with central management switched off

## Install

Requires **.NET SDK 8.0** or later. Targets .NET 10 with `LatestMajor` roll-forward.

```bash
dotnet tool install --global CPMigrate --version 3.28.2
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

**First run?** Bare `cpmigrate` launches Mission Control, an interactive wizard that guides you through migration, analysis, updates, and rollback. **Single project?** `cpmigrate --project ./src/Api/Api.csproj --dry-run`. **CI?** Add `--output Json --quiet` to any command for strict JSON-only stdout.

## See it work

Product-flow diagrams rendered from real CLI output, plus a recording of the interactive wizard.

### Dependency analysis scoreboard

![CPMigrate dependency analysis scoreboard — version inconsistencies and health share meters](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-analyze-scoreboard.svg)

### CPM migration before / after

![CPMigrate Central Package Management migration — Directory.Packages.props before and after](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-cpm-migration.svg)

### Safe package updates with --bisect

![CPMigrate package update bisect — keep the largest green update subset with rollback](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-update-bisect.svg)

### Mission Control — the interactive wizard

![CPMigrate Interactive Wizard Mission Control dashboard — guided Central Package Management migration, risk assessment, and dependency analysis](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/cpmigrate-interactive.gif)

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

---

## FAQ

### How do I migrate a .NET solution to Central Package Management?

Run `cpmigrate -s ./MySolution.sln --dry-run` to preview, then `cpmigrate -s ./MySolution.sln` to apply. CPMigrate extracts every `<PackageReference>` from your `.csproj` / `.fsproj` / `.vbproj` files, resolves version conflicts with a defined strategy, generates `Directory.Packages.props`, and strips the inline `Version` attributes — with a timestamped backup you can roll back to.

### What is Directory.Packages.props?

It is the file NuGet Central Package Management (CPM) reads package versions from, so every project in a solution shares one version per package instead of declaring its own. CPMigrate generates and maintains it for you. [Microsoft's CPM docs](https://learn.microsoft.com/nuget/consume-packages/central-package-management) cover the format itself.

### Can CPMigrate roll back a bad package update?

Yes, two ways. `--update-packages` runs `dotnet test` after applying updates and automatically rolls back if tests fail. `--update-packages --bisect` goes further: instead of reverting all 38 updates because one broke, it keeps the largest subset that stays green and names the packages it held back. Migrations get timestamped backups restorable with `cpmigrate --rollback`.

### Does CPMigrate work in CI/CD?

It is built for it. `--output Json --quiet` gives strict JSON-only stdout against a [published schema](schemas/cpmigrate-output.schema.json); `--output Sarif` uploads to GitHub code scanning as PR annotations; `--output Markdown` drops a verdict-first report into `$GITHUB_STEP_SUMMARY`. Exit codes are contract-level — `5` means findings, `8` means the scan did not complete. See [CI/CD integration](#cicd-integration).

### Does it support .slnx and monorepos?

Yes. CPMigrate reads both classic `.sln` and Visual Studio 17.10+ `.slnx` solutions, and `--batch /path/to/repo` recursively discovers every solution in a monorepo — optionally in parallel (`--batch-parallel`) and continuing past failures (`--batch-continue`). Each solution gets an isolated backup directory.

### Can it gate on security vulnerabilities without failing on existing debt?

Yes. `--audit` scans direct and transitive dependencies for known CVEs; `--fail-on High` narrows the exit-code gate to severities you care about while still reporting everything; `--write-baseline` records today's findings once, so CI fails only on *new* debt. Baselined findings stay visible in every report — terminal, JSON, and SARIF.

### Which .NET versions does CPMigrate support?

The tool targets .NET 10 and rolls forward (`LatestMajor`); it runs on any machine with **.NET SDK 8.0+**. Your projects can target anything — CPMigrate edits project XML directly and does not build your solution except when you ask it to verify updates with `dotnet test`.

---

## Features

### CPM Migration

Scans your `.sln` or `.slnx`, extracts all `<PackageReference>` entries, resolves version conflicts, and generates a centralized `Directory.Packages.props`.

```bash
cpmigrate -s ./MySolution.sln                       # standard migration
cpmigrate -s ./MySolution.sln --merge               # merge into existing props
cpmigrate -s ./MySolution.sln --conflict-strategy Fail    # strict mode
cpmigrate -s ./MySolution.sln --interactive-conflicts     # prompt per conflict
```

| Strategy | Behavior |
|----------|----------|
| `Highest` | Use the highest version found across projects (default) |
| `Lowest` | Use the lowest version found |
| `Fail` | Exit with error if any package has conflicting versions |

---

### Dependency Analysis

Run the built-in analyzers without modifying any files:

```bash
cpmigrate --analyze                                 # core analyzers
cpmigrate --analyze --transitive                    # + transitive dependencies
cpmigrate --analyze --audit                         # + security vulnerability scanning
cpmigrate --analyze --outdated --deprecated         # + outdated / deprecated checks
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

Every run ends with a scoreboard tallying each analyzer's findings:

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

```bash
cpmigrate --analyze --fix                           # fix all auto-fixable issues
cpmigrate --analyze --fix-dry-run                   # preview what would be fixed
```

| Fixer | What it fixes |
|-------|---------------|
| **Version Inconsistency Fixer** | Standardizes package versions across projects using the configured conflict strategy |
| **Duplicate Package Casing Fixer** | Normalizes package name casing to the most common variant |
| **Redundant Reference Fixer** | Removes duplicate `<PackageReference>` entries within the same project |
| **Transitive Conflict Pinner** | Pins divergent transitive dependencies in `Directory.Packages.props` |

---

### Package Updates with Test Verification

Update all NuGet packages to their latest versions with automatic test verification and rollback. Requires CPM — run `cpmigrate` first if `Directory.Packages.props` does not exist.

```bash
cpmigrate --update-packages --dry-run               # preview available updates
cpmigrate --update-packages                         # update, test, rollback on failure
cpmigrate --update-packages --transitive            # also scan and pin transitive deps
cpmigrate --update-packages --include-prerelease    # include pre-release versions
```

**How it works:** reads current versions from `Directory.Packages.props`, queries NuGet for latest (8 concurrent lookups), auto-accepts minor/patch bumps and prompts for major ones, backs up the props file, applies updates atomically, then runs `dotnet restore` + `dotnet test`. Tests pass — updates kept. Tests fail — everything rolls back.

With `--transitive`, CPMigrate additionally scans `dotnet list package --include-transitive`, deduplicates across projects (highest resolved version wins), excludes deps already managed directly, and pins accepted transitive updates as new `<PackageVersion>` entries. Per-project scan failures are logged and skipped; if every scan fails it continues direct-only. Transitive scanning requires `dotnet restore` to have run beforehand.

#### Bisecting Updates

All-or-nothing rollback is blunt: one bad package in a set of 38 reverts the other 37 and tells you nothing about which one broke. `--bisect` applies the **largest subset that keeps tests green** and names the packages it held back.

```bash
cpmigrate --update-packages --bisect
cpmigrate --update-packages --bisect --bisect-budget 24 --bisect-test-filter "Category=Unit"
cpmigrate --update-packages --only Serilog,AutoMapper   # follow up on held packages
```

```text
────────────────────────────── BISECT RESULT ──────────────────────────────

  HELD    Serilog: 3.1.1 → 4.2.0 (left at 3.1.1)
  HELD    AutoMapper: 12.0.1 → 14.0.0 (left at 12.0.1)
  APPLIED Polly: 8.4.1 → 8.6.4
  …

Kept 36/38 update(s) with tests green (9 verification run(s)).
  Investigate with: cpmigrate --update-packages --only AutoMapper,Serilog
```

**How the search works.** The whole set is verified first, so a healthy run costs exactly one verification. On failure the set is halved: a clean half is **banked** into the baseline every later probe builds on; a failing half is split again until a single package is held back. Probing against the banked-good set — rather than each package alone — resolves failures that need **two packages together**. If nothing can be kept, the zero-update baseline is verified first, so an already-red suite is reported as such.

**Cost.** Roughly `2·log₂(n)` restore+test cycles for a single culprit — about 9–12 runs for 40 packages. `--bisect-budget` (default 16) caps it; when the budget runs out, unresolved packages are held back and the banked-good set is **still applied**.

**Exit codes.** `0` when the tree ends green and at least one update was applied — check `summary.packagesHeldBack` in `--output Json` to tell a clean sweep from a partial one. `7` when nothing could be applied. `--bisect` cannot be combined with `--dry-run`: the search has to observe real test runs.

---

### Directory.Build.props Unification

Promote repeated properties and items from individual project files into a shared `Directory.Build.props`:

```bash
cpmigrate --unify-props --dry-run                   # preview
cpmigrate --unify-props                             # apply
cpmigrate --unify-props --force                     # skip confirmation
```

Identifies properties and items present in at least 60% of projects with the same value (e.g., `TargetFramework`, `ImplicitUsings`, `Nullable`, `Authors`) and migrates them to the root-level file. Individual project files are cleaned up automatically.

---

### Batch Processing

```bash
cpmigrate --batch /path/to/repo                     # sequential
cpmigrate --batch /path/to/repo --batch-parallel    # all CPU cores
cpmigrate --batch /path/to/repo --batch-parallel --batch-continue
```

Recursively discovers `.sln` and `.slnx` files, excluding common non-project directories (`node_modules`, `bin`, `obj`, `.git`, etc.). Each solution gets an isolated backup directory to prevent collisions.

---

### Backup & Rollback

Every migration creates a timestamped backup in `.cpmigrate_backup/` with a JSON manifest.

```bash
cpmigrate --rollback                                # restore most recent backup
cpmigrate --list-backups                            # list all backups
cpmigrate --prune-backups --retention 3             # keep the last 3
cpmigrate --prune-all                               # delete all backups
```

Use `--add-gitignore` to add the backup directory to `.gitignore` automatically.

---

### Configuration File

Create a `.cpmigrate.json` in your repository root to set team defaults (CLI arguments always win):

```json
{
  "$schema": "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate.schema.json",
  "ConflictStrategy": "Highest",
  "Backup": true,
  "BackupDir": ".",
  "AddGitignore": true,
  "MergeExisting": false,
  "OutputFormat": "Terminal",
  "failOn": "High",
  "baseline": ".cpmigrate-baseline.json",
  "Retention": {
    "Enabled": true,
    "MaxBackups": 5
  }
}
```

The config file is discovered by walking up from the selected solution/project path, or from the current directory when no path is provided.

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

**Gating with `--fail-on`.** By default any finding fails the build — unusable on a repo with existing debt, so the gate gets switched off and the vulnerability you cared about goes with it. `--fail-on High` narrows the gate without narrowing the report: sub-threshold findings still appear in terminal, JSON, and SARIF output; only the exit code changes. `--fail-on` **cannot** suppress exit `8` — a severity threshold says which findings matter; it does not make an unexamined project safe.

```bash
cpmigrate --analyze --audit --outdated --fail-on High
cpmigrate --analyze --audit --output Sarif --output-file cpmigrate.sarif --fail-on Never
```

**Adopting a gate on an existing codebase with `--baseline`.** Record the current state once on a green branch, then gate on what is new:

```bash
cpmigrate --analyze --audit --outdated --write-baseline   # writes .cpmigrate-baseline.json; commit it
cpmigrate --analyze --audit --outdated --baseline .cpmigrate-baseline.json
```

Baselined findings **stay in every report** — terminal, JSON (`suppressed: true`), and SARIF (`suppressions` with `kind: "external"`). A finding is identified by rule, package, and affected projects — not by the versions in its description — so a version drifting `13.0.1 → 13.0.2` stays suppressed, while spreading to another project does not. When entries stop matching anything, CPMigrate suggests regenerating. A baseline is never recorded from an incomplete scan: if a project or query fails to scan, `--write-baseline` exits `8` rather than permanently accepting findings nobody looked for. Set both once for the team in `.cpmigrate.json`: `{ "baseline": ".cpmigrate-baseline.json", "failOn": "High" }`.

The JSON payload reports the policy alongside the findings:

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

### Package Updates

| Option | Default | Description |
|--------|---------|-------------|
| `--update-packages` | `false` | Update all packages to latest, run tests, rollback on failure |
| `--transitive` | `false` | Also scan and pin transitive dependencies |
| `--include-prerelease` | `false` | Include pre-release versions when updating |
| `--bisect` | `false` | On failure, keep the largest subset that stays green instead of reverting everything |
| `--bisect-budget` | `16` | Max restore+test cycles a bisection may spend |
| `--bisect-test-filter` | | `dotnet test --filter` expression used for each bisection probe |
| `--only` | | Comma-separated package IDs to restrict the update to |

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
| `--gitignore-dir` | | `.` | Directory to create `.gitignore` in, when there is not one already |

### Output & Logging

| Option | Short | Default | Description |
|--------|-------|---------|-------------|
| `--output` | | `Terminal` | Output format: `Terminal`, `Json`, `Sarif`, or `Markdown` (the last two require `--analyze`) |
| `--output-file` | | | Write `Json`, `Sarif`, or `Markdown` output to a file |
| `--quiet` | `-q` | `false` | Suppress non-essential output |
| `--verbose` | `-v` | `false` | Enable diagnostic logging to `cpmigrate.log` |

### Rules, Completions & Self-Update

| Option | Description |
|--------|-------------|
| `--explain <RuleId>` | Print what a rule means, why it matters, and how to resolve it (`--explain all` lists every rule) |
| `--doctor` | Diagnose your environment: SDK version, NuGet connectivity, workspace, config, and git status |
| `--init` | Create a `.cpmigrate.json` config file with team defaults (interactive or CI-safe defaults) |
| `--completions <Shell>` | Print a completion script and exit: `Bash`, `Zsh`, `Fish`, or `PowerShell` |
| `--update` | Check for and install the latest version of CPMigrate |

Rule IDs from build logs or SARIF annotations (`issueCode` / `ruleId`) paste straight back: `cpmigrate --explain InlineVersionUnderCpm`. A near miss suggests the real rule; an unrecognized ID exits non-zero so a typo in CI is visible. Full reference: [docs/rules.md](docs/rules.md). Completions are generated from the live option list — enum values and paths complete too — so they cannot drift from the CLI.

```bash
cpmigrate --completions bash > /usr/local/etc/bash_completion.d/cpmigrate
cpmigrate --completions zsh > "${fpath[1]}/_cpmigrate"
cpmigrate --completions fish > ~/.config/fish/completions/cpmigrate.fish
cpmigrate --completions powershell >> $PROFILE
```

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

**On `8` (IncompleteAnalysis):** if a project fails to scan, or an `--audit` / `--outdated` / `--deprecated` query fails, the run produces no findings for the part it could not read. Treat `8` as "re-run or investigate", not as "no issues" — it is exactly the failure mode a security gate exists to prevent.

---

## CI/CD Integration

### Strict JSON contract mode

`--output Json --quiet` guarantees JSON-only stdout — safe for CI parsing without stripping banners or config notices:

```bash
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analyze.json
cpmigrate -s ./MySolution.sln --dry-run --output Json --quiet > migrate.json
cpmigrate --update-packages --dry-run --output Json --quiet > update-packages.json
```

The payload has a **published schema** at [`schemas/cpmigrate-output.schema.json`](schemas/cpmigrate-output.schema.json) — key off `outputSchemaVersion`, not the tool version. Two things before writing a parser: **`success: true` does not mean "no findings"** (check `summary.issuesFound` and `summary.issuesAtOrAboveThreshold`), and **absent fields are meaningful** (a missing `issuesBaselined` means no baseline was used, not zero suppressions).

### Non-interactive terminals

CPMigrate detects redirected stdout — a CI runner, a pipe, `> log.txt` — and never attempts a prompt it cannot service: post-migration verification is skipped rather than prompted for (and after `--dry-run`, since nothing was written); `--rollback` declines and says to re-run with `--force`; Unicode glyphs fall back to ASCII when the terminal reports no Unicode support.

### No sub-commands

CPMigrate is flag-driven — the analysis command is `cpmigrate --analyze`, not `cpmigrate analyze`. A leading bare word is rejected rather than ignored, because a discarded verb would otherwise fall through to a real migration:

```text
✖ Unrecognized argument 'analyze'. CPMigrate takes flags, not sub-commands.
› Did you mean: cpmigrate --analyze -s ./MySolution.sln
```

### SARIF for GitHub code scanning

`--output Sarif` emits a [SARIF 2.1.0](https://docs.github.com/code-security/code-scanning/integrating-with-code-scanning/sarif-support-for-code-scanning) log — findings appear as annotations on the PR diff, each pointing at the project file **and the line declaring the offending `PackageReference`**, with a stable fingerprint so code scanning tracks findings across runs. Severities map as `Critical`/`High` → `error`, `Moderate` → `warning`, `Low`/`Info` → `note`. (`--output Sarif` requires `--analyze`, since SARIF describes analyzer findings.)

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

Capture the exit code rather than using `continue-on-error`: that would swallow **every** failure, including exit `8` — leaving the job green on exactly the unexamined-dependency case the upload is meant to catch.

### Markdown for a job summary or PR comment

SARIF only surfaces findings that map to a line in the diff, and a dependency problem is usually about the solution as a whole — so it never appears on the diff. `--output Markdown` puts the report where a reviewer will actually see it:

```bash
cpmigrate --analyze --audit --outdated --output Markdown --quiet >> "$GITHUB_STEP_SUMMARY"
```

The report leads with the verdict against the `--fail-on` threshold, then scan totals, a severity breakdown, and findings linking to [their rules](docs/rules.md). Baselined findings are marked, incomplete scans get a prominent warning, and long finding lists collapse behind a `<details>` disclosure. Post it as a PR comment with `gh pr comment "${{ github.event.number }}" --body-file report.md`. Dedicated guide: https://georgepwall1991.github.io/CPMigrate/guides/ci-cd/

---

## Compatibility

- **.NET SDK:** 8.0+ (tool targets `net10.0` with `LatestMajor` roll-forward)
- **Project types:** `.csproj` / `.fsproj` / `.vbproj`
- **Solutions:** `.sln` and `.slnx`
- **CPM:** generates and consumes standard NuGet Central Package Management files

## Examples & Benchmarks

- Starter example: `examples/small-solution/`
- Monorepo example: `examples/monorepo/`
- Benchmark table: `docs/benchmarks.md`

These sample repositories are designed for onboarding, CI templates, and reproducible before/after conversion demos.

## Release Cadence

- **Stable releases:** weekly, versioned and changeloged
- **Release candidates (RC):** published for fast feedback before stable promotion
- **Change log source of truth:** `CHANGELOG.md`
- **Policy details:** `docs/release-cadence.md`

## Telemetry (Opt-in)

CPMigrate supports privacy-first telemetry that is **disabled by default**.

- Enable by setting `CPMIGRATE_TELEMETRY_OPT_IN=true`
- Captures only command-level metrics (operation, duration, exit code category, high-level flags)
- Captures **no** project paths, package names, file contents, or source code
- Stores local events at `~/.cpmigrate/telemetry/events.ndjson`

## Contributing

Contributions are welcome:

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Write tests for your changes
4. Ensure the full suite passes (`dotnet test`)
5. Open a Pull Request

## License

Distributed under the MIT License. See `LICENSE` for more information.

## Author

**George Wall** — [@georgepwall1991](https://github.com/georgepwall1991)

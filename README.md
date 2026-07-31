<div align="center">

<img src="https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/logo.png" alt="CPMigrate logo" width="132" />

# CPMigrate

### Stop hand-editing `.csproj` files. Stop "update everything and pray."
### One binary migrates you to Central Package Management, scores the rot, and ships updates that **refuse** to break your build.

<img src="https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/banner.png" alt="CPMigrate — migrate, analyze, and update NuGet packages" width="100%" />

[![NuGet](https://img.shields.io/nuget/v/CPMigrate.svg?style=for-the-badge&logo=nuget&logoColor=white&label=nuget&color=0099CC)](https://www.nuget.org/packages/CPMigrate/)
[![.NET](https://img.shields.io/badge/.NET-8.0+-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Downloads](https://img.shields.io/nuget/dt/CPMigrate.svg?style=for-the-badge&color=blue&label=downloads)](https://www.nuget.org/packages/CPMigrate/)
[![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)](LICENSE)

**[Install](#install-)** · **[30-second path](#30-second-path-)** · **[See it work](#see-it-work-)** · **[CLI reference](#-cli-reference)** · **[Docs site ↗](https://georgepwall1991.github.io/CPMigrate/)**

</div>

> You inherit a 40-project solution. `Newtonsoft.Json` is at six different versions. Something transitive has a CVE nobody's looked at since 2023. The intern's migration PR touched every `.csproj` by hand and missed three. **CPMigrate is the binary that makes that a one-command problem** — and then keeps it solved.

---

## The problem 🎯

NuGet dependency drift is a slow leak. Version sprawl, duplicate references, transitive conflicts, and unpatched CVEs pile up silently — until the build breaks at the worst possible moment or an audit finds a vulnerable package you didn't know you shipped. Hand-migrating to Central Package Management is tedious and lossy; updating packages blind is Russian roulette with your test suite.

CPMigrate replaces both with three things that actually hold up:

- 🔍 **A dry-run-first migration** that shows you the exact `Directory.Packages.props` diff before it touches a byte.
- 📊 **A dependency health scoreboard** — 11 analyzers, a 0–100 score, severity-gated CI exits.
- 🛡️ **Updates that roll themselves back** the instant `dotnet test` goes red — and with `--bisect`, keep the largest subset that stays green instead of nuking all 38 because one broke.

---

## What it catches 🕵️

| | Rule | What it finds | Severity |
|:--:|------|---------------|:--:|
| 🟥 | **SecurityVulnerability** | Known CVEs in direct *and* transitive deps (`--audit`) | Critical |
| 🟧 | **InlineVersionUnderCpm** | CPM drift — inline overrides, missing pins, orphaned entries, CPM switched off | High |
| 🟧 | **LicenseRisk** | Copyleft (GPL/AGPL) & proprietary licenses (`--licenses`) | High |
| 🟨 | **VersionInconsistency** | Same package, different versions across projects | Moderate |
| 🟨 | **TransitiveConflict** | Divergent transitive graphs (auto-pinnable) | Moderate |
| 🟦 | **DuplicatePackageCasing** | `Newtonsoft.Json` vs `newtonsoft.json` | Low |
| 🟦 | **RedundantReference** | The same `PackageReference` twice in one project | Low |
| 🟦 | **OutdatedPackage** / **DeprecatedPackage** | Behind the feed / abandoned packages | Low |
| ⬜ | **FrameworkAlignment** | Projects drifting across `TargetFramework` values | Info |

Every finding carries a stable rule ID — paste it straight into `cpmigrate --explain <RuleId>` for the why and the fix. Full reference: [`docs/rules.md`](docs/rules.md).

---

## 30-second path ⚡

```bash
# 0 · is my machine even ready?
cpmigrate --doctor

# 1 · score the rot (CI-safe exit codes: 0 clean · 5 findings · 8 incomplete)
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analysis.json

# 2 · preview the migration as a unified diff — nothing written
cpmigrate -s ./MySolution.sln --dry-run --diff

# 3 · migrate to Central Package Management
cpmigrate -s ./MySolution.sln

# 4 · update packages; tests fail → automatic rollback
cpmigrate --update-packages --bisect
```

**No flags?** Bare `cpmigrate` drops you into **Mission Control**, an interactive wizard. **One project?** `cpmigrate --project ./src/Api/Api.csproj --dry-run`. **Monorepo?** `cpmigrate --batch ./repo --batch-parallel`. **Team defaults?** `cpmigrate --init` scaffolds a `.cpmigrate.json`.

---

## Install 📦

Requires **.NET SDK 8.0** or later. The tool itself targets `net10.0` with `LatestMajor` roll-forward.

```bash
dotnet tool install --global CPMigrate --version 3.51.0
```

```bash
dotnet tool update --global CPMigrate     # or:  cpmigrate --update
```

| Channel | Command |
|---------|---------|
|  **Homebrew** | `brew tap georgepwall1991/cpmigrate && brew install cpmigrate` |
| 🪟 **Winget** | `winget install GeorgeWall.CPMigrate` |
| 📦 **Windows portable** | `CPMigrate-portable-win-x64.zip` from [Releases](https://github.com/georgepwall1991/CPMigrate/releases) |
| ‍💻 **From source** | `git clone https://github.com/georgepwall1991/CPMigrate.git && dotnet build` |

> Indexing lag after a fresh release? `dotnet nuget locals http-cache --clear`

---

## See it work 🎬

### The scoreboard — a 0–100 health meter, not a wall of text

```text
────────────────────────── ! ANALYSIS COMPLETE — 4 ISSUES ──────────────────────────

              ╭──────────────── ANALYZER SCOREBOARD ────────────────╮
              │ ANALYZER                       │ ISSUES │  STATUS   │
              ├────────────────────────────────┼────────┼───────────┤
              │ ! Version Inconsistencies      │      3 │  3 FOUND  │
              │ ✖ Security Vulnerabilities     │      1 │  1 FOUND  │
              │ ✔ Duplicate Packages (Casing)  │      0 │     PASS  │
              │ ✔ Transitive Conflicts         │      0 │     PASS  │
              │ ✔ Redundant References         │      0 │     PASS  │
              ╰────────────────────────────────╯

   Dependency Health  ██████████████████░░░  78/100  GOOD
```

![Dependency analysis scoreboard — version inconsistencies and health share meters](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-analyze-scoreboard.svg)

### The migration — see the diff before it lands

![CPM migration before / after — Directory.Packages.props](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-cpm-migration.svg)

### The bisect — keep 36/38 instead of reverting all 38

![Package update bisect — keep the largest green subset with rollback](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/assets/flow-update-bisect.svg)

### Mission Control — the interactive wizard

![CPMigrate interactive wizard — guided CPM migration, risk assessment, dependency analysis](https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/docs/images/cpmigrate-interactive.gif)

---

## Feature snapshot 🧩

| Surface | What you get |
|---------|--------------|
| 🏗️ **CPM migration** | Generate `Directory.Packages.props`, strip inline versions, conflict strategies, `--merge` |
| 🔬 **Dependency analysis** | 11 analyzers + scoreboard + 0–100 health score; JSON / SARIF / Markdown / **CSV** |
| 🩹 **Auto-fix** | Version, casing, redundant refs, transitive pin |
| 🔁 **Safe updates** | Latest versions + `dotnet test` + automatic rollback |
| 🔪 **`--bisect`** | Largest green update subset; names the held-back packages |
| 🧱 **`Directory.Build.props`** | Unify repeated properties across projects |
| 🏢 **Batch / monorepo** | Sequential or parallel multi-solution runs |
| 💾 **Backup & rollback** | Timestamped on-disk backups for every destructive path |
| 📄 **`.sln` + `.slnx`** | Classic solutions and Visual Studio 17.10+ `.slnx` |
| 🩺 **`--doctor`** | Environment diagnostics: SDK, NuGet, workspace, config, git |
|  **`--init`** | Scaffold `.cpmigrate.json` with team defaults |
| 📟 **`--status`** | One-shot workspace health dashboard |
| 🌳 **`--tree`** | ASCII dependency tree, direct + transitive |
| 🔀 **`--diff`** | Unified diff preview on `--dry-run` |

### Why not just do it by hand?

| | Manual CPM | Ad-hoc scripts | `dotnet package list` | **CPMigrate** |
|---|:--:|:--:|:--:|:--:|
| Generates `Directory.Packages.props` | ✋ | ⚠️ | ✖ | ✅ |
| Conflict resolution strategy | ✖ | ️ | ✖ | ✅ |
| Dependency health scoreboard | ✖ | ✖ | ✖ | ✅ |
| Auto-fixers | ✖ | ️ | ✖ | ✅ |
| Test-verified updates + rollback | ✖ | ✖ | ✖ | ✅ |
| Bisect to keep green subset | ✖ | ✖ | ✖ | ✅ |
| Machine-readable CI output | ✖ | ️ | ️ | ✅ |
| Repeatable across a monorepo | ✖ | ⚠️ | ✖ | ✅ |

### Who this is for

- 🧑‍💼 **Solution owners** dragging a codebase onto `Directory.Packages.props`
- 🛠️ **App teams** modernizing package management without a hand-edited migration PR
- 🏙️ **Monorepo / multi-solution teams** standardizing one dependency policy
- 🤖 **CI/CD maintainers** who need gates that can't be fooled by an incomplete scan

---

## 🖥️ The whole toolbox

<details open>
<summary><b>🩺 Diagnostics &amp; workspace</b> — know your state before you change it</summary>

```bash
cpmigrate --doctor                 # SDK, NuGet reachability, workspace, git — one table
cpmigrate --status                 # repo-context dashboard, no wizard
cpmigrate --tree --transitive      # ASCII dependency tree per project
cpmigrate --init                   # scaffold .cpmigrate.json (interactive or CI-safe)
```

</details>

<details open>
<summary><b>🔬 Analysis &amp; auto-fix</b> — find the rot, then fix it</summary>

```bash
cpmigrate --analyze --audit --outdated --deprecated --licenses
cpmigrate --analyze --fix                       # apply every auto-fixable finding
cpmigrate --analyze --fix-dry-run               # preview the fixes
cpmigrate --analyze --fail-on High              # gate CI without failing on old debt
cpmigrate --analyze --write-baseline            # accept today's debt; fail only on new
```

</details>

<details open>
<summary><b>🔁 Updates &amp; bisect</b> — move forward without fear</summary>

```bash
cpmigrate --update-packages --dry-run
cpmigrate --update-packages                     # update · test · rollback on red
cpmigrate --update-packages --bisect            # keep the largest green subset
cpmigrate --update-packages --only Serilog,Polly   # chase the held-back ones
```

</details>

---

## 📚 CLI reference

> Every flag, in one place. Collapsible so the page stays scannable.

<details open>
<summary><b>Diagnostics &amp; workspace</b></summary>

| Option | Default | Description |
|--------|:-------:|-------------|
| `--doctor` | `false` | Diagnose the environment: SDK, NuGet, workspace, config, git |
| `--init` | `false` | Scaffold a `.cpmigrate.json` (interactive, or CI-safe defaults) |
| `--status` | `false` | One-shot workspace health dashboard |
| `--tree` | `false` | ASCII dependency tree per project (add `--transitive` for the full graph) |

</details>

<details open>
<summary><b>Migration &amp; core</b></summary>

| Option | Short | Default | Description |
|--------|:-----:|:-------:|-------------|
| `--solution` | `-s` | cwd | Path to a `.sln` / `.slnx` file or directory |
| `--project` | `-p` | | A specific project file, or a directory holding one |
| `--output-dir` | `-o` | `.` | Where `Directory.Packages.props` is written |
| `--dry-run` | `-d` | `false` | Preview changes without modifying files |
| `--diff` | | `false` | Render a unified diff during `--dry-run` |
| `--merge` | | `false` | Merge into an existing props file instead of failing |
| `--conflict-strategy` | | `Highest` | `Highest` · `Lowest` · `Fail` |
| `--interactive-conflicts` | | `false` | Prompt for each version conflict |
| `--keep-attrs` | `-k` | `false` | Leave inline `Version` attributes in place |
| `--interactive` | `-i` | `false` | Launch the Mission Control wizard |

</details>

<details open>
<summary><b>Analysis &amp; auto-fix</b></summary>

| Option | Short | Default | Description |
|--------|:-----:|:-------:|-------------|
| `--analyze` | `-a` | `false` | Run dependency health analysis |
| `--transitive` | | `false` | Include transitive dependencies |
| `--audit` | | `false` | Security vulnerability scanning |
| `--outdated` | | `false` | Outdated package checks |
| `--deprecated` | | `false` | Deprecated package checks |
| `--licenses` | | `false` | Flag copyleft / proprietary / unknown licenses |
| `--fix` | | `false` | Apply auto-fixes (with `--analyze`) |
| `--fix-dry-run` | | `false` | Preview auto-fixes |
| `--fail-on` | | `Info` | Lowest severity that fails: `Info`·`Low`·`Moderate`·`High`·`Critical`·`Never` |
| `--max-parallelism` | | procs (≤8) | Projects scanned at once for `--audit`/`--outdated`/`--deprecated` |
| `--baseline` | | | Accepted-findings file; reported but never fail the build |
| `--write-baseline` | | `false` | Record current findings as the baseline, then exit |

**Gating on a codebase with existing debt.** `--fail-on High` narrows the *gate* without narrowing the *report* — sub-threshold findings still show in terminal, JSON, and SARIF; only the exit code changes. It can **never** suppress exit `8` (incomplete scan). Record the current state once, then fail only on what's new:

```bash
cpmigrate --analyze --audit --write-baseline              # commit .cpmigrate-baseline.json
cpmigrate --analyze --audit --baseline .cpmigrate-baseline.json
```

Baselined findings stay visible everywhere (`suppressed: true` in JSON, `kind: "external"` in SARIF). A finding is keyed by rule + package + projects — a version drifting `13.0.1 → 13.0.2` stays suppressed; spreading to a new project does not.

</details>

<details open>
<summary><b>Package updates</b></summary>

| Option | Default | Description |
|--------|:-------:|-------------|
| `--update-packages` | `false` | Update all packages, test, rollback on failure |
| `--include-prerelease` | `false` | Include pre-release versions |
| `--bisect` | `false` | Keep the largest green subset instead of reverting all |
| `--bisect-budget` | `16` | Max restore+test cycles a bisection may spend |
| `--bisect-test-filter` | | `dotnet test --filter` expression per probe |
| `--only` | | Comma-separated package IDs to restrict the update to |

**How `--bisect` thinks.** The whole set is verified first (one run if it's healthy). On failure it halves: a clean half is *banked* into the baseline every later probe builds on; a failing half splits again until one package is held back. Probing against the banked-good set — not each package alone — catches failures that need *two* packages together. Cost ≈ `2·log₂(n)` cycles. Exit `0` when green with ≥1 applied (check `summary.packagesHeldBack` in JSON for a partial), `7` when nothing could be kept. `--bisect` can't combine with `--dry-run`.

</details>

<details>
<summary><b>Modernization · batch · backup · output · rules</b></summary>

**Modernization**

| Option | Default | Description |
|--------|:-------:|-------------|
| `--unify-props` | `false` | Promote common properties to `Directory.Build.props` |
| `--force` | `false` | Skip confirmation prompts |

**Batch processing**

| Option | Default | Description |
|--------|:-------:|-------------|
| `--batch` | | Recursively scan a directory for solutions |
| `--batch-parallel` | `false` | Process solutions in parallel |
| `--batch-continue` | `false` | Continue past a failing solution |

**Backup & rollback**

| Option | Short | Default | Description |
|--------|:-----:|:-------:|-------------|
| `--rollback` | `-r` | `false` | Restore the most recent backup |
| `--no-backup` | `-n` | `false` | Disable backup creation |
| `--backup-dir` | | `.` | Backup directory location |
| `--list-backups` | | `false` | List backups with timestamps & file counts |
| `--prune-backups` | | `false` | Delete old backups per `--retention` |
| `--prune-all` | | `false` | Delete all backups |
| `--retention` | | `5` | Backups to keep when pruning |
| `--add-gitignore` | | `false` | Add the backup dir to `.gitignore` |
| `--gitignore-dir` | | `.` | Where to create `.gitignore` if missing |

**Output & logging**

| Option | Short | Default | Description |
|--------|:-----:|:-------:|-------------|
| `--output` | | `Terminal` | `Terminal` · `Json` · `Sarif` · `Markdown` · `Csv` (last four need `--analyze`) |
| `--output-file` | | | Write `Json`/`Sarif`/`Markdown` to a file |
| `--quiet` | `-q` | `false` | Suppress non-essential output |
| `--verbose` | `-v` | `false` | Diagnostic logging to `cpmigrate.log` |

**Rules, completions & self-update**

| Option | Description |
|--------|-------------|
| `--explain <RuleId>` | What a rule means, why it matters, how to fix it (`--explain all` lists every rule) |
| `--completions <Shell>` | Emit a completion script and exit: `Bash` · `Zsh` · `Fish` · `PowerShell` |
| `--update` | Check for and install the latest CPMigrate |

Completions are generated from the live option list — enums and paths complete too, so they can't drift. `--explain` IDs paste straight from build logs and SARIF (`issueCode` / `ruleId`); a near-miss suggests the real rule, an unknown ID exits non-zero so a CI typo is visible.

</details>

---

## 🚪 Exit codes

The contract a CI gate is written against — and the one thing a script can't discover by trying.

| Code | Name | Meaning |
|:----:|------|---------|
| `0` | Success | Operation completed successfully |
| `1` | ValidationError | Invalid command-line options |
| `2` | FileOperationError | File I/O or permission failure |
| `3` | VersionConflict | Unresolvable conflict (with `--conflict-strategy Fail`) |
| `4` | NoProjectsFound | No `.csproj` / `.fsproj` / `.vbproj` files discovered |
| `5` | AnalysisIssuesFound | Analysis detected issues (your CI gate) |
| `6` | UnexpectedError | Unhandled exception |
| `7` | TestFailure | Tests failed after update (rollback done); with `--bisect`, only when *nothing* could be kept |
| `8` | IncompleteAnalysis | A scan didn't finish — treat as **re-run**, never as clean |

> ⚠️ **Exit `8` is the whole point of the gate.** If a project fails to scan, the run reports *nothing* for the part it couldn't read. A green `0` on an incomplete scan would let a vulnerability slip through. Always branch on `8`.

---

## 🤖 CI/CD integration

### Strict JSON contract

`--output Json --quiet` guarantees JSON-only stdout against a [published schema](schemas/cpmigrate-output.schema.json):

```bash
cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analyze.json
cpmigrate -s ./MySolution.sln --dry-run --output Json --quiet > migrate.json
```

Key off `outputSchemaVersion`, not the tool version. Two gotchas: **`success: true` ≠ "no findings"** (read `summary.issuesFound` / `issuesAtOrAboveThreshold`), and **absent fields are meaningful** (no `issuesBaselined` = no baseline used, not zero suppressions). `--output Csv` gives one row per finding for spreadsheets.

### SARIF → PR annotations

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
    case "$code" in 0|5) ;; *) echo "::error::incomplete scan (exit $code)"; exit 1 ;; esac
```

Capture the exit code — don't `continue-on-error`, which would swallow `8` and go green on exactly the case the upload exists to catch.

### Markdown → job summary / PR comment

```bash
cpmigrate --analyze --audit --outdated --output Markdown --quiet >> "$GITHUB_STEP_SUMMARY"
```

Verdict-first, severity breakdown, findings linked to their rules, baselined rows marked, incomplete scans flagged, long lists collapsed behind `<details>`. Post to a PR with `gh pr comment "$N" --body-file report.md`. Full guide: [georgepwall1991.github.io/CPMigrate/guides/ci-cd](https://georgepwall1991.github.io/CPMigrate/guides/ci-cd/).

---

## ❓ FAQ

<details>
<summary><b>How do I migrate a solution to Central Package Management?</b></summary>

`cpmigrate -s ./MySolution.sln --dry-run --diff` to preview, then drop `--dry-run --diff` to apply. CPMigrate extracts every `<PackageReference>`, resolves conflicts by strategy, generates `Directory.Packages.props`, and strips inline `Version` attributes — with a timestamped backup you can `--rollback` to.

</details>

<details>
<summary><b>What <i>is</i> <code>Directory.Packages.props</code>?</b></summary>

The file NuGet Central Package Management reads versions from, so every project shares one version per package instead of declaring its own. CPMigrate generates and maintains it. [Microsoft's CPM docs](https://learn.microsoft.com/nuget/consume-packages/central-package-management) cover the format.

</details>

<details>
<summary><b>Can it roll back a bad update?</b></summary>

Two ways. `--update-packages` runs `dotnet test` and rolls back on failure. `--update-packages --bisect` keeps the largest green subset and names the held-back packages. Migrations get timestamped backups restorable with `cpmigrate --rollback`.

</details>

<details>
<summary><b>Does it work in CI/CD?</b></summary>

It's built for it. `--output Json --quiet` for strict stdout, `--output Sarif` for PR annotations, `--output Markdown` for the step summary, `--output Csv` for spreadsheets. Exit codes are contract-level: `5` = findings, `8` = incomplete scan.

</details>

<details>
<summary><b>Does it support <code>.slnx</code> and monorepos?</b></summary>

Yes — classic `.sln` and VS 17.10+ `.slnx`, and `--batch` recursively discovers every solution, optionally in parallel (`--batch-parallel`) and continue-on-failure (`--batch-continue`), with an isolated backup per solution.

</details>

<details>
<summary><b>Can I gate on vulnerabilities without failing on existing debt?</b></summary>

Yes. `--audit` scans direct + transitive CVEs; `--fail-on High` narrows the gate while still reporting everything; `--write-baseline` records today's findings once so CI fails only on *new* debt. Baselined findings stay visible in every report.

</details>

<details>
<summary><b>Which .NET versions are supported?</b></summary>

The tool targets .NET 10 with `LatestMajor` roll-forward and runs on any machine with **.NET SDK 8.0+**. Your projects can target anything — CPMigrate edits XML directly and only builds your solution when you ask it to verify updates.

</details>

---

## 🔧 Configuration

`cpmigrate --init` writes a `.cpmigrate.json` (CLI flags always win):

```json
{
  "$schema": "https://raw.githubusercontent.com/georgepwall1991/CPMigrate/main/schemas/cpmigrate.schema.json",
  "conflictStrategy": "Highest",
  "backup": true,
  "addGitignore": true,
  "failOn": "High",
  "baseline": ".cpmigrate-baseline.json",
  "retention": { "enabled": true, "maxBackups": 5 }
}
```

Discovered by walking up from the solution/project path (or cwd). Contradictory settings warn; malformed JSON reports the exact line and column.

---

## Compatibility ✅

- **.NET SDK:** 8.0+ (tool targets `net10.0`, `LatestMajor` roll-forward)
- **Projects:** `.csproj` / `.fsproj` / `.vbproj`
- **Solutions:** `.sln` and `.slnx`
- **CPM:** generates and consumes standard NuGet Central Package Management files

## 🧪 Examples & benchmarks

- Starter repo: [`examples/small-solution/`](examples/small-solution/)
- Monorepo: [`examples/monorepo/`](examples/monorepo/)
- Benchmarks: [`docs/benchmarks.md`](docs/benchmarks.md)

## 🗓️ Release cadence

Stable releases weekly (versioned + changelogged); RCs for fast feedback. Source of truth: [`CHANGELOG.md`](CHANGELOG.md). Policy: [`docs/release-cadence.md`](docs/release-cadence.md).

## 📡 Telemetry (opt-in)

Disabled by default. Set `CPMIGRATE_TELEMETRY_OPT_IN=true` to emit command-level metrics only (operation, duration, exit-code category, high-level flags) — **never** paths, package names, file contents, or source. Stored locally at `~/.cpmigrate/telemetry/events.ndjson`.

## 🤝 Contributing

Fork → `git checkout -b feature/thing` → write tests → `dotnet test` → open a PR. The drift tests hold the docs to the code, so update the README tables when you touch a flag.

## 📜 License

MIT — see [`LICENSE`](LICENSE).

---

<div align="center">

**Built by [George Wall](https://github.com/georgepwall1991)** · `cpmigrate` · make the build boring again.

[⭐ Star it](https://github.com/georgepwall1991/CPMigrate) · [🐛 Report a bug](https://github.com/georgepwall1991/CPMigrate/issues) · [📖 Docs site](https://georgepwall1991.github.io/CPMigrate/)

</div>

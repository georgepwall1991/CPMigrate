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
- 📊 **A dependency health scoreboard** — 12 analyzers, a 0–100 score, severity-gated CI exits.
- 🛡️ **Updates that roll themselves back** the instant `dotnet test` goes red — and with `--bisect`, keep the largest subset that stays green instead of nuking all 38 because one broke.

---

## What it catches 🕵️

| | Rule | What it finds | Severity |
|:--:|------|---------------|:--:|
| 🟥 | **SecurityVulnerability** | Known CVEs in direct *and* transitive deps (`--audit`) | Critical |
| 🟧 | **InlineVersionUnderCpm** | CPM drift — inline overrides, missing pins, orphaned entries, CPM switched off | High |
| 🟧 | **LicenseRisk** | Copyleft (GPL/AGPL) & proprietary licenses (`--licenses`) | High |
| 🟨 | **VersionInconsistency** | Same package, different versions across projects | Moderate |
| 🟨 | **FloatingVersion** | `4.*` or `[4.0.0,)` — restore picks the version, so the build isn't reproducible | Moderate |
| 🟨 | **TransitiveConflict** | Divergent transitive graphs (auto-pinnable) | Moderate |
| 🟨 | **EolTargetFramework** | Project targets `net6.0`, `net7.0`, `net9.0`, `netcoreapp`, or another end-of-life runtime | Moderate |
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

# 3 · migrate to Central Package Management — and prove it changed nothing that ships
cpmigrate -s ./MySolution.sln --verify

# 4 · update packages; tests fail → automatic rollback
cpmigrate --update-packages --bisect
```

**No flags?** Bare `cpmigrate` drops you into **Mission Control**, an interactive wizard. **One project?** `cpmigrate --project ./src/Api/Api.csproj --dry-run`. **Monorepo?** `cpmigrate --batch ./repo --batch-parallel`. **Team defaults?** `cpmigrate --init` scaffolds a `.cpmigrate.json`.

---

## Install 📦

Requires **.NET SDK 8.0** or later. The tool itself targets `net10.0` with `LatestMajor` roll-forward.

```bash
dotnet tool install --global CPMigrate --version 3.62.0
```

```bash
dotnet tool update --global CPMigrate     # or:  cpmigrate --update
```

| Channel | Command |
|---------|---------|
|  **Homebrew** | `brew tap georgepwall1991/cpmigrate && brew install cpmigrate` |
| 🪟 **Winget** | `winget install GeorgeWall.CPMigrate` *(after the package is indexed)* |
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
| 🔎 **`--verify`** | Restores before *and* after, diffs the resolved graph, attributes every change to the decision that caused it |
| 🔬 **Dependency analysis** | 12 analyzers + scoreboard + 0–100 health score; JSON / SARIF / Markdown / **CSV** |
| 🩹 **Auto-fix** | Version, casing, redundant refs, transitive pin |
| 🔁 **Safe updates** | Latest versions + `dotnet test` + automatic rollback |
| 🔪 **`--bisect`** | Largest green update subset; names the held-back packages |
| 🧱 **`Directory.Build.props`** | Unify repeated properties across projects |
| 🏢 **Batch / monorepo** | Sequential or parallel multi-solution runs |
| 💾 **Backup & rollback** | Timestamped on-disk backups for every destructive path |
| 📄 **`.sln` + `.slnx`** | Classic solutions and Visual Studio 17.10+ `.slnx` |
| 🩺 **`--doctor`** | Environment diagnostics: SDK, NuGet, disk space, write access, workspace, config, git |
|  **`--init`** | Scaffold `.cpmigrate.json` with team defaults |
| 📟 **`--status`** | One-shot workspace health dashboard |
| 🌳 **`--tree`** | ASCII dependency tree, direct + transitive |
| 🕵️ **`--why`** | Trace one package: who declares it, who inherits it, version drift — as text or `--output Json` for CI |
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
cpmigrate --doctor                 # SDK, NuGet reachability, disk, write access, workspace, git — one table
cpmigrate --status                 # repo-context dashboard, no wizard
cpmigrate --tree --transitive      # ASCII dependency tree per project
cpmigrate --why Newtonsoft.Json    # who declares it, who inherits it, do versions drift
cpmigrate --why Newtonsoft.Json --output Json   # the same answer, as one JSON document for CI
cpmigrate --init                   # scaffold .cpmigrate.json (interactive or CI-safe)
```

</details>

<details open>
<summary><b>🔎 Migration &amp; verification</b> — change the build, then prove what changed</summary>

```bash
cpmigrate -s ./MySolution.sln --dry-run --diff   # preview, nothing written
cpmigrate -s ./MySolution.sln --verify           # migrate, then prove the graph didn't move
cpmigrate -s ./MySolution.sln --verify --verify-strict   # demand a literal no-op
cpmigrate -s ./MySolution.sln --verify --output Markdown # the receipt, for the PR body
```

</details>

<details open>
<summary><b>🔬 Analysis &amp; auto-fix</b> — find the rot, then fix it</summary>

```bash
cpmigrate --analyze --audit --outdated --deprecated --licenses --transitive
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
| `--doctor` | `false` | Diagnose the environment: SDK, NuGet, disk space, workspace writability, config, git |
| `--init` | `false` | Scaffold a `.cpmigrate.json` (interactive, or CI-safe defaults) |
| `--status` | `false` | One-shot workspace health dashboard |
| `--tree` | `false` | ASCII dependency tree per project (add `--transitive` for the full graph) |
| `--why` | — | Explain where a package comes from: direct declarations (inline vs central pin), update-only amendments, transitive introducers, and version drift across projects. Pair with `--output Json` for one machine-readable document; exit codes match either mode |

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
| `--verify` | | `false` | Prove the migration didn't change what restores. Two restores; exit `9` on drift nothing explains |
| `--verify-strict` | | `false` | Fail on *any* graph change, including explained ones. Requires `--verify` |
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
| `--licenses` | | `false` | Flag copyleft / proprietary / unknown licenses from restored nuspecs |
| `--fix` | | `false` | Apply auto-fixes (with `--analyze`) |
| `--fix-dry-run` | | `false` | Preview auto-fixes |
| `--fail-on` | | `Info` | Lowest severity that fails: `Info`·`Low`·`Moderate`·`High`·`Critical`·`Never` |
| `--rules` | | | Per-rule policy: `Rule=Severity` pairs, or `Rule=none` to switch a rule off |
| `--max-parallelism` | | procs (≤8) | Projects scanned at once for `--audit`/`--outdated`/`--deprecated` |
| `--baseline` | | | Accepted-findings file; reported but never fail the build |
| `--write-baseline` | | `false` | Record current findings as the baseline, then exit |

**Tuning rules to the codebase.** `--fail-on` is one global threshold, so silencing a noisy rule means lowering the gate for everything. `--rules` re-grades or removes rules individually, before the threshold is applied:

```bash
cpmigrate --analyze --rules "OutdatedPackage=none,LicenseRisk=Critical"
```

Unknown rule IDs are **rejected**, not ignored — a typo that quietly left a rule armed would look exactly like a working policy. A disabled rule is different from a baselined one: baselined findings stay visible so the debt gets paid down, while a disabled rule reports nothing at all. Either way the policy is echoed in the terminal and published in JSON (`summary.disabledRules`, `summary.severityOverrides`), so `issuesFound: 0` can always be told apart from findings that were configured away. Set `"rules"` in `.cpmigrate.json` to apply it team-wide.

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
| `--report <PATH>` | | Write a Markdown rollup of the batch run to a file |

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
| `9` | GraphDrift | `--verify` found the resolved graph moved unexplained, or couldn't prove it hadn't |

> ⚠️ **Exit `8` is the whole point of the gate.** If a project fails to scan, the run reports *nothing* for the part it couldn't read. A green `0` on an incomplete scan would let a vulnerability slip through. Always branch on `8`.

> ⚠️ **Exit `9` is the only code that says the files are fine and the *build* isn't.** A migration that rewrites every `.csproj` perfectly and quietly ships a different version of a package exits `0` on every other measure.

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

### Gate a migration PR on the resolved graph

A migration PR is sixty changed files, and `git diff` cannot answer the only question that matters: does this change what we ship? `--verify` answers it, and exits `9` when it can't.

```yaml
- name: Migrate and verify
  id: migrate
  run: |
    set +e
    cpmigrate -s ./MySolution.sln --verify --force --output Markdown --quiet > receipt.md
    echo "exit_code=$?" >> "$GITHUB_OUTPUT"
- name: Publish the receipt
  if: always() && hashFiles('receipt.md') != ''
  run: cat receipt.md >> "$GITHUB_STEP_SUMMARY"
- name: Require an accounted-for graph
  run: |
    code="${{ steps.migrate.outputs.exit_code }}"
    [ "$code" = "0" ] || { echo "::error::resolved graph moved unexplained (exit $code)"; exit 1; }
```

`--verify` rolls the migration back on drift it can't account for, so a failed job leaves the tree as it found it. Add `--verify-strict` when the migration must be a literal no-op — then *any* graph change fails, even one the receipt explains.

---

## ❓ FAQ

<details>
<summary><b>How do I migrate a solution to Central Package Management?</b></summary>

`cpmigrate -s ./MySolution.sln --dry-run --diff` to preview, then `cpmigrate -s ./MySolution.sln --verify` to apply. CPMigrate extracts every `<PackageReference>`, resolves conflicts by strategy, generates `Directory.Packages.props`, and strips inline `Version` attributes — with a timestamped backup you can `--rollback` to. `--verify` then proves the result restores to the same graph it started from.

</details>

<details>
<summary><b>Does migrating to CPM change what my code builds against?</b></summary>

It can, and that's the point of `--verify`. Moving a version from a `.csproj` into `Directory.Packages.props` is a no-op — but when two projects disagree about a package, the migration has to pick one, and `--conflict-strategy Highest` (the default) silently upgrades the loser. That's a real change to shipped binaries, and `git diff` can't show it to you.

`--verify` restores before and after, diffs the fully-resolved graph per project and target framework, and reports every version that moved alongside the decision that caused it — plus anything reachable from it. Changes nothing accounts for fail the run (exit `9`) and roll the migration back. On Serilog it reports: 221 resolved versions, 216 unchanged, 5 moved, all from one `PolySharp` unification.

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

The tool targets .NET 10 with `LatestMajor` roll-forward and runs on any machine with **.NET SDK 8.0+**. Your projects can target anything — CPMigrate edits XML directly, and only restores or builds your solution when you ask it to: `--verify`, `--update-packages`, `--transitive`, `--audit`, `--outdated`, and `--deprecated`.

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

Discovered by walking up from the solution/project path (or cwd). Contradictory settings warn; malformed JSON reports the exact line and column. Unknown keys are **named, not ignored** — a typo like `fialOn` warns `did you mean 'failOn'?` instead of silently leaving the setting unset (nested keys too, e.g. inside `retention`). Keys that differ only in case still deserialize normally and are not flagged. The run itself never fails on an unknown key.

---

## Compatibility ✅

- **.NET SDK:** 8.0+ (tool targets `net10.0`, `LatestMajor` roll-forward)
- **Projects:** `.csproj` / `.fsproj` / `.vbproj`
- **Solutions:** `.sln` and `.slnx`
- **CPM:** generates and consumes standard NuGet Central Package Management files
- **`--verify` costs two full solution restores** — one for the baseline, one for the result — and needs the feed both times. It is opt-in for that reason. A restore that fails either time is reported as exit `9`, never as a clean graph.
- **`--verify` fails closed rather than guessing.** Three shapes it will not measure, each reported by name rather than silently skipped: two projects in one directory (they share a single `obj/project.assets.json`, so neither can be read independently); two projects that would be reported under the same name; and a project that redirects its intermediate output (`MSBuildProjectExtensionsPath`, `BaseIntermediateOutputPath`, `ProjectAssetsFile`), since finding its graph needs MSBuild evaluation this pass does not perform. In each case the run exits `9` — a verification that cannot tell two projects apart has verified neither.
- **`--verify` needs the solution, not a directory holding several.** Discovery can pick one interactively, but the restore still targets the directory, which `dotnet restore` rejects. Pass `-s ./Path/To/Solution.slnx` when a directory contains more than one.
- **`--verify` is incompatible with `--output Csv`**, which carries analyzer findings and has no shape for a receipt. Use `--output Json` or `--output Markdown`.

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

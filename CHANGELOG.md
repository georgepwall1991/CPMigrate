# Changelog

All notable changes to CPMigrate are documented in this file.

The format is based on Keep a Changelog and follows semantic versioning intent.

## [Unreleased]

## [3.6.1] - 2026-07-27

### Documentation
- **NuGet/GitHub discoverability:** keyword-rich `Title`, `Description`, and `PackageTags` for Central Package Management / `Directory.Packages.props` search (CPM, CentralPackageManagement, migrator, vulnerability, Directory.Build.props, bisect, and related high-intent terms).
- README conversion funnel above the fold: problem → what it catches → install (exact `3.6.1`) → product-flow visuals → 30-second path → feature snapshot → compatibility; deep CLI reference preserved below.
- Three product-flow SVGs under `assets/` (analysis scoreboard, CPM before/after, update+bisect loop) plus absolute `https://raw.githubusercontent.com/...` image URLs so NuGet.org PackageReadmeFile renders images reliably.
- Durable `DiscoverabilityMetadataTests` and `scripts/verify-packages.sh` gate package metadata, README funnel, HTTPS image refs, and packed assets.

### Changed
- Package version **3.6.1** (docs/discoverability only — no CLI behavior or diagnostic severity changes).

## [3.6.0] - 2026-07-25

### Added
- **`--bisect`: keep the updates that work instead of discarding all of them.** `--update-packages` was all-or-nothing — one bad package in a set of 38 reverted the other 37 and reported nothing about which one was at fault, leaving the user to bisect by hand. `--bisect` runs delta debugging over the accepted update set and applies the largest subset that still restores and tests clean, naming the packages it held back.
  - The whole set is verified first, so a healthy run costs exactly one verification and pays no bisection overhead. Only a failure triggers the split.
  - Each probe is verified against the set already banked as good, not in isolation, so failures that need two packages together (a library plus its own updated dependency) are still resolved. A plain binary search assumes one independent culprit and gets these wrong.
  - `--bisect-budget <n>` (default 16) caps the restore+test cycles. Expect roughly `2·log₂(n)` runs for a single culprit. When the budget runs out, whatever is unresolved is held back and the banked-good set is still applied, so an interrupted search delivers partial progress rather than nothing.
  - `--bisect-test-filter <expr>` passes a `dotnet test --filter` expression to each probe, to search against a fast subset of the suite.
  - When nothing at all can be kept, CPMigrate verifies the zero-update baseline before blaming the packages, so an already-red test suite is reported as such instead of being attributed to the updates.
- **`--only <ids>`** restricts `--update-packages` to a comma-separated set of package IDs — the natural follow-up to a bisect run that named its culprits.
- JSON contract additions (`outputSchemaVersion` 1.1.0, all additive): `summary.packagesHeldBack`, `summary.verificationRuns`, `summary.bisectBudgetExhausted`, and `packageUpdates[].heldBack`. The summary fields appear on every `--update-packages` payload (including `--dry-run`, where they read `0`/`0`/`false`) and on no other operation. They are populated for non-bisecting runs too: a plain rollback now reports the whole set as held back. No existing field changes meaning.

### Changed
- The update pipeline was decomposed behind two seams: `IUpdateTransaction` (write any subset over a pristine in-memory baseline; revert exactly) and `IVerificationRunner` (restore+test, memoized by subset identity so a repeated subset never re-runs the suite). The existing all-or-nothing behaviour is now `AllOrNothingSearchStrategy` over the same seams and is unchanged. Reverting no longer depends on the on-disk backup manifest, so it works under `--no-backup` too.
- `IDotNetCliService.RunTestAsync` takes an optional `testFilter`. Existing callers are unaffected.

### Fixed
- **`--update-packages` crashed before it could back anything up.** `PackageUpdateService` called `CreateBackupForProject(settings, propsPath, timestamp)` against a `(settings, filePath, backupPath, timestampOverride)` signature, so the timestamp was bound to `backupPath` and the backup was written to a relative directory named after the timestamp — throwing `IOException` for any run with backups enabled, which is the default. The existing tests missed it because they mocked the `Options` overload while production called the `BackupSettings` one, so the mock never matched, returned null, and left rollback silently restoring nothing.
- **Every remaining prompt is now guarded against a non-interactive terminal.** 3.5.0 added `IConsoleService.IsInteractive` but only wired it into two call sites; the other seven still threw Spectre's "Cannot show selection prompt" when stdout was redirected. The fallback is chosen per site rather than applied uniformly, because the safe answer is not the same everywhere:
  - **Declines the action** (a write nobody confirmed): `BuildPropsService` `--unify-props`, and `CommandRouter`'s backup deletion/pruning. Both point at `--force` for unattended runs.
  - **Declines and redirects**: `UpdateService` self-update suggests `dotnet tool update --global CPMigrate` instead of prompting.
  - **Fails loudly**: `SolutionDiscovery` with multiple `.sln` files lists the candidates and asks for an explicit `-s`, since guessing could migrate the wrong projects.
  - **Falls back to the deterministic answer**: `--interactive-conflicts` resolves via the configured `--conflict-strategy` (the same resolution the run would use without the flag) and says so once; `PackageUpdateService` skips major-version updates, matching its existing `--quiet`/`--output Json` behaviour.
  - **Proceeds**: the automatic rollback offered *after* a failed migration, where declining would leave the tree half-migrated. This matches the existing `--quiet` behaviour.

### Documentation
- Regenerated `cpmigrate-demo.gif` and `cpmigrate-analyze.gif` against 3.5.0, so they show the current styling and the new per-analyzer scoreboard.
- Fixed three ways `scripts/generate-docs-media.sh` had gone stale:
  - It recorded against the repo root. CPMigrate adopted CPM for its own dependencies, so the demo captured nothing but "already migrated to CPM" and the analysis found no issues. Both recordings now target `examples/small-solution`, which has real version conflicts.
  - Its `expect` script predated the wizard's current question sequence (`AskAnalyzeOptions` alone asks five questions), so recordings trailed off partway through — silently, since a missed `expect` only times out.
  - It never answered the wizard's closing "Return to main menu?", so the process never exited and `asciinema` hung on the open pty. The expect timeout was also raised from 60s to 180s, since the full analysis exceeds it and expiry kills the wizard mid-scan.
- Known gap: `cpmigrate-interactive.gif` still shows pre-3.5.0 styling. The wizard's analysis stalls when driven through a recorded pty in WSL, so it needs regenerating on a machine where that completes.

## [3.5.0] - 2026-07-25

### Fixed
- **`cpmigrate analyze -s .` silently ran a migration.** CPMigrate is flag-driven and has no sub-commands, so `CommandLineParser` discarded the leading bare word and fell through to the default action — rewriting `.csproj` files for a read-only intent. A new `CliVerbGuard` rejects a leading positional argument and suggests the equivalent flag (`analyze` → `--analyze`, `fix` → `--fix`, …). Genuinely ambiguous words offer both candidates: bare `update` suggests `--update-packages` *and* `--update` (self-update), rather than guessing.
- **Post-migration prompt still crashed on a redirected stdout.** `ShouldOfferVerification` only covered the flags an operator opts into (`--force`, `--quiet`, `--output Json`); a plain `cpmigrate` with stdout piped to a file or `tee` has none of them set and still threw "Cannot show selection prompt". It now also consults the new `IConsoleService.IsInteractive`, and skips verification after `--dry-run` (nothing was written to verify).
- **`--rollback` on a non-interactive terminal** now declines with a "re-run with `--force`" hint instead of throwing from the confirmation prompt.
- Bumped the `System.*` 10.0.9 servicing pins to 10.0.10, clearing the five `NU1903` advisories against `System.Security.Cryptography.Xml` that were failing the build under `TreatWarningsAsErrors`.
- `MigrationValidatorTests.GetOutputPaths_SolutionFile_ResolvesToParentDirectory` hard-coded POSIX separators and failed on Windows; it now builds its expectations with `Path.Combine`.

### Added
- `SpectreTheme` / `GlyphSet`: status icons (`✔ ✖ › » ○ ▶ ➜ █ ░`) now degrade to ASCII equivalents when the target console reports no Unicode support, instead of emitting replacement characters into legacy Windows consoles and CI logs.
- Per-analyzer scoreboard after `--analyze`: each analyzer's issue count and a relative-share meter, so a long scroll of individual tables ends with one scannable summary.
- `IConsoleService.IsInteractive`, so callers can guard prompts without reaching for `AnsiConsole`.

### Changed
- The migration pipeline indicator renders as a connected stepper — completed steps and the rails behind them light up — rather than five disconnected labels.
- The risk assessment panel shows a bar meter and a 0–100 score alongside the LOW/MEDIUM/HIGH band.
- Version conflict tables now bold the winning version and dim the ones being dropped, so the resolution is readable without comparing strings.
- Colour literals (`deeppink1`, `springgreen1`, …) scattered across the three Spectre renderers were replaced with `SpectrePalette.Ink` tokens, giving the palette a single source of truth.

### Documentation
- README gained a **No Sub-Commands** and a **Non-Interactive Terminals** section under CI/CD Integration, documenting the verb guard, the prompt-skipping guarantees, and the ASCII glyph fallback.
- README Gallery now leads with real captured terminal output for the pipeline stepper, risk assessment, and conflict resolution, alongside the existing GIFs.
- The Dependency Analysis section documents the new per-analyzer scoreboard.
- Note: `docs/images/*.gif` still show the pre-3.5.0 styling; regenerating them needs `asciinema` + `agg` (see `scripts/generate-docs-media.sh`).

## [3.4.1] - 2026-06-28

### Fixed
- **Strict JSON stdout contract**: under `--output Json --quiet`, `SolutionDiscovery` no longer emits `› Found project: …` notices (or banners) to stdout before the JSON document. CI parsers can now `json.load` the output directly, as documented in README. Applied via a new `ApplicationServices.WithConsole(...)` swap that re-wires `SolutionDiscovery`/`ProjectAnalyzer` to `SilentConsoleService` in JSON mode.
- **`cpmigrate -s ./MySolution.sln`** (the README's primary quickstart): `MigrationValidator.GetOutputPaths` now resolves a `.sln`/`.slnx` file path to its containing directory, instead of trying to `Directory.CreateDirectory("MySolution.sln")` and crashing with `FileOperationError`.
- **Post-migration guidance in non-TTY shells**: `MigrationDisplay.ShowPostMigrationGuidance` no longer crashes with "Cannot show selection prompt since the current terminal isn't interactive" under `--force`, `--quiet`, or `--output Json`. New `ShouldOfferVerification(options)` mirrors `ShouldProceedWithDestructiveAction`.

### Changed
- Extracted `JsonOutputWriter.EmitAsync` to centralize the four duplicated `WriteJsonOutput*` emit tails in `CommandRouter` (file-or-stdout, with optional "written to" notice under `--quiet`).
- `Console.Error` suggestion/stack-trace writes in `CommandRouter` catch blocks now respect `--quiet`.
- Split `SpectreConsoleService` (536 → 253 lines) into `SpectrePanelBuilder`, `SpectreTableBuilder`, and a shared `SpectrePalette`. The `IConsoleService` facade is unchanged; behavior preserved (all 31 Spectre tests still pass).
- Replaced `InteractiveService`'s brittle `Contains()`/emoji-prefix action routing with a `WizardAction` enum + label→enum map. Fixed a latent bug where migration actions surfaced as "READY TO UNKNOWN" (the unused `ModeMigrate`/`ModeAnalyze`/`ModeBatch`/`ModeRollback`/`ModeBackups` consts never matched the action strings).
- Extracted the ~190-line `PackageUpdateService.UpdatePackagesAsync` into a ~50-line orchestrator calling seven cohesive step methods (`DiscoverAndLoadCurrentVersionsAsync`, `QueryAllUpdatesAsync`, `FilterAvailableUpdates`, `BuildDryRunResult`, `CreateBackupAsync`, `ApplyUpdatesAsync`, `RestoreTestAndFinalizeAsync`).

### Removed
- Dead JSON-model fields: `ConflictInfo`, `VersionUsage`, and `Conflicts` properties from `OperationResult`/`SolutionResult` (declared in the schema but never populated — always serialized as empty).

### Added
- `JsonContractTests`: 2 integration tests capturing real stdout via `Console.SetOut` + `AnsiConsole.Create` and asserting pure JSON parses; would have failed on `3.4.0`.
- `ListBackupsHandlerTests` (6 facts) and `RollbackHandlerTests` (7 facts) directly covering the previously-untested handlers from the 3.4.0 `Services/Migration/` split.
- `MigrationValidatorTests.GetOutputPaths_SolutionFile_ResolvesToParentDirectory` regression test for the `.sln` path fix.

### Documentation
- `REFACTORING_STATUS.md` and `NEXT_STEPS.md` refreshed to reflect actual state: `Program.cs`/`Options.cs`/`MigrationService` split marked DONE; `.editorconfig` rule status corrected (CA1502/CA1505/CA1506 are already `warning`); this session's work recorded; "Re-enabling SonarCloud" checklist added.
- `sonar-project.properties.reference` version 2.9.0 → 3.4.0.
- All `release.yml` GitHub Actions bumped v4 → v5 to align with `ci.yml`/`distribution-smoke.yml`/`pages.yml`.
- Redacted a leaked `SONAR_TOKEN` literal from `SONARCLOUD.md`; SonarCloud remains disabled in CI (`ci.yml:15` has `if: false`) pending `SONAR_TOKEN` rotation in repo secrets.

### Internal
- Test suite: 546 → 562 passing, 0 warnings, 0 errors.
- Verified end-to-end with a throwaway multi-project .NET 10 solution exercising every command surface: analyze (terminal + JSON), migrate dry-run + apply, analyze-after-migration, unify-props dry-run + apply, `dotnet restore`/`build` after migration, update-packages dry-run (terminal + pure JSON), list-backups, rollback (with version-attr restoration check), prune-all, batch mode (`2/2 solutions processed successfully`), `--version`, `--help`.

## [3.4.0] - 2026-06-28

### Changed
- Split `MigrationService` command flows into dedicated `RollbackHandler`, `ListBackupsHandler`, and `AnalysisHandler` classes under `Services/Migration/`, reducing the orchestrator from 1,631 to 885 lines (-46%) while preserving the public `MigrationService` constructor signature.
- Wired the previously-built-but-unused `BackupCoordinator` into `MigrationService`, removing four duplicated inline backup methods (`SetupBackupDirectory`, `WriteBackupManifestAsync`, `ManageGitIgnoreAsync`, `CreatePropsBackup`) that mirrored the coordinator's logic.
- `MigrationService.ExecuteAsync` is now a thin command router delegating to mode-specific handlers.

### Internal
- Command handler extraction enables independent unit testing of rollback, list-backups, and analyze flows.
- All 546 existing tests continue to pass without modification.

## [3.3.3] - 2026-06-28

### Fixed
- Resolved high-severity vulnerabilities in transitive `System.Security.Cryptography.Xml` and related `System.*` packages by updating pins to `10.0.9` and adding explicit package references.
- Disabled `GeneratePackageOnBuild` to prevent nupkg file locking during Release builds and remove redundant pack steps in CI.

### Changed
- Refactored high-complexity methods in `VersionResolver`, `FixService`, `ConfigService`, `DependencyGraphService`, `BuildPropsService`, `BatchService`, `MigrationService`, and `InteractiveService` into smaller, testable helpers.
- Reduced cyclomatic complexity across the codebase while preserving all existing behavior.

### Added
- 25 new unit tests covering edge cases for version resolution, fix service error handling, config merging, dependency graph analysis, and previously untested CLI/process infrastructure.
- Added test coverage for `DotNetCliService` argument builders and `ProcessRunner`.

### Documentation
- Updated test counts across README, NEXT_STEPS, REFACTORING_STATUS, and SESSION_SUMMARY to reflect the current 546 tests.

## [3.3.2] - 2026-03-13

### Changed
- Centralized application wiring through a single internal composition root so command routing no longer constructs concrete services inline.
- Introduced mode-specific request records and analyzer/fixer catalogs to reduce deep coupling to the full CLI option surface while preserving existing command behavior.
- Split project inspection responsibilities into solution discovery, project file scanning, and `dotnet package list` query services, keeping migration and analysis flows stable behind narrower seams.

## [3.3.1] - 2026-03-13

### Fixed
- `--project` now consistently scopes migrate and analyze flows to the selected project instead of falling back to implicit solution discovery.
- JSON output mode no longer emits config discovery chatter before structured output, keeping stdout machine-safe for CI and scripts.
- Config loading now happens once per invocation, preserving CLI-over-config precedence and avoiding duplicate config notices.

### Changed
- Help text and documentation now describe explicit solution/project targeting more accurately, including single-project examples and JSON contract expectations.

## [3.3.0] - 2026-02-02

### Added
- `--analyze` flags: `--outdated` and `--deprecated`.
- JSON schema field `outputSchemaVersion` for machine-contract evolution.
- JSON output contract for `--update-packages` including per-package update entries.
- Typed analysis issue metadata in JSON (`issueCode`, `severity`, `fixable`, `metadata`).
- Runtime version injection for machine-readable output payloads.
- Example repos for onboarding and demos:
  - `examples/small-solution/`
  - `examples/monorepo/`

### Changed
- Analyze mode now prefers resolved package data from `dotnet package list --format json --output-version 1`, enabling CPM-managed package counting without `--transitive`.
- Vulnerability/transitive/outdated/deprecated scans now parse structured `dotnet` JSON output instead of brittle text parsing.
- `--output Json --quiet` now emits JSON-only stdout for migrate/analyze/rollback/update-packages.
- Quiet mode behavior is more consistent for headers/banners across command modes.
- Fixer routing now prefers typed issue codes instead of description string matching.

## [3.2.0]

### Added
- Interactive wizard enhancements for package update workflows.
- Documentation refresh for v3.2 interactive package update flows.

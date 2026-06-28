# Changelog

All notable changes to CPMigrate are documented in this file.

The format is based on Keep a Changelog and follows semantic versioning intent.

## [Unreleased]

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

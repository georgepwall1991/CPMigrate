# Changelog

All notable changes to CPMigrate are documented in this file.

The format is based on Keep a Changelog and follows semantic versioning intent.

## [Unreleased]

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

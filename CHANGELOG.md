# Changelog

All notable changes to CPMigrate are documented in this file.

The format is based on Keep a Changelog and follows semantic versioning intent.

## [Unreleased]

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

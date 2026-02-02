# Release Cadence Policy

CPMigrate follows a simple, predictable shipping model:

- **Stable channel:** weekly releases when quality gates pass.
- **RC channel:** optional release candidates for rapid feedback on larger changes.
- **Contract stability:** JSON schema changes are additive where possible and tracked with `outputSchemaVersion`.
- **Release notes:** every stable release updates `CHANGELOG.md` with migration, analysis, and CI-impacting changes.

If an urgent regression or security issue is discovered, an out-of-band patch release may be published.

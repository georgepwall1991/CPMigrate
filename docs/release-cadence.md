# Release Cadence Policy

CPMigrate follows a simple, predictable shipping model:

- **Stable channel:** weekly releases when quality gates pass.
- **RC channel:** optional release candidates for rapid feedback on larger changes.
- **Contract stability:** JSON schema changes are additive where possible and tracked with `outputSchemaVersion`.
- **Release notes:** every stable release updates `CHANGELOG.md` with migration, analysis, and CI-impacting changes.

If an urgent regression or security issue is discovered, an out-of-band patch release may be published.

## Version bump checklist

A release commit bumps the version in every place `DiscoverabilityMetadataTests` reads it:

- `CPMigrate/CPMigrate.csproj` — `<Version>` and the release notes paragraph
- `README.md` — the `dotnet tool install --global CPMigrate --version X.Y.Z` command
- `site/index.html` — five spots: `softwareVersion`, the eyebrow badge, the install command, and the two terminal-simulation strings

`rg "X.Y.Z" site/index.html README.md` (previous version) should return nothing before the tag is pushed.

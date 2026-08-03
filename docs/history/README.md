# Historical planning documents

Point-in-time working notes, kept for the record and **not maintained**. Every file here describes
the repository as it was on the date in its name, and each contradicts the current state in ways that
are easy to mistake for instructions:

| File | What it was | Why it is not current |
| --- | --- | --- |
| [`2026-01-sonarcloud-session.md`](2026-01-sonarcloud-session.md) | The session that added SonarCloud and the `.editorconfig` | Cites "546/546 tests" against a suite now past 1300, and a `~/RiderProjects/cpmigrate/` path that no longer exists |
| [`2026-01-refactoring-status.md`](2026-01-refactoring-status.md) | A 44-file complexity-reduction plan | Frozen at "6 of 44"; the three files it called critical were since refactored, and the SonarCloud job it targets is disabled in `ci.yml` |
| [`2026-01-next-steps.md`](2026-01-next-steps.md) | Setup steps for that SonarCloud integration, plus a refactor backlog | Its manual setup steps have been performed; its "highest-impact remaining refactors" are mostly struck through |

They were moved here in 3.56.0. Before that they sat at the repository root, where a stale
`NEXT_STEPS.md` reads as a live plan — the same failure the rest of this repository guards against
with drift tests, applied to its own documentation.

**For current state, read instead:**

- [`../../CHANGELOG.md`](../../CHANGELOG.md) — what changed and why, release by release. The engineering record.
- [`../../README.md`](../../README.md) — the CLI surface, held to the code by `DocumentationDriftTests`.
- [`../rules.md`](../rules.md) — every analysis rule, held to the catalog by the same tests.
- [`../release-cadence.md`](../release-cadence.md) — how releases are cut.

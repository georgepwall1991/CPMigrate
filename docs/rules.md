# CPMigrate rule reference

Every finding CPMigrate reports carries a stable **rule ID**. The same ID appears in three places,
so you can move between them without a translation table:

| Surface | Where the ID appears |
| --- | --- |
| `--output Json` | the `issueCode` field of each entry in `analysisIssues` |
| `--output Sarif` | the `ruleId` of each SARIF result, and the `id` of each entry under `tool.driver.rules` |
| Terminal output | the analyzer section heading the finding is printed under |

Rule IDs are part of CPMigrate's public contract. They are not renamed within a major version.

Because the IDs are stable, they are also how a rule is configured. `--rules` takes
`Rule=Severity` pairs — `none` switches a rule off, any severity re-grades its findings before
`--fail-on` is applied:

```bash
cpmigrate --analyze --rules "OutdatedPackage=none,LicenseRisk=Critical"
```

The same policy can be set team-wide as a `rules` map in `.cpmigrate.json`. Unknown rule IDs are
rejected rather than ignored, because a typo that quietly left a rule armed is indistinguishable
from a policy that works. A disabled rule reports nothing at all — unlike a
[baseline](../README.md#gating-on-a-codebase-with-existing-debt), which keeps accepted findings
visible so the debt gets paid down. Whichever rules a run switched off or re-graded are echoed to
the terminal and published in JSON as `summary.disabledRules` and `summary.severityOverrides`, so
`issuesFound: 0` can always be told apart from findings that were configured away.

Several rules below are marked **not fixable** because removing a pin or a reference changes which
version restores, and a fixer cannot know whether that is acceptable.
`cpmigrate -s ./Solution.sln --verify` answers the question those rules leave open: it restores
before and after the change and reports every resolved version that moved, so a fix you make by hand
can be checked rather than hoped for.

Severities map onto SARIF levels as follows:

| CPMigrate severity | SARIF level |
| --- | --- |
| `Critical`, `High` | `error` |
| `Moderate` | `warning` |
| `Low`, `Info` | `note` |

---

## VersionInconsistency

**The same package is referenced at different versions across projects.**

Divergent versions of one package in a single solution produce assembly binding surprises and make
upgrades unpredictable. Centralize the package in `Directory.Packages.props` so every project
resolves the same version.

- Default severity: `Moderate`
- Fixable: yes — `cpmigrate --analyze --fix`, or migrate with `cpmigrate -s ./Solution.sln`

## DuplicatePackageCasing

**One package is referenced under multiple casings.**

NuGet package IDs are case-insensitive, so `newtonsoft.json` and `Newtonsoft.Json` are the same
package. Mixed casing defeats deduplication and can produce duplicate `PackageVersion` entries
under central package management. Settle on the canonical casing published on nuget.org.

- Default severity: `Low`
- Fixable: yes

## RedundantReference

**A project declares the same `PackageReference` more than once.**

Repeated `PackageReference` items for one package are ignored by NuGet at best and produce
conflicting versions at worst. Remove the duplicates and keep a single declaration.

- Default severity: `Low`
- Fixable: yes

## TransitiveConflict

**Projects resolve conflicting versions of a shared transitive dependency.**

When two projects pull different versions of the same transitive package, the version that wins at
runtime depends on build order and restore graph shape. Pin the package explicitly so the resolved
version is deliberate.

- Requires `--transitive`
- Default severity: `Moderate`
- Fixable: yes

## SecurityVulnerability

**A referenced package has a known security advisory.**

NuGet audit reported a published advisory affecting this package. Upgrade to the fixed version, or
if the package is transitive, pin a patched version explicitly.

- Requires `--audit`
- Default severity: mirrors the advisory (`Low` … `Critical`)
- Fixable: no — review the advisory before changing versions

## RedundantDirectReference

**A package is referenced directly even though it already arrives transitively.**

Direct references that duplicate a transitive dependency add maintenance cost and can pin an older
version than the graph would otherwise resolve. Remove the direct reference unless the project
genuinely needs to control that version.

- Requires `--transitive`
- Default severity: `Low`
- Fixable: no — removing a direct reference can change the resolved version, so review it yourself.
  `cpmigrate -s ./Solution.sln --verify` will tell you whether it did

## FrameworkAlignment

**Projects in the solution target different frameworks.**

Mixed target frameworks constrain which package versions can be shared and often surface as restore
failures during a central package management migration. Align the frameworks, or confirm the split
is intentional.

- Default severity: `Info`
- Fixable: no

## OutdatedPackage

**A newer version of the package is published.**

The referenced version is behind the latest release on the configured feed. Review the changelog and
update, or pin deliberately if the newer version is not wanted yet.

- Requires `--outdated`
- Default severity: `Low`
- Fixable: no — use `cpmigrate --update-packages` to update with test verification

## DeprecatedPackage

**The package is marked deprecated by its author.**

Deprecated packages stop receiving fixes, including security fixes. Migrate to the suggested
alternative, or vendor the functionality if no replacement exists.

- Requires `--deprecated`
- Default severity: `Moderate`
- Fixable: no

## Which props file governs a project

The four rules below are judged per project, against the props file MSBuild actually reads for it —
not against one file for the whole scan. A repository can hold several: a root
`Directory.Packages.props` and a nested one governing `tools/`, each governing the projects beneath
it. Every finding names the file it was judged against, so the file you are told to edit is the one
in force for that project.

Resolution follows MSBuild:

- **Walking up** from each project's own directory for the nearest `Directory.Packages.props`,
  stopping at the repository root.
- **`DirectoryPackagesPropsPath`**, when set, redirects to a file of your choosing and wins over the
  walk. Where the redirect names a property CPMigrate cannot evaluate, no file is claimed and the
  project is left unjudged rather than measured against the wrong one.
- **Through imports** — an unconditional `<Import>` whose path is statically resolvable is followed,
  including one anchored with `$(MSBuildThisFileDirectory)`. A conditioned import, or one inside a
  conditioned `<ImportGroup>`, is not followed: whether it applies depends on properties that cannot
  be evaluated from the XML alone.
- **The project's own file has the last word** on `ManagePackageVersionsCentrally`, matching
  MSBuild's evaluation order.
- Path identity follows a temporary case-sensitivity probe at the scan root, not the host OS. This
  keeps `tools/` and `Tools/` separate on case-sensitive filesystems; if the probe cannot run,
  CPMigrate falls back to ordinal comparison so contexts are not silently merged.

A props file governing no scanned project is not judged at all — reporting its pins as orphaned would
make every scoped scan accuse the wider repository's file of being dead.

## CpmNotEnabled

**A `Directory.Packages.props` exists without central package management enabled.**

Without `ManagePackageVersionsCentrally` set to `true`, NuGet ignores every `PackageVersion` entry in
the file. The result is a props file that looks authoritative and does nothing, while projects
silently keep using whatever versions they declare themselves.

The property is resolved the way MSBuild resolves it: through the props file's imports, from the
nearest `Directory.Build.props` and its imports, and from the project's own file, which has the last
word — so a single project can opt out with
`<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>` and is then exempt from all
four CPM rules.

- Reported once per props file, not once per project beneath it
- Default severity: `High`
- Fixable: no — add `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`

## InlineVersionUnderCpm

**A project pins a version inline while central package management is in force.**

An inline `Version` attribute overrides the central one, so the solution is quietly half-centralized.
NuGet does not warn about it — nothing surfaces until two projects disagree and something breaks at
runtime. Remove the attribute so the central version applies.

- Reported for a project governed by a props file, judged against that file's pins
- Default severity: `Moderate`
- Fixable: no — removing a pin changes the resolved version, so review it yourself.
  `--verify` on the migration reports exactly which versions moved and what accounts for each

## MissingPackageVersion

**A referenced package has no version, inline or central.**

Under central package management a `PackageReference` without a `Version` needs a matching
`PackageVersion` entry. With neither, restore fails.

- Reported for a project governed by a props file, judged against that file's pins
- Default severity: `High`
- Fixable: no — add the package to the props file the finding names, which is the one MSBuild reads
  for that project

## OrphanedPackageVersion

**A central `PackageVersion` entry that no project references.**

Harmless to restore, but stale pins accumulate — and once nothing references a package, its pin is
indistinguishable from a deliberate one when someone comes to upgrade.

A pin is judged against every project it governs, across all props files in the scan — a pin
inherited by a nested file is not orphaned because the projects using it read that file rather than
the one declaring it. Pins are attributed to the file that declares them, and a scan where transitive
pinning is on reports no orphans, since a pin nothing references directly is then deliberate.

- Reported once per declared pin, judged across every project governed by it
- Default severity: `Low`
- Fixable: no

## LicenseRisk

**A package carries a copyleft, proprietary, or unverified license.**

Copyleft licenses (GPL, AGPL) require derivative works to use the same license, which may conflict
with proprietary distribution. Proprietary licenses may restrict redistribution. Review the license
terms before shipping.

- Reported when `--licenses` is passed with `--analyze`
- Default severity: `High` (copyleft), `Moderate` (proprietary), `Low` (unknown)
- Fixable: no — review the license on the package's NuGet page

## FloatingVersion

**A version is a wildcard or an open range rather than an exact pin.**

A wildcard (`4.*`) or an open range (`[4.0.0,)`) lets restore choose the version, so the same commit
can build against different code tomorrow. Nothing reports it: resolving a wildcard to a new major
is a perfectly successful restore, and a green CI run against one version says nothing about the one
that gets built next. It is also why a bisect can fail to reproduce — the tree is not the whole
input.

`[4.0.0]` is *not* reported. A bracketed single version is the most exact form NuGet has; it locks
the package to that release.

Read from what the files declare and from every props file governing a scanned project, never from
the resolved graph — by the time restore has run, `4.*` is already a concrete version. A nested
file's floating pin is exactly as unreproducible as the root's, so reading only the root would pass
it as reproducible. A `VersionOverride` counts, because under central package management that is the
version actually in force for that project.

- Reported whenever a declared version is a wildcard or a range
- Default severity: `Moderate`. Teams that accept ranges deliberately can re-grade or switch the
  rule off with `--rules FloatingVersion=Low` or `--rules FloatingVersion=none`
- Fixable: no — choosing the release a float should become needs the feed, which this pass does not
  query

## Unknown

**An analyzer reported a finding without a specific rule code.**

This is a fallback used when an analyzer does not classify its finding. Treat the message text as
the source of truth.

# CPMigrate discoverability keyword map

Session research for NuGet.org / GitHub conversion. Not marketing fiction — terms map to real product surfaces.

> Originally written against 3.7.0. The competitor and gap tables below are that snapshot and have not
> been re-run; the keyword lists are current as of 3.62.0.

## Primary keywords (high intent)

1. **Central Package Management** / **CPM**
2. **Directory.Packages.props**
3. **NuGet package migration** / **migrate to CPM**
4. **dependency analysis** (version drift, duplicates, transitive conflicts)
5. **package updates with rollback** / **test verification**
6. **Directory.Build.props** unification
7. **verify CPM migration** / **did migrating to CPM change my package versions** — added 3.56.0 with `--verify`. No substitute on NuGet answers this, which is the point: every other tool in the table performs the migration and stops.

## Secondary / long-tail

1. centralised package management (UK spelling; competitors rank on this)
2. CentralPackageManagement migrator
3. monorepo NuGet / batch solutions
4. transitive dependency pin / package version drift
5. NuGet vulnerability audit / CVE package scan
6. .slnx solution format
7. bisect package updates (largest green subset)
8. resolved dependency graph diff / package graph before and after
9. does central package management change resolved versions
10. conflict strategy Highest upgraded my package

## Competitor / substitute search phrases (live NuGet)

| Query | Notable substitutes | Tags they use |
|-------|---------------------|---------------|
| central package management | CentralPackageManagementMigrator, CentralNuGetUpdater | CPM, Central, Package, Management |
| Directory.Packages.props | georg-jung.update-cpm-versions, nuget-cpm-cleaner | directory-packages-props, central-package-management |
| cpm nuget | devdeer.tools.tocpm, Saucery.NuGet | CPM, nuget, tool |
| centralised package management | CentralisedPackageConverter | Centralised, Migrator, Converter |

CPMigrate already ranks for `Directory.Packages.props` (3.6.0, ~13k downloads). Gaps vs substitutes: missing explicit **CPM**, **CentralPackageManagement**, **migrator**, **centralised**, **Directory.Build.props**, **vulnerability** tags; no human **Title**; README images were **relative** (broken on NuGet.org).

## Gap vs 3.6.0 package surface

| Field | Was | Issue |
|-------|-----|--------|
| Title | (none / PackageId only) | Search UI shows "CPMigrate" only |
| Description | Solid but generic lead | Add CPM acronym + Directory.Packages.props early |
| PackageTags | good base | Missing CPM / migrator / centralised / vulnerability / Directory.Build.props |
| README images | `./docs/images/...` | NuGet PackageReadmeFile does not render relative paths |
| Product visuals | GIFs only, late in Gallery | Need above-the-fold product-flow diagrams |

## Mapping table

| Term | Description | PackageTags | README section |
|------|-------------|-------------|----------------|
| Central Package Management / CPM | lead sentence | central-package-management; CPM; CentralPackageManagement | Hook, Install, Feature snapshot |
| Directory.Packages.props | lead sentence | directory-packages-props | Problem, What it catches, 30-second path |
| dependency analysis | mid description | dependency-analysis | What it catches, See it work |
| package updates / rollback | end of description | package-updates; rollback; bisect | What it catches, Feature snapshot |
| Directory.Build.props | description body | Directory.Build.props | Feature snapshot |
| monorepo / batch | tags + README | monorepo | Feature snapshot |
| vulnerability / audit | tags + README bullets | vulnerability | What it catches |
| .slnx | tags | slnx | Compatibility |
| centralised (UK) | tags only | centralised | — (search only) |
| migrator | tags | migrator | Hook |

## Recommended package metadata (3.7.0)

**Title:** CPMigrate — NuGet Central Package Management Migration & Dependency Analysis CLI

**Description:** Migrate .NET solutions to NuGet Central Package Management (CPM). Generate Directory.Packages.props, analyze dependency health (version drift, transitive conflicts, vulnerabilities), auto-fix package issues, unify Directory.Build.props, and update NuGet packages with test verification and rollback — including --bisect to keep the largest green subset.

**PackageTags:** nuget;central-package-management;CPM;CentralPackageManagement;directory-packages-props;Directory.Build.props;dotnet-tool;cli;dependency-analysis;package-updates;rollback;bisect;monorepo;slnx;dotnet;package-management;migrator;version-drift;vulnerability;centralised

## Honesty guardrails

- No invented download rankings or social proof beyond NuGet badge.
- No claims of FluentValidation / Roslyn analyzer diagnostics (this is a CLI tool with runtime analyzers).
- Bisect, audit, and fixers only described as implemented.

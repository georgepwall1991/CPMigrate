# Changelog

All notable changes to CPMigrate are documented in this file.

The format is based on Keep a Changelog and follows semantic versioning intent.

## [Unreleased]

## [3.25.1] - 2026-07-30

### Changed
- **Refreshed the dependencies bundled into the tool.** `Serilog` 4.3.0 → 4.4.0, `NuGet.Versioning` 7.0.1 → 7.6.0, `Microsoft.Extensions.Logging(.Abstractions)` 10.0.2 → 10.0.10, plus `FluentAssertions` 8.10.0 and `Microsoft.NET.Test.Sdk` 18.8.1 for the test project. These landed on `main` after 3.25.0 was tagged, so the published 3.25.0 was still carrying the older assemblies — this is a `PackAsTool` package, which bundles its dependencies rather than declaring them for consumers to restore.
- No behaviour change, hence a patch. Each update was verified against the full suite; `Microsoft.Build` 18.8.2 was held back because it breaks 20 tests, and `Buildalyzer` 9, `coverlet` 10, `Spectre.Console` 0.57 and `SonarAnalyzer` 10.31 remain outstanding work items — see 3.24.0's notes and the closed Dependabot PRs for the detail.

### Testing
- No new tests. 1107 pass.


## [3.25.0] - 2026-07-30

### Added
- **`DocumentationDriftTests`: the documentation is now held to the tool.** A flag that exists and is undocumented is invisible to most users; one that is documented and no longer exists is worse, because someone writes it into a CI script and finds out from an exit code. Neither failure shows up in a build, a test run, or a review of the code that changed — the docs are a different file, and nothing was checking them. This release series added eleven options and a rule, and one option had already slipped through undocumented (`--gitignore-dir`), which is the argument for asserting it rather than remembering it.
  - Every `[Option]` long name appears in the README **reference table**, and **every option documented there still exists**. Both directions read the table rather than the whole file: searching everywhere let an option mentioned only in an example count as documented, and the reverse check cannot catch that because it validates only rows that already exist. **Found in cross-review** — the direction that costs someone an afternoon. Only the options tables are scanned, so prose mentioning `dotnet`'s own flags cannot confuse it.
  - Every exit code appears in the exit-code table **under its own name**, so the number and the meaning cannot drift apart. That table is the contract a CI script is written against and the one thing a script cannot discover by trying.
  - Every rule in `AnalysisIssueCode` has **a section of its own** in `docs/rules.md` — the page a SARIF annotation's help link points at. Matched on the heading structure, not on the name appearing somewhere: a rule whose section was deleted but which is still named in a cross-reference would otherwise pass, which is precisely the drift being guarded against. **Also found in cross-review.**
  - Modelled on `OutputSchemaDriftTests`, which does the same job for the JSON contract.

### Fixed
- `--gitignore-dir` is documented in the README options reference. It has existed undocumented; the test above is what found it.

### Testing
- 4 new tests. 1107 pass.

### A note on the version
This is 3.25.0, not 4.0.0. Nothing in this release series broke a documented contract — the one change that could have, findings identifying projects by relative path rather than file name, shipped in 3.10.0 with a baseline-regeneration instruction and an explicit rejection of old baseline files. A major bump would say something untrue about what upgrading costs.

## [3.24.0] - 2026-07-30

### Added
- **`ScanWorkTests`: the properties any change to the scan's scheduling has to hold.** Not a duration — a timing assertion in CI is a flake generator, and it would not catch what actually goes wrong here. Every performance change to this scan has had the same failure mode available to it: parallelism that silently *erases* findings, producing a clean report with a successful exit code. What is pinned instead: findings do not depend on `--max-parallelism`; a solution whose projects share one directory still reports its inconsistency; layouts that redirect their intermediate output — directly, via `Directory.Build.props`, through an import chain, or into another project's `obj` — still report theirs; and the same solution produces the same report twice, so merging results in completion order rather than project order would fail.

### Fixed
- **A `--fix` regression from 3.23.0's fallback path.** Reworking the reference pass dropped the fix that stopped `ScanProjectPackages` output being reused as the declared-reference list. That scan records no `Condition`, so a framework-conditional pin read as an ordinary one and `--fix` unified every other project to it — `Serilog 99.0.0` written into two projects that wanted `2.0.0`. Caught by the test written for this exact bug one release earlier, and reproduced outside the suite before fixing. Second time this line has needed it.

### Not shipped: concurrent package resolution
An attempt to parallelise the `dotnet package list` phase was written, measured at **two to four times faster** on 60 projects (39s → 9.6s idle, 46s → 19s loaded), reviewed, and **abandoned**. It is recorded here because the speedup is tempting and the reasoning is the useful part.

`dotnet package list` restores, and two restores writing the same `project.assets.json` race on it: the loser reports the other project's packages, so two projects with different versions of a package report the same one and the version-inconsistency finding disappears. Silently, exit code 0.

Whether two projects share that file means knowing where it goes. Eight rounds of review found eight distinct routes to a shared one: a conditional property; one set in an imported `Directory.Build.props`; one built from `$(…)`; an import reaching a *child* directory; `MSBuildProjectExtensionsPath` outranking `BaseIntermediateOutputPath`; `ProjectAssetsFile` naming the file outright; one project redirected into another's default `obj`; and `--batch-parallel` putting two solutions' projects in flight at once. Every fix was correct and the next round found another — which is the signal that the approach was wrong rather than unfinished. The answer is only available from full MSBuild evaluation, and evaluation is precisely what that phase cannot do, because MSBuild's object model is not thread-safe.

So the speedup stays unshipped. A scan that silently reports fewer findings is a worse product than a slow one, and this release series exists to remove exactly that failure mode. The reasoning is preserved in `AnalysisHandler.ScanProjectsAsync`, and the tests above are what a future attempt has to satisfy.

### Testing
- 8 new tests. 1103 pass.

## [3.23.0] - 2026-07-30

### Fixed
- **`--fix` said "No changes were needed" when it could not change anything.** Both fixers that edit project XML caught every exception and returned `null`, and the caller could only read `null` as "there was nothing to change". So on a read-only, locked, or malformed project file the run printed that reassuring line **directly above** "1 issue(s) could not be fixed automatically" — two statements contradicting each other, the comforting one first — and discarded the actual cause.
  - **The exit code was already correct.** The post-fix rescan finds the surviving finding and gates on it, so `--analyze --fix --fail-on …` in CI never passed on unrepaired work. What was wrong was everything a human reads: someone running it interactively was told the finding needed no action, with no hint that a permission problem existed.
  - A failure now carries its reason and names the file: `Could not modify Api.csproj: Access to the path … is denied.`
  - **One file that cannot be read still does not stop the others.** That behaviour was deliberate and is kept — a malformed project in a large solution should not block fixing the rest — but the skipped files are now listed rather than passed over in silence.
  - "No changes were needed" is printed only when nothing changed *and* nothing failed.
  - A partial result is rendered as *partially* fixed rather than at success level, and the summary counts issues whose fix changed at least one file. Reported at success level, a green `Fixed: …` sat directly under the error explaining what had failed; counted only when successful, the summary printed `Applied 0 fix(es) affecting 1 file(s)` after a file had demonstrably been modified. **Both found in cross-review**, one round after the change that introduced them.
  - **A partial failure is not a success.** An issue spanning several projects where one write succeeds and another fails used to be reported as fixed, so the summary said nothing about the half still broken. It is now reported as unfinished while keeping the changes it did make — those still have to be listed, and under `--fix-dry-run` they are the entire output. **Found in cross-review.**

### Testing
- `FixFailureReportingTests` (5 new) drives the real CLI against a read-only project file and asserts what the user is told, that the file is left byte-identical, that a successful run still reports success and says nothing about failures, that the message survives for the case where it is true, and that a fix which changes one file but not another is not counted as fixed. 1090 pass.

## [3.22.0] - 2026-07-30

### Added
- **Every analysis rule is now proven end to end — no exemptions left.** 3.21.0's guard exempted four rules (`SecurityVulnerability`, `OutdatedPackage`, `DeprecatedPackage`, `TransitiveConflict`) as needing a live NuGet feed. That is true of the *query* and false of everything after it — and everything after it is precisely where the 3.20.0 defect lived: a parser reading a shape the feed does not produce, reporting nothing, and looking clean. Exempting the rules left that part unexamined.
  - `RecordedFeedOutputTests` drives all four from JSON **captured from real `dotnet package list` runs**, not written from documentation. That distinction is the point: the 3.20.0 bug survived because its fixtures were plausible rather than real.
  - Real output does things a hand-written fixture would not, and each is now pinned: the advisory URL key is `advisoryurl`, lower case, unlike every neighbouring key; a project with **no findings has no `frameworks` key at all**, not an empty array; a transitive entry carries `resolvedVersion` and no `requestedVersion`.
  - The exemption list is empty and a test keeps it that way, so adding one requires a reason that survives the question "could this be driven from recorded output instead?"

### Fixed
- **A `--transitive` scan invented duplicate-reference findings.** Found immediately by the above. When the declared-reference list is unavailable the resolved list stands in, and under `--transitive` that list holds the same package twice — once as a direct reference, once as a transitive of something else. `RedundantReference` read that as the project declaring it twice, reporting a defect in a project with one perfectly ordinary reference. A transitive entry is not a declaration; nobody wrote it in the project file.

### Testing
- 8 new tests. 1085 pass. One of them documents a wrong turn worth recording: the first `TransitiveConflict` test asserted a direct-versus-transitive version difference, which no rule claims to report and NuGet resolves on its own. A test asserting the wrong contract is one of the ways a rule ends up looking covered when it is not.

## [3.21.0] - 2026-07-30

### Added
- **An end-to-end guard that every rule can actually fire.** 3.20.0 found an analyzer that had never produced a finding on any real project while its unit tests passed throughout — because those tests hand-authored an input the pipeline never delivers. Every analyzer here has unit tests; unit tests prove the analyzer's logic, not that what reaches it has the shape the logic expects. Nothing was asking the question end to end.
  - `EveryRuleCanFireTests` builds real projects, solutions, props files, and an assets file on disk, runs the actual CLI through `ProgramRunner`, and reads the findings back out of the JSON a consumer would parse. Nine rules are provoked this way.
  - **Every member of `AnalysisIssueCode` must be accounted for**: either a test proving it fires, or an entry in a list of rules that need a live feed, with the reason. A rule added with neither fails the build, so this cannot fall behind the analyzers the way the unit tests did. A companion test rejects an exemption for a rule that no longer exists.
  - A negative case asserts a healthy solution produces no findings, so none of the above can be satisfied by an analyzer that reports unconditionally.

### Fixed
- **`RedundantReference` could not fire.** It reads package references from `dotnet package list`, which reports the **resolved** graph — and resolution collapses two `PackageReference` items with the same `Include` into one. Confirmed directly: a project declaring `Newtonsoft.Json` twice yields exactly one entry in that output. So the duplicate the rule exists to find was gone before the analyzer saw it, and the rule only ever worked on the XML fallback path used when the resolved scan *fails*.
  - `ProjectPackageInfo` now also carries the references **as declared**, read from the project files, and rules about what a file says use those. The resolved list stays authoritative for everything about what restore produced.
  - The project files are read on the success path only. When a `--transitive` scan fails the XML scan is deliberately not consulted, because it cannot see transitive packages and standing in for the failed scan would turn "we could not look" into "there is nothing there".

- **The `RedundantReference` fixer could not fix.** It resolved each affected project by matching `ProjectName`, but findings have identified projects by **path relative to the scan root** since 3.10.0 — file names were dropped as identifiers because two projects can share one. So the match never succeeded, the fixer found no project, and it returned "no fix needed". `--analyze --fix` printed **"No changes were needed"** over an unrepaired finding: two statements each true and jointly misleading.
  - `ProjectPackageInfo.ResolveProjectPath` does the reverse lookup, so no caller has to know how a finding's project entry maps back to disk.
  - Verified on a real project: the duplicate reference is now removed from the file, and a test asserts it on the file rather than on the exit code.

- **Conditional declarations are no longer mistaken for defects — by any rule or fixer.** Found in cross-review, and the most serious item here: declaring a package once per target framework behind a `Condition` is how multi-targeting is written, and making `RedundantReference` fire turned a rule that quietly reported nothing into one that would have had `--fix` **delete the declaration another framework depends on**. MSBuild conditions cannot be evaluated outside a build, so overlap is not guessed at: a duplicate is reported only among declarations that always apply.
  - Fixing that exposed the same flaw in `VersionInconsistency`, which reads the *resolved* list where conditions no longer exist. It saw `4.0.0` and `4.3.0` in one multi-targeted project, called them inconsistent, and — being fixable — unified them to `4.3.0`, breaking the `net8.0` target. A per-framework pin is now excluded from the comparison, and the fixer refuses to rewrite a conditional declaration even if something else reports one.
  - A condition is detected anywhere in the ancestor chain, not just on the item and its group: a declaration inside `<Choose><When Condition=…><ItemGroup>` carries none on either, so checking two levels read a mutually exclusive pair as duplicates of each other. **Found in cross-review.**
  - The fixer refuses to remove a conditional declaration even when real unconditional duplicates exist alongside one — otherwise a project with two duplicates *and* a framework-specific pin had the pin deleted, which is precisely what the analyzer's filter exists to prevent. **Also found in cross-review**, and asserted on the file.
  - A conditional pin does not decide the version other projects are unified to. The analyzer excluded it from the comparison while the fixer still drew its target from every reference, so a framework-conditional `99.0.0` would drag unconditional `1.0.0` and `2.0.0` up to `99.0.0` — on the strength of a finding that mentioned only `1.0.0` and `2.0.0`. Not writing *to* a conditional declaration is not enough if it still decides what gets written elsewhere. **Found in cross-review.**
  - An `<Otherwise>` branch counts as conditional. It carries no `Condition` attribute but applies exactly when no sibling `<When>` did, so reading it as unconditional made the fallback branch a deletion or rewrite candidate once real duplicates existed elsewhere in the file. **Found in cross-review**, and it showed the `<Choose><When>` test was passing for the wrong reason.
  - A project that pins a package unconditionally *and* overrides it for one framework still reports a genuine inconsistency against another project, while the override stays out of the comparison. The question is asked **per version**, not per package: asking per package either hid the unconditional pin from comparison or left the conditional override in it, where the `Highest` strategy would pick the override and rewrite every other project to a version meant for one framework. **Both halves found in cross-review**, one round apart.
  - Reporting a finding a fixer then declines to act on is its own kind of wrong — the same "no changes were needed" over an unrepaired finding as above — so the finding is suppressed rather than merely made unfixable.

- **The fallback path reads declarations properly too.** When `dotnet package list` fails, the stand-in scan that replaces it is *not* reused as the declaration list: it drops versionless items — every reference under central package management — and records no conditions, so reusing it missed duplicates for most users and could report a framework-conditional pair as a fixable duplicate the fixer then refuses to touch. **Found in cross-review.**

- **A declaration-read failure is an incomplete analysis, and exits 8.** When the resolved scan succeeded but a project file could not be read, `RedundantReference` was not evaluated for that project — yet the run reported a clean, complete result with a success exit code, so neither a JSON consumer nor a CI job could tell that from "nothing found". Declaration failures now count towards the existing incomplete-analysis accounting, once per project rather than twice when both reads failed. **Found in cross-review**, and it is the exact failure mode this release exists to close.

- **A project whose declarations could not be read is named, not silently skipped.** A partial failure cannot be covered by falling back to the resolved list for the missing projects, because that list has already collapsed the duplicates declarations exist to reveal. Those projects are simply not checked — which is honest only if it is said out loud, since otherwise "no duplicates found" is indistinguishable from "we could not look". **Also found in cross-review.**

- **A successful declaration scan that finds nothing is an answer, not missing data.** The fallback to the resolved list now triggers only when the project files could not be read at all. Otherwise, with `--transitive` on a multi-targeted project, the resolved parser listing the same transitive package once per framework would read as duplicate *declarations* of a package the project never declares. **Found in cross-review**, on a fallback flagged for scrutiny in the PR description.

- **`RedundantReference` now fires under central package management.** Also found in cross-review. The first fix read declarations through a scan that drops `PackageReference` items with no `Version` — and under CPM a reference normally *has* no version, so for the majority of this tool's users the rule still could not fire, having just been "fixed". A dedicated declaration scan keeps versionless items, and records whether each was conditional.

### Testing
- 23 new tests. 1078 pass.

## [3.20.0] - 2026-07-30

### Fixed
- **`RedundantDirectReference` had never reported a single finding.** The analyzer reads the resolved dependency graph out of `project.assets.json` and looks each package up by composing `Name/Version`. The version it composed with came from `project.frameworks.<tf>.dependencies`, where **NuGet writes a version range** — `"[7.0.0, )"` for an ordinary reference — while the `targets` section is keyed by the **resolved** version, `"Serilog.Sinks.File/7.0.0"`. So every lookup built a key like `Serilog.Sinks.File/[7.0.0, )`, matched nothing, and returned no dependencies. The transitive closure was always empty, so no reference was ever redundant.
  - It failed as a clean result rather than as an error: the analyzer ran, succeeded, and reported nothing, which is indistinguishable from a project that has no redundant references.
  - **Its tests passed because their fixtures could not occur.** They declared dependencies as `"version": "1.0.0"`, the one form real restore output never contains — and the only form the broken key happened to match. Those fixtures now use ranges, so nobody re-derives a version-keyed lookup from them.
  - Lookup is now by **package name** against the resolved graph. Restore settles on exactly one version per package per target framework, so the name identifies a node and no version needs parsing, comparing, or reconstructing.
  - **Verified end-to-end on real restored projects**: a direct `Serilog` at the version its provider already requires is reported; the shipped code reported nothing for the same input.

- **A reference that pins higher than anything else requires is no longer reported.** Reachability alone is not redundancy, and this is where a missing finding turns into a harmful one. A project referencing `Serilog.Sinks.File` 7.0.0 and `Serilog` 4.3.0 directly: the sink only requires Serilog 4.2.0, and restore settled on 4.3.0 *because of* the direct reference. Serilog is reachable, so reachability calls the reference redundant — but removing it silently downgrades Serilog to 4.2.0. The finding would read as a tidy-up and land as a regression.
  - The test is whether the version that would be resolved *without* the reference still satisfies the range the reference declares. That is asked of the range itself, not by comparing floors: a floor comparison cannot tell `[4.3.0, )` from `(4.3.0, )` — both report a minimum of 4.3.0 — so a provider requiring exactly 4.3.0 looked sufficient for a reference that excludes it. **Also found in cross-review.** Asking the range settles exact pins and upper bounds at the same time. Handled with `NuGet.Versioning`, already a dependency, rather than by string comparison.
  - **Found in cross-review**, and it invalidated the first end-to-end check written for this release: that check's own finding was the unsafe kind.

- **A reference redundant under only some target frameworks is no longer reported.** Per-framework findings were unioned, so a package transitive under `net10.0` but independently required under `netstandard2.0` was advised for removal — and the advice looked exactly as confident as a correct one. A reference is now reported only when *every* framework that declares it agrees. A framework the project declares but that is absent from `targets` cannot be judged, so a reference it declares is not reported either: the cost of silence is one missed finding, the cost of a guess is a broken restore. **Found in cross-review.**

### Changed
- A `ProjectReference` listed among a framework's dependencies is no longer treated as a package. It appears there with `"target": "Project"` and no version, and whether one project reference is reachable through another is a different question from package redundancy — not one this analyzer is entitled to answer.
- Traversal tolerates the graph shapes a real file produces: a missing `targets` entry for a declared framework, an assets file truncated by an interrupted restore, inconsistent casing between the dependency list and the target keys, and cycles.

### Testing
- `DependencyGraphRealAssetsTests` (26 new) uses the shapes real restore output actually contains, which is the entire point. Covers five range forms (floor-only, bare, exact pin, bounded, exclusive floor), a chain several levels deep, case-insensitive IDs, project references, a framework absent from `targets`, a truncated file, and a cycle — plus both version-intent directions, seven declared-range forms against the version that would remain (inclusive and exclusive floors, exact pins, bounded ranges), the highest-requirement-wins case, and three multi-targeting cases. 1056 tests pass.

## [3.19.0] - 2026-07-30

### Fixed
- **A merge wrote new pins in a different style from every entry around them.** `AddMetadata` defaults to element form, so `--merge` produced `<PackageVersion Include="Bravo"><Version>2.0.0</Version></PackageVersion>` next to entries using `Version="2.0.0"`. One file, two styles for the same thing, and a three-line diff for a one-line addition. New pins now match the style the file already uses: attribute form for a file that uses it or mixes both (what `Generate` emits and NuGet's own docs use), element form for one written consistently that way. An entry someone deliberately wrote as a child element is updated in place and left in element form, because rewriting it would be a diff they did not ask for.
  - Found in cross-review: the first fix wrote attributes unconditionally, which recreated the same mixture in the other direction on a consistently element-form file.

- **A merge could take a comment away from the pin it documents.** Inserting an entry at its sorted position put it *between* a comment and the following item, so a team's `<!-- Pinned: 2.x drops netstandard2.0, see #412 -->` silently came to describe a different package. That is worse than losing the comment, because the file still reads as if it were right.
  - MSBuild's object model does not expose comments at all — they survive a merge only because the underlying document is round-tripped whole, which is also why nothing in the model can be positioned relative to one. So comment positions are read from the file's own lines, and where a comment occupies the sorted position **the new entry goes one slot later instead**. One entry marginally out of order is a strictly smaller problem than a misattributed explanation.
  - A comment that is the item group's first child is treated as a header for the group rather than for whichever entry happens to be listed first, so a new alphabetically-first pin goes below it. "First child" means exactly that, not "first `PackageVersion`": a group can open with a `GlobalPackageReference`, and a comment below that is documenting the pin it sits above. **Found in cross-review.**
  - That concession does not compound: honouring a comment must not make the next merge read the position it had itself just forced as evidence the file is unsorted, give up, and append everything from then on. The order check therefore undoes exactly that displacement — across the whole run of consecutive commented entries a pin may have been pushed past, and across every pin a single merge pushed past the same comment, not just one step or one pin — and then requires the result to be ordered outright — not "ignore any inversion after a commented entry", which would hide inversions a comment had nothing to do with and override a hand-arranged file. **Found in cross-review**, along with the style question above.
  - A trailing comment on an entry's own line documents *that* entry, so it does not displace the next one. **Found in cross-review**: accepting any line ending in a comment close gave up a pin's sorted position for a comment that was never in the way, turning an ordered file unordered.
  - **Known limitation:** when a pin is inserted after an entry carrying a *trailing* comment on its own line, MSBuild's round-trip moves that comment onto a line of its own. It stays immediately after the entry it describes, so nothing is lost, but a reader may now take it as heading the line below. MSBuild's construction API cannot position anything relative to a comment or control that whitespace, so this is a boundary of the approach rather than something left unfinished.
  - One case is undecidable and is documented as such: `Alpha, <!--why--> Zulu, Bravo` is byte-identical to what inserting `Bravo` into `Alpha, <!--why--> Zulu` produces, so nothing can separate a hand-written file of that shape from this code's own output. It is read as ordered, the benign reading.

- **A merge no longer decides its own insertion position.** `ItemGroup.AddItem` chose where to put things: given an *unordered* group it inserted at the top, which is neither ordered nor where the author would have put it. A group that is already in order has new entries sorted into place; one that is not is left exactly as its author arranged it, with the new pin appended — reordering someone's file to match our preference would produce a diff far larger than the change they asked for, and would move comments away from what they document.

### Changed
- **Package ordering is explicit rather than culture-sensitive.** `OrderBy(x => x.Key)` uses the ambient culture's collation. Measured against realistic NuGet IDs, ICU and case-insensitive ordinal agree for every locale checked except `tr-TR` — so this is a latent hazard rather than an ordering anyone has been bitten by. It is worth removing anyway because of what it depends on: the current culture, the ICU version on the box, and whether the host was built with invariant globalization, which silently falls back to ordinal. None of those belong in the byte order of a committed file. Ordering is now case-insensitive (NuGet treats IDs case-insensitively, and pure ordinal would strand every lower-cased ID in a block after the upper-cased ones) with an ordinal tie-break, so it is total and cannot depend on input enumeration order.

### Testing
- `PropsOrganizationTests` (28 new) covers determinism across four cultures, ordinal placement of `.` and `-`, insertion into ordered and unordered groups, comment preservation and non-reattachment, the group-header case, style matching in both directions, and idempotence — running a merge on its own output must produce no second diff, which is the property a committed generated file needs above all others. 1030 tests pass.

## [3.18.0] - 2026-07-30

### Fixed
- **The interactive wizard could answer its own questions.** Every prompt worked out what an answer meant by reading its label back — `choice.StartsWith("Yes")`, an exact match against the display string, `selection[3..].TrimEnd('/')` to recover a directory name, or a dictionary lookup ending in a permissive default. Each of those had a silent wrong answer waiting behind it, and none of them failed loudly:
  - An answer matching nothing **fell through to a default**. In the mission menu that default was `CustomMigration`, so an unrecognised selection began rewriting project files — the worst possible outcome for a choice nobody made. In the conflict prompt the `_ => Highest` arm meant an unmatched label took the highest version of every conflict without being asked to.
  - **Wording was load-bearing without saying so.** Rewording "Yes" to "Include them" silently flipped an option to `false`, and nothing anywhere would have caught it.
  - Recovering a directory name by slicing three characters off the label assumed the first three were the decoration the wizard itself added. A directory literally named `📁 src` sliced back correctly only by luck of the emoji's byte width.

  Answers now carry their values, so no caller interprets a string, and **an answer that was not offered throws** rather than resolving to something plausible. The distinction matters because a wizard that quietly proceeds with an option the user did not choose is indistinguishable, downstream, from them having chosen it.

- **A CPM root whose projects live in subdirectories could not be selected.** A repository with `Directory.Packages.props` at the top and projects under `src/` is the ordinary shape of a migrated solution, and exactly what `--analyze` is pointed at — but the browser gated "use current directory" on solutions or projects being present in that directory itself, leaving no way to accept the root. The only exits were to descend into a single project or type the path by hand. This had been broken for as long as the option existed and was invisible precisely *because* of the bug above: selecting an option that had not been offered fell through to returning the current directory anyway, so the missing option appeared to work.

### Changed
- `InteractiveService` prompt handling is a single `AskChoice<T>` mechanism (the last refactor listed in `NEXT_STEPS.md`). Thirteen prompts, the mission menu, the conflict strategy, the backup options, and the directory browser all route through it.

### Testing
- `InteractivePromptRoutingTests` pins the invariant rather than the wording: every conflict answer maps to its own strategy (only two of the four were previously distinguishable from a miss), an unoffered answer throws, navigation lands where the user pointed, a directory named like the browser's own decoration still resolves, and a nested CPM root is selectable.
- **Five existing wizard tests were asserting less than they appeared to**, and the throw exposed it — their queued answers did not line up with the prompts actually asked. `RunWizard_AnalyzeMode_ReturnsCorrectOptions` was four answers short, so `AuditSecurity` came out `true` without being chosen and the later prompts took whatever the fake console returned by default; `RunWizard_MigrationMode_ReturnsCorrectOptions` named the clean migration path but ran `CustomMigration`, because with no project in the test directory that action was never in the menu. All five now match the real prompt sequence and assert the options they produce. 1002 tests pass.

## [3.17.1] - 2026-07-30

### Changed
- **`CommandRouter` dispatch is a table rather than an if/else chain** (the remaining refactor called out in `NEXT_STEPS.md`, and one this release series had made worse by adding branches to it). Behaviour-preserving: all 984 existing tests passed unchanged.
  - The point is not tidiness. Which mode wins when several flags are present — `--update` with `--interactive`, say — is real CLI behaviour that was decided *implicitly*, by the order statements happened to appear in. As an ordered table that precedence is a single readable list, and `CommandRouterDispatchTests` now asserts it: pure-output commands beat everything, `--update-packages` beats `--interactive`, no mode flags falls through to the default action, and the reporting contract is checked before any mode runs.
  - Pure-output commands (`--completions`, `--explain`) are grouped behind one guard that documents why they run before anything else: they must not be preceded by anything that writes to stdout, or a documented redirection produces a corrupt file.
  - Handlers take a single `CommandContext` rather than the union of every handler's parameters.

## [3.17.0] - 2026-07-30

### Added
- **A published JSON Schema for the `--output Json` payload**, at `schemas/cpmigrate-output.schema.json`. The contract has been versioned since 1.0.0 but never described, so a consumer had to infer the shape from example output — which means inferring it wrong at the edges, where the fields that matter live (`success` being true with findings present, counters that are absent rather than zero).
  - Documents every field of every object, including *why* rather than only *what*: that `success: true` does not mean "no findings" when they were below the `--fail-on` threshold or accepted by a baseline, that an absent `summary` counter means the command did not produce it rather than producing zero, and that `affectedProjects` holds scan-relative paths as of schema 1.3.0.
  - `additionalProperties: false` throughout, so validating a payload catches a misspelled field rather than silently accepting it.
  - **Both payload shapes are modelled.** `--batch` serializes a different type entirely — one result per solution, with no top-level `exitCode` or `summary` — so a single root schema would have rejected valid batch output. The root is a `oneOf`, and a consumer distinguishes the two by `operation` or by the presence of `solutions`.
  - **Guarded against drift by reflection**, not by a new dependency: `OutputSchemaDriftTests` compares the schema's properties against the model's `[JsonPropertyName]` attributes for all ten payload types (honouring `[JsonIgnore]`, since `BatchResult` derives an exit code it never serializes), checks the `severity` and `failOnSeverity` enums against the C# enums, and asserts the serializer emits nothing the schema does not document. A field added without updating the schema fails the build — the same approach that already caught the config schema missing `Sarif`.

## [3.16.0] - 2026-07-30

### Added
- **`--explain <RuleId>`: rule documentation from the terminal that produced the finding.** A rule ID in a build log or a SARIF annotation is the moment someone needs to know what the rule means, and the moment they are least likely to go looking for a docs site. The catalog is already in the binary; this makes it reachable. `--explain all` lists every rule.
  - Case- and whitespace-insensitive, because nobody types `VersionInconsistency` correctly from memory every time.
  - A near miss suggests the real rule, matched by bounded edit distance rather than substring: `--explain VersionInconsistncy` still finds `VersionInconsistency`, which is the case the feature exists for. The distance budget scales with query length, so something unrelated suggests nothing — a list containing every rule is no more useful than silence.
  - An unrecognised ID exits non-zero, so a typo in a CI script is visible rather than silently printing nothing useful.
  - Prose is wrapped rather than left to the terminal, and the output states that the same ID appears as `issueCode` in JSON and `ruleId` in SARIF — which is the link between a report and this text.
  - A test asserts every rule in the catalog is explainable, so a rule added without documentation fails the build.

## [3.15.0] - 2026-07-30

### Changed
- **`--audit`, `--outdated`, and `--deprecated` now query projects concurrently.** Each of those shells out to `dotnet package list` once per project and then waits on the network, so the scan scaled linearly with solution size for no reason — a large solution spent nearly all of its wall clock idle. Measured on 10 projects with `--outdated`: **12s → 6s**. Bounded by `--max-parallelism`, defaulting to the processor count capped at 8, because past that the feed starts rate-limiting and the scan gets slower and noisier rather than faster.
  - **Reading package references stays serial, deliberately.** That pass goes through MSBuild's object model, whose static caches are not thread-safe; running it concurrently produced projects reporting each other's package versions, which *erased* version-inconsistency findings rather than crashing. A parallel analyzer that silently reports fewer problems is worse than a slow one, so only the process-isolated queries are parallelized.
  - The ceiling is enforced **process-wide**, not per scan. `--batch-parallel` processes several solutions at once, and a per-scan limit would have multiplied the advertised cap by the number of solutions — producing exactly the feed rate-limiting the cap exists to avoid.
  - Results are merged in project order rather than completion order, so a report is identical run to run — verified at parallelism 1, 2, 8, and 16.

## [3.14.0] - 2026-07-30

### Fixed
- **A failed version lookup was reported as "up to date".** `GetLatestVersionAsync` returned `null` both for "this package is current" and for "the request failed", so a single 503 or timeout during `--update-packages` silently dropped that package from the update set — and the run finished with **"Everything up to date!"**. On a slow connection or a rate-limited feed, a large solution could skip most of its updates and say nothing.
  - Transient failures are now **retried** three times with exponential backoff and jitter, honouring a server-provided `Retry-After` up to a 5-second cap so the CLI cannot appear to hang.
  - `404` is treated as definitive — a package that does not exist will not start existing, and three waits per missing package is pure latency. A malformed response body is likewise not retried, since retrying will not make it parse.
  - Whatever still fails is **named**, and the run no longer claims everything is current: "Could not check N package(s) after retries: …", followed by "These are reported as unchanged, not as up to date."
  - `TaskCanceledException` from an `HttpClient` timeout is retried, but the same exception from real cancellation is not — retrying a Ctrl-C would ignore the user.

  - Valid JSON that is not a version index is treated as malformed and recorded, rather than returning "no versions" and being cached as a clean answer.
  - A package that fails and then succeeds is no longer still reported as unchecked.

### Changed
- **Version lookups are cached per run.** A solution referencing one package from thirty projects previously issued thirty identical requests. Cached per instance rather than statically, because the lifetime of a CLI invocation is exactly the window in which a cached version cannot be stale. Transient failures are deliberately *not* cached — one bad moment must not become the run's settled view of a package.
- Lookup state is held in concurrent collections. Callers run up to eight lookups at once, and the cache is now keyed on the in-flight task — so two projects asking for the same package at the same moment share one request instead of racing to make two.
- `INuGetVersionLookupService` exposes `GetFailedLookups()`, so a caller can distinguish a clean result from a silently incomplete one.

## [3.13.0] - 2026-07-30

### Added
- **`--completions <shell>`: shell completion for bash, zsh, fish, and PowerShell.** CPMigrate has 45 options; remembering which of `--fail-on`, `--baseline`, and `--output` takes a value, and that the value is `Sarif` rather than `SARIF`, is not a reasonable expectation.
  - **Generated from the option metadata**, not hand-written. A hand-written script is wrong the moment an option is added and nobody notices — and a stale completion list is worse than none, because it actively suggests flags that no longer exist. Reflecting over the same `[Option]` attributes that drive parsing means the two cannot disagree, and a test asserts every option appears in every script.
  - Enum-valued options complete their values, so `--output <tab>` offers `Terminal`, `Json`, `Sarif`, `Markdown`.
  - Path options complete filenames rather than flag names.
  - zsh shows each option's help text inline while completing, which is most of what a zsh completion is for. Brackets and colons in that text are escaped, since both are structural in a zsh spec.
  - Output is deterministic, so a committed script regenerates without a diff.
  - PowerShell completes values and paths too, not just flag names — it inspects the preceding token, so `--output <tab>` offers the formats rather than repeating the flag list.
  - Paths containing spaces stay a single candidate. `COMPREPLY=($(compgen …))` word-splits, so `with space.sln` arrived as two useless suggestions; bash uses `mapfile` instead. PowerShell keeps the directory prefix already typed, since returning only the leaf name would replace `src/Ap` with `App.csproj` and silently produce the wrong path.
  - Emitted **before the config file is read**. `--completions zsh > _cpmigrate` is a documented redirection, so a "Loaded config from: …" notice would land inside the script, and a configured output format could otherwise make the reporting contract reject the command outright.
  - Verified by asking the shells themselves: the generated bash and zsh scripts are parsed with `-n`, which catches an unbalanced quote or a missing `esac` that no structural assertion would.

## [3.12.0] - 2026-07-30

### Changed
- `ProjectPackageInfo` now carries the list of projects the scan covered. Deriving it from the package references silently loses projects: the fallback scanner skips `PackageReference` items with no version, so a **correctly centralized** project contributes nothing — which would have made the new drift rules skip exactly the projects they exist to check.

### Added
- **Four rules that catch a solution drifting back off central package management.** Migrating is a one-off event; *staying* migrated is not. Someone adds a package the way they always have — `<PackageReference Include="X" Version="1.0.0" />` — and the solution is quietly half-centralized again. NuGet says nothing, because an inline version simply wins, so nothing surfaces until two projects disagree and something breaks at runtime.
  - **`InlineVersionUnderCpm`** (Moderate) — a project pins a version inline while central management is in force, overriding the central value. Recognises both the attribute and the `<Version>` child-element form, and does not fire on an empty `Version=""`, which overrides nothing.
  - **`MissingPackageVersion`** (High) — a reference with no version, inline or central. Restore fails outright.
  - **`OrphanedPackageVersion`** (Low) — a central pin nothing references. Harmless to restore, but stale pins accumulate, and once nothing uses a package its pin is indistinguishable from a deliberate one.
  - **`CpmNotEnabled`** (High) — a `Directory.Packages.props` without `ManagePackageVersionsCentrally`, so every entry in it is inert. The file looks authoritative and does nothing.
  - `GlobalPackageReference` counts as a central version, so a project referencing an analyzer package supplied that way is not reported as missing one.
  - **`VersionOverride` is reported too**, at `Low`. It is NuGet's sanctioned per-project escape hatch, so it is not a mistake the way a stray `Version` attribute is — but the project has still stepped outside the central version, which is what a reviewer needs to see.
  - When central management is **off**, only the configuration problem is reported. Continuing would flag every ordinary versioned reference in the solution as drift, since with CPM disabled an inline version overrides nothing.
  - **Transitive pinning is respected.** With `CentralPackageTransitivePinningEnabled`, a `PackageVersion` deliberately pins a package no project references directly, so the orphan check is skipped rather than reporting every such pin.
  - A central entry with an **empty** `Version` does not satisfy a reference: the entry exists but supplies nothing usable, and restore still fails.
  - **Imported `PackageVersion` entries are followed.** A props file that imports another supplies central versions through it, and missing them would report perfectly valid references as `MissingPackageVersion` — a `High` finding that fails CI on a working repository. When an import path cannot be resolved by reading XML (built from an MSBuild property, or a glob), the rules that need the complete central set stand down rather than guess; inline-version detection, which does not need it, keeps working.
  - Repeated MSBuild properties are read **last-wins**, so `CentralPackageTransitivePinningEnabled` set to `true` and later overridden to `false` is correctly treated as off.
  - `CpmNotEnabled` also honours `Directory.Build.props`, the other conventional home for the property. Reporting on the props file alone was a High-severity false positive on repositories that set it there — MSBuild resolves the property through imports, so its absence from one file proves nothing.
  - Like the other analyzers, these are gated on **data rather than a flag**: they report nothing unless the solution actually has a `Directory.Packages.props` to drift from, so a pre-migration repository sees no change. They flow through every existing surface — terminal, JSON, SARIF, Markdown, `--fail-on`, and baselines.

## [3.11.0] - 2026-07-30

### Added
- **`--output Markdown`: put the report where a reviewer will actually see it.** Neither existing format reaches a human at the moment they need it — JSON is for parsers, and SARIF only surfaces findings that map to a line in the diff under review. A dependency problem is usually about the solution as a whole, so it never appears on the diff at all, and nobody goes digging in build logs for it. Redirect this into `$GITHUB_STEP_SUMMARY`, or post it as a PR comment with `gh pr comment --body-file`.
  - Leads with the **verdict** — whether anything reached the `--fail-on` threshold — before any detail, so the answer to "did this pass, and why" is the first thing read.
  - Scan totals, a severity breakdown worst-first, and a findings table linking each rule to its documentation.
  - An incomplete scan gets a prominent `> [!WARNING]` callout, because "no findings" from a scan that did not finish reads exactly like a clean result.
  - Baselined findings are marked *(baselined)* and explained, so accepted debt is visible without looking like a live problem.
  - Long lists collapse behind a `<details>` disclosure, and a finding spanning many projects summarises its tail, so one noisy result cannot bury the rest of a job summary.
  - Package names, project paths, and descriptions are escaped: they come from files CPMigrate did not write, and a single stray `|` would silently destroy every table row after it.
  - When `--fix` applied changes, the report describes the tree **after** the fixes — what is there now, not what was there before.

### Fixed
- **A run that failed before producing findings is no longer rendered as a clean result.** `NoProjectsFound` (a misconfigured path, the common case) produced a report reading "✅ No findings" — contradicting the command's own exit code, and the exact false-clean shape this release series has been closing elsewhere. The verdict now accounts for the exit code, and a warning states that the report is not evidence of health.
- `--output Markdown` is rejected with `--batch`, where the run aggregates into a batch result this report has no shape for and would have emitted nothing at all — including leaving `--output-file` unwritten.
- **`--write-baseline --output Markdown` reported nothing about the baseline.** Recording one is the run's whole point, but the terminal confirmation is suppressed for machine-readable formats, so the report said only that no findings reached the threshold. It now leads with the outcome and names the file.
- **The projects-scanned count excluded projects with no `PackageReference`,** because it was derived from the references themselves — a solution whose projects have no packages reported zero scanned.
- Errors are now reported in the requested format. A failure under `--output Markdown` previously emitted raw error JSON into what was meant to be a rendered summary.

## [3.10.0] - 2026-07-29

### Changed (action required if you have a baseline)
- **Findings now identify projects by path, not by file name.** A project file name is not an identifier — two projects can share one — so `src/App/App.csproj` and `tests/App/App.csproj` were indistinguishable to everything downstream. That had two concrete consequences: a baseline entry accepting debt in one project silently suppressed a *new, unrelated* finding in the other, and SARIF had to guess which of several same-named files to annotate, sometimes annotating a project that never referenced the package. Findings now carry each project's path relative to the scan root (`src/App/App.csproj`), forward-slashed and free of absolute paths so a committed baseline matches on every machine.
  - **Regenerate baselines**: the fingerprint scheme is now `v2`. A `v1` baseline is rejected with an explicit instruction rather than silently matching nothing — run `cpmigrate --analyze --write-baseline` again.
  - **JSON schema 1.3.0**: `analysisIssues[].affectedProjects` holds relative paths instead of file names. This is the only field whose meaning has changed across any schema revision.
  - Terminal output and finding descriptions show the same relative paths, which also disambiguates them for a human reader.
- The `FrameworkAlignment` analyzer was the last finding source still emitting bare file names, which would have left those findings with no SARIF locations at all once resolution became exact.
- `VulnerabilityInfo` carries the project path alongside the name, since it was the one finding source that could not be traced back to a file.

### Fixed
- The "is this path inside the scan root" check tested the string prefix `..`, so a project under a directory legitimately named `..generated` was treated as outside the root and fell back to its bare file name — recreating the collision this change exists to prevent. It now tests the first path *segment*, and the same check is shared with SARIF's URI and symlink handling, which had the identical bug.

### Removed
- The package-matching heuristic SARIF used to guess between same-named projects, and the fallback that annotated *every* candidate when the guess had nothing to go on. Resolution is now an exact lookup, so both are unnecessary.

## [3.9.0] - 2026-07-29

### Added
- **`--baseline` / `--write-baseline`: adopt a CI gate on a codebase that already has debt.** `--fail-on` narrows a gate by severity; this narrows it by *which findings*. A repository with a backlog cannot turn on a gate that fails on all of it, so record the current state once (`--write-baseline`, committed alongside the code) and every run after that fails only on findings the baseline does not contain.
  - Baselined findings **stay in every report** — terminal, JSON (`suppressed: true`), and SARIF, where they are emitted as a `suppressions` entry with `kind: "external"`, which is exactly the construct the spec provides for a suppression the tool was told about. The debt stays visible; it stops blocking.
  - A finding is identified by its rule, package, and affected projects — deliberately **not** by the versions in its description. A version inconsistency drifting from `13.0.1, 12.0.3` to `13.0.2, 12.0.3` is the same unresolved finding, so the suppression holds; spreading to a new project is new information, so it does not.
  - The baseline file is reviewable: each entry carries the rule, package, severity, and projects next to its fingerprint, so accepting technical debt shows up in a pull request as a decision rather than as a list of hashes. Entries are ordered deterministically, so regenerating an unchanged baseline produces no diff.
  - **Stale entries are reported.** When a baseline entry no longer matches anything the findings were fixed, and CPMigrate says so and suggests regenerating — which is what stops a baseline growing forever and quietly suppressing a finding that came back under the same identity.
  - A baseline written under a different fingerprint scheme is **rejected** rather than silently matching nothing, because "suppressed nothing" and "no debt accepted" look identical from the outside.
  - Path settable team-wide as `"baseline"` in `.cpmigrate.json`; published schema at `schemas/cpmigrate-baseline.schema.json`.
- **JSON:** `analysisIssues[].suppressed` and `summary.issuesBaselined` (additive; schema stays 1.2.0-compatible in shape, both fields omitted when no baseline is used).

### Known limitation
- Findings identify projects by **file name**, so two distinct projects sharing a basename (`src/App/App.csproj` and `tests/App/App.csproj`) share an identity — a baseline entry for one can suppress an equivalent finding in the other. Fixing it means carrying project paths on every finding, which also removes the guesswork in SARIF location resolution; it is tracked as a follow-up rather than partially worked around, because a partial disambiguation would change every fingerprint without closing the gap.

### Changed
- **`--baseline` survives `--fix`.** Applying fixes triggers a rescan (3.8.0), which produces a fresh unsuppressed report; the baseline is now reapplied to it, so accepted debt does not start failing the build the moment an unrelated fix runs.
- Finding identity now lives in one place (`AnalysisIssueIdentity`), shared by SARIF `partialFingerprints` and baseline matching. They have to agree on what "the same finding" means, and two implementations of that would drift.

### Validation
- **A baseline is never recorded from an incomplete scan.** If a project fails to scan or an `--audit`/`--outdated`/`--deprecated` query fails, `--write-baseline` refuses and exits `8` rather than writing a file that permanently accepts findings nobody looked for — a transient audit failure would otherwise silently bless every vulnerability it missed.
- `--write-baseline` is rejected with `--batch`, where every solution would write the same file: sequentially the last wins, in parallel they race, and either way the result covers one solution while claiming to cover the repository. Reading a shared baseline across a batch is fine and still supported.
- `--baseline`/`--write-baseline` are rejected alongside any mode that runs *instead* of an analysis (`--update`, `--update-packages`, `--interactive`, `--unify-props`, `--rollback`, pruning, `--list-backups`), where the baseline would be silently ignored while a mutating operation went ahead. SARIF and baselines now share one list, so a mode added later fails loudly rather than quietly doing nothing.
- Baseline and SARIF option checks now run *before* `CommandRouter` dispatches a mode, so combinations like `--update --write-baseline` are rejected instead of performing a self-update and recording nothing.
- A structurally invalid baseline (explicit `"findings": null`, an entry missing its fingerprint, an unsupported `baselineVersion`) is reported as an error rather than faulting mid-analysis. An *absent* `findings` array is treated as a baseline that accepts nothing, which is legitimate.
- `--baseline` and `--write-baseline` require `--analyze`. `--write-baseline` is rejected with `--fix`, which would record findings from the pre-fix tree and so accept debt the same run just repaired. A missing baseline file is a validation error rather than a silent fall-back to gating on everything.

## [3.8.0] - 2026-07-29

### Added
- **`--fail-on <severity>`: gate CI on the findings that matter, not on all of them.** Every finding failed the build, which makes the gate unusable for a repository with existing debt — it fires on every run, so it gets switched off, and the vulnerability anyone actually cared about goes with it. `--fail-on High` narrows the gate without narrowing the report: findings below the threshold still appear in terminal, JSON, and SARIF output, and only the exit code changes.
  - Accepts `Info` (the default, and the pre-3.8.0 behaviour), `Low`, `Moderate`, `High`, `Critical`, and `Never` for report-without-gating — useful when a SARIF upload is the real signal.
  - Cannot suppress exit `8` (`IncompleteAnalysis`). A severity threshold says which findings matter; it does not make an unexamined project safe.
  - Settable team-wide as `"failOn"` in `.cpmigrate.json`.
  - A non-default threshold is explained on the terminal, so a run that prints "12 findings" and exits `0` reads as a policy rather than a bug.
  - Passing `--fail-on` on the command line without `--analyze` now warns, because the default action is a real migration and the flag would silently do nothing. It is a warning rather than an error so a repository that sets the policy in config can still run migrations.
- **JSON schema 1.2.0 (additive):** `summary.failOnSeverity`, `summary.issuesAtOrAboveThreshold`, `summary.highestSeverity`, `summary.scanFailures`, and `summary.deepScanFailures`. Together these let a consumer distinguish a clean run from one whose findings were below the gate, and either from one whose scan did not complete — without re-deriving the policy from the issue list. No existing field changed meaning.

### Fixed
- **`--analyze --fix` could exit `0` with a live vulnerability on disk.** Once any fix applied, the run reported success regardless of what remained — and a Critical security advisory is never auto-fixable, so `--fix` repairing an unrelated version inconsistency was enough to report the advisory as a pass. **CPMigrate now re-scans the tree after writing fixes** and gates on that fresh report. An issue's `fixable` flag says a fixer *exists*, not that it ran or succeeded, so nothing short of looking at the modified files is trustworthy. `summary.issuesRemainingAfterFixes` reports the post-fix count; `issuesFound` stays as the pre-fix count so it still lines up with the `fixes` array.
- **Batch mode silently discarded most options.** `--batch` cloned `Options` per solution through an explicit allow-list, so every option added after that list was written was dropped: a batch run ignored `--audit`, `--outdated`, `--deprecated`, and `--transitive`. A monorepo security scan therefore reported no vulnerabilities because it never looked for any, and would have ignored `--fail-on` too. The clone now copies everything and overrides only what must be per-solution (paths, quiet, and the modes the batch driver owns), so the failure direction is inverted — a new option propagates unless deliberately excluded. `BatchOptionPropagationTests` enforces that, property by property, so this cannot recur.
- **Batch analysis JSON omitted the gate metadata** it advertises through the schema version, leaving a consumer unable to distinguish a below-threshold or incomplete batch result from a clean one.
- **Enum-valued config settings never worked.** `ConfigService` deserialized without a string-enum converter, so the documented — and schema-mandated — `{ "conflictStrategy": "Highest" }` threw a parse error, the whole config file was rejected with a warning, and every setting in it silently fell back to its default. This affected `conflictStrategy` and `outputFormat` from the day config files shipped, and would have taken `failOn` with it. The generated sample config had the mirror-image problem: it wrote enums as *numbers*, which the published schema rejects, so the file CPMigrate produced showed as invalid in an editor. Both directions now use names, with a round-trip test.
- **`schemas/cpmigrate.schema.json` was missing `Sarif` from `outputFormat`,** so a valid 3.7.0 config showed as an editor error. New `ConfigSchemaDriftTests` fail the build when a config property or enum value is added without updating the published schema — the schema is hand-written, so it had no other protection.

## [3.7.0] - 2026-07-29

### Added
- **`--output Sarif`: analyzer findings as SARIF 2.1.0, for GitHub code scanning.** Teams wiring CPMigrate into CI previously had to parse CPMigrate's bespoke JSON and re-render it themselves to get findings in front of reviewers. SARIF is the format GitHub, Azure DevOps, and every static-analysis viewer already consume, so `cpmigrate --analyze --output Sarif --output-file cpmigrate.sarif` now feeds `github/codeql-action/upload-sarif` directly — findings land as annotations on the pull request diff.
  - Each finding resolves to the **project file and the exact line** declaring the offending `PackageReference`, so annotations attach to real code rather than to the repository root.
  - Every issue code ships full rule metadata — short and full descriptions, tags, and a `helpUri` into the new rule reference — so a reviewer can act on a finding without leaving the PR.
  - Results carry a `partialFingerprints` entry derived from the issue code, package, and affected projects, letting code scanning track a finding across runs instead of reopening it every build.
  - Severities map to SARIF levels: `Critical`/`High` → `error`, `Moderate` → `warning`, `Low`/`Info` → `note`.
  - Tool failures are reported as an unsuccessful SARIF invocation with a tool execution notification, so stdout is a parseable SARIF log even when a run fails — an upload step never breaks on a malformed or missing file.
- **`docs/rules.md`: a published reference for every rule CPMigrate reports.** Rule IDs are now documented as a public contract shared by SARIF `ruleId`, JSON `issueCode`, and terminal output, with the trigger, default severity, and whether a built-in fixer applies. A test fails the build if an issue code is added without a matching section.

### Changed
- `--output-file` now accepts `Sarif` as well as `Json`, and reports which format it wrote.
- Console suppression, prompt guards, and non-TTY safety checks now key off a single "machine-readable output" predicate rather than testing for `Json` at ~20 separate call sites, so any future machine format inherits the same protections instead of re-introducing banner leaks.

### Changed (behavioral — check your CI gates)
- **An incomplete analysis now exits `8` (`IncompleteAnalysis`) instead of `0`.** If a project fails to scan, or a `--audit`/`--outdated`/`--deprecated` query fails, the run produces no findings for the part it could not read — and exiting `0` told a CI gate the dependencies were clean. Exit `5` (`AnalysisIssuesFound`) still wins when real issues were found, since that is the more actionable signal. A pipeline that treats any non-zero exit as failure will now surface scan failures it previously ignored; one that gates specifically on `5` is unaffected.

### Fixed
- **A failed `--audit`, `--outdated`, or `--deprecated` query looked identical to a clean result.** Those scans returned a success flag that was discarded, so a NuGet query that never completed simply contributed no findings — and "no vulnerabilities found" was reported for a vulnerability scan that did not run. The failures are now counted, warned about on the terminal, and reported through SARIF as an unsuccessful invocation. This affected every output format, not just SARIF.
- **`--verbose` corrupted machine-readable stdout.** The "Verbose logging enabled: …" notice was written before the payload, so `--output Json --verbose` emitted prose ahead of the opening brace and no longer parsed as JSON.
- **An unwritable `--output-file` aborted the process.** The failed write threw, and the error handler retried the same path from inside its catch block, throwing again and terminating with an unhandled exception instead of reporting the original problem. Failure payloads now fall back to stdout.
- **SARIF annotations were lost for solutions that reference projects above themselves.** A solution under `build/` referencing `../src/App.csproj` put that project outside the scan root, forcing an absolute `file://` URI that code scanning cannot map to a checked-out file. The URI base now widens to the common ancestor of every reported project.
- **SARIF line locations are resolved by parsing the project as XML, not by matching text.** A project file is XML, and a text search got several valid forms wrong: a commented-out `PackageReference` above the live one won, single-quoted attributes (`Include='Serilog'`) never matched at all, and `Update=` declarations were invisible. An unparseable project now falls back to a file-level annotation instead of a wrong line.
- **Artifact URIs are percent-encoded.** `artifactLocation.uri` is a URI reference, so a project path containing a space, `#`, or `%` produced an invalid location that a consumer could reject or resolve to the wrong file.
- **`--output Sarif` is now rejected for modes that cannot produce findings.** `--update`, `--interactive`, `--unify-props`, `--update-packages`, `--rollback`, `--prune-backups`, `--prune-all`, and `--list-backups` are dispatched before per-command validation, so `--update --output Sarif` previously ran a real self-update and emitted no SARIF at all. Each is named explicitly, so passing `--analyze` alongside one no longer slips past the check.
- **`--output Sarif` is rejected with `--fix`.** The report describes the projects as they were *before* the fixes were written, so uploading it would annotate findings that no longer exist. `--fix-dry-run` changes nothing and is still allowed.
- **Findings no longer annotate unrelated projects that share a file name.** Analyzer findings carry project *names*, so in a solution with both `src/App/App.csproj` and `tests/App/App.csproj` a finding against one was reported on both. Locations now resolve through the project that actually declared the package, falling back to every candidate only for findings that are not about a single package.

- **Symlinked projects annotate the real file.** A project referenced through a symlink was reported at the link path, which a code-scanning consumer cannot display. The link is now followed — unless its target lies outside the scan root, where the in-repository link path is the more useful of the two.

### Repository
- `.gitignore` now covers `cpmigrate.log` / `cpmigrate*.log`. CPMigrate writes its own `--verbose` log into the working directory, so running the tool inside its own repo left a machine-specific log staged for commit.

### Validation
- `--output Sarif` requires `--analyze` (SARIF carries only analyzer findings) and is rejected with `--batch`, `--interactive`, and `--interactive-conflicts`.

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

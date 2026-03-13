# Launch Assets

Use these assets when announcing a release, docs refresh, or new install channel. Keep the CTA consistent: install CPMigrate, run one command on your solution, and inspect the output.

## Asset 1: Migrate to Central Package Management

Target page: `https://georgepwall1991.github.io/CPMigrate/guides/migrate-directory-packages-props/`

Suggested post copy:

> Migrating a .NET solution to `Directory.Packages.props` is easy to underestimate.
>
> CPMigrate generates the central props file, surfaces version conflicts before they break CI, and lets you preview the migration before writing changes.
>
> Install it, run a dry run on your solution, and inspect the output:
>
> ```bash
> dotnet tool install --global CPMigrate
> cpmigrate -s ./YourSolution.sln --dry-run
> ```
>
> Guide: https://georgepwall1991.github.io/CPMigrate/guides/migrate-directory-packages-props/

Proof asset: `docs/images/cpmigrate-demo.gif`

## Asset 2: Find Dependency Drift Before CI Breaks

Target page: `https://georgepwall1991.github.io/CPMigrate/guides/ci-cd/`

Suggested post copy:

> If your .NET solution has package drift, duplicate references, or vulnerable dependencies, you should catch it before a build fails.
>
> CPMigrate turns that into a CI-friendly dependency analysis step with JSON output.
>
> ```bash
> cpmigrate --analyze --audit --outdated --deprecated --output Json --quiet > analysis.json
> ```
>
> CI/CD guide: https://georgepwall1991.github.io/CPMigrate/guides/ci-cd/

Proof asset: JSON output example from the CI guide.

## Asset 3: Update Packages Safely with Rollback

Target page: `https://georgepwall1991.github.io/CPMigrate/guides/safe-package-updates/`

Suggested post copy:

> `dotnet package list` tells you what changed. It does not give you a safe update workflow.
>
> CPMigrate can preview package updates, include transitive dependencies, run your tests, and roll back automatically if verification fails.
>
> ```bash
> cpmigrate --update-packages --dry-run
> ```
>
> Guide: https://georgepwall1991.github.io/CPMigrate/guides/safe-package-updates/

Proof asset: before/after package update example plus rollback flow.

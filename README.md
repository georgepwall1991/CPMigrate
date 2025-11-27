# CPMigrate

A stunning CLI tool to migrate .NET solutions to [Central Package Management (CPM)](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management).

![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/CPMigrate.svg)](https://www.nuget.org/packages/CPMigrate/)

![CPMigrate Demo](./docs/images/cpmigrate-demo.gif)

## Features

- **Interactive Wizard Mode** - Guided setup with arrow-key navigation, no flags to remember
- **Automatic Migration** - Scans your solution/projects and generates `Directory.Packages.props`
- **Version Conflict Resolution** - Handles packages with different versions across projects
- **Package Analysis** - Scan for package issues without migrating (version inconsistencies, duplicates, redundant references)
- **Dry-Run Mode** - Preview changes before applying them
- **Rollback Support** - Undo migrations and restore original project files
- **Cyberpunk Terminal UI** - Stunning neon-styled interface with progress bars and ASCII art
- **Cross-Platform** - Works on Windows, macOS, and Linux
- **Backup Support** - Automatically backs up project files before modification

## Installation

### As a .NET Global Tool

```bash
dotnet tool install --global CPMigrate
```

### From Source

```bash
git clone https://github.com/georgepwall1991/CPMigrate.git
cd CPMigrate
dotnet build
```

## Testing Locally

To verify the tool works correctly on your machine before installing globally:

```bash
# Pack the tool
dotnet pack CPMigrate/CPMigrate.csproj -o ./nupkg

# Install to a local test path
dotnet tool install CPMigrate --tool-path ./test-tool --add-source ./nupkg

# Run the tool
./test-tool/cpmigrate --help

# Cleanup
rm -rf ./test-tool ./nupkg
```

## Usage

### Interactive Mode (Recommended for New Users)

Simply run `cpmigrate` with no arguments to start the interactive wizard:

```bash
cpmigrate
```

The wizard guides you through:
1. Choosing an operation (Migrate, Analyze, or Rollback)
2. Selecting your solution file
3. Configuring options with arrow keys
4. Reviewing settings before execution

You can also explicitly enter interactive mode:

```bash
cpmigrate --interactive
# or
cpmigrate -i
```

### Command-Line Usage

```bash
# Migrate current directory (looks for .sln file)
cpmigrate -s .

# Preview changes without modifying files
cpmigrate --dry-run

# Migrate a specific solution
cpmigrate -s /path/to/solution

# Migrate a specific project
cpmigrate -p /path/to/project.csproj
```

### Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--interactive` | `-i` | Run in interactive wizard mode | `false` |
| `--solution` | `-s` | Path to solution file or directory | `.` |
| `--project` | `-p` | Path to project file or directory | - |
| `--output-dir` | `-o` | Output directory for Directory.Packages.props | `.` |
| `--dry-run` | `-d` | Preview changes without modifying files | `false` |
| `--rollback` | `-r` | Restore project files from backup and remove Directory.Packages.props | `false` |
| `--analyze` | `-a` | Analyze packages for issues without modifying files | `false` |
| `--keep-attrs` | `-k` | Keep Version attributes in .csproj files | `false` |
| `--no-backup` | `-n` | Disable automatic backup | `false` |
| `--backup-dir` | - | Backup directory location | `.` |
| `--add-gitignore` | - | Add backup directory to .gitignore | `false` |
| `--conflict-strategy` | - | How to handle version conflicts: `Highest`, `Lowest`, or `Fail` | `Highest` |

### Examples

```bash
# Preview migration with dry-run
cpmigrate --dry-run

# Migrate and use lowest version for conflicts
cpmigrate --conflict-strategy Lowest

# Migrate without creating backups
cpmigrate --no-backup

# Migrate and add backup to .gitignore
cpmigrate --add-gitignore

# Rollback a migration (restore original project files)
cpmigrate --rollback

# Rollback with custom backup directory
cpmigrate --rollback --backup-dir ./my-backups

# Analyze packages for issues without migrating
cpmigrate --analyze

# Analyze a specific solution
cpmigrate --analyze -s /path/to/solution
```

## What is Central Package Management?

Central Package Management (CPM) is a NuGet feature that allows you to manage all package versions in a single `Directory.Packages.props` file at the root of your repository, rather than specifying versions in each `.csproj` file.

### Before (Traditional)

```xml
<!-- Project1.csproj -->
<PackageReference Include="Newtonsoft.Json" Version="13.0.1" />

<!-- Project2.csproj -->
<PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
```

### After (CPM)

```xml
<!-- Directory.Packages.props -->
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
  </ItemGroup>
</Project>

<!-- Project1.csproj -->
<PackageReference Include="Newtonsoft.Json" />

<!-- Project2.csproj -->
<PackageReference Include="Newtonsoft.Json" />
```

## Screenshots

### Migration Mode

![CPMigrate Demo](./docs/images/cpmigrate-demo.gif)

*Dry-run mode previewing changes with the cyberpunk-styled terminal UI.*

### Package Analysis

![CPMigrate Analyze](./docs/images/cpmigrate-analyze.gif)

*Analyze mode scanning for package issues without modifying files.*

### Terminal UI Features

The tool features a stunning cyberpunk-inspired terminal UI with:

- Neon ASCII art header with magenta/cyan color scheme
- Animated progress bars during project scanning
- Status indicators: `[▓▓▓]` success, `[!]` warning, `[X]` error
- Rich panel displays for file previews and summaries
- Simulation mode visual cues for dry-run operations

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

### Regenerating Documentation Media

To regenerate the GIFs and screenshots for the README:

```bash
# Prerequisites
brew install asciinema agg

# Generate all documentation media
./scripts/generate-docs-media.sh

# Options
./scripts/generate-docs-media.sh --skip-build    # Skip dotnet build
./scripts/generate-docs-media.sh --demo-only     # Only generate demo GIF
./scripts/generate-docs-media.sh --analyze-only  # Only generate analyze GIF
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

**George Wall**

- GitHub: [@georgepwall1991](https://github.com/georgepwall1991)

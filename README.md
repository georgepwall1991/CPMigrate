# CPMigrate

A stunning CLI tool to migrate .NET solutions to [Central Package Management (CPM)](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management).

![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/CPMigrate.svg)](https://www.nuget.org/packages/CPMigrate/)

![CPMigrate Interactive Wizard](./docs/images/cpmigrate-interactive.gif)

## Features

- **Mission Control Dashboard** - Intelligent repository pre-scan with risk assessment and situational awareness.
- **Zero-Typing Interaction** - Entirely selection-driven UI with a visual path browser for navigating your file system.
- **Smart Conflict Resolution** - Impact-aware version selection showing exactly how many projects use each version.
- **Live Verification Loop** - Automatic `dotnet restore` verification with autonomous recovery and rollback options.
- **Batch Migration** - Effortlessly migrate multiple solutions across a monorepo in one go.
- **Package Analysis & Auto-Fix** - Scan for issues like version divergence or redundant references and fix them instantly.
- **Cyberpunk Terminal UI** - Stunning neon-styled interface with progress tracking and mission status blueprints.
- **Safe by Default** - Automatic backups, Git health checks, and a comprehensive rollback system.

### New in v2.3: "Mission Control"

- **Intelligence-Driven Workflow**: The tool analyzes your environment on boot to suggest the best "Quick Actions".
- **Visual Path Browser**: Navigate and select solutions or projects using arrow keys—no more typing long paths.
- **Migration Risk Score**: Immediate feedback on the complexity of your migration (Low/Medium/High).
- **Impact Analysis**: View "Blast Radius" when resolving conflicts (e.g., `"1.2.3 (Used by 15 projects)"`).
- **Mission Progress Tracker**: Persistent visual blueprint of the migration stages.

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

## Usage

### Interactive Mode (Recommended)

Simply run `cpmigrate` with no arguments to enter **Mission Control**:

```bash
cpmigrate
```

The tool will immediately:
1. **Scan** your current directory for solutions and existing CPM setups.
2. **Dashboard** your repository state (Git health, backups, solutions).
3. **Assess Risk** based on version divergence across projects.
4. **Offer Quick Actions** like "Fast-Track Migration" or "Optimize Existing Setup".
5. **Guide** you through a selection-based path browser if manual setup is needed.

### Command-Line Usage

```bash
# Migrate current directory (looks for .sln file)
cpmigrate -s .

# Preview changes without modifying files
cpmigrate --dry-run

# Migrate all solutions in a directory recursively
cpmigrate --batch /path/to/repo

# Analyze packages for issues
cpmigrate --analyze --fix
```

### Options

#### Core Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--interactive` | `-i` | Run in interactive Mission Control mode | `false` |
| `--solution` | `-s` | Path to solution file or directory | `.` |
| `--project` | `-p` | Path to project file or directory | - |
| `--dry-run` | `-d` | Preview changes without modifying files | `false` |
| `--merge` | - | Merge into existing Directory.Packages.props if present | `false` |
| `--rollback` | `-r` | Restore project files from backup | `false` |
| `--analyze` | `-a` | Analyze packages for issues | `false` |
| `--fix` | - | Automatically apply fixes for detected issues | `false` |
| `--no-backup` | `-n` | Disable automatic backup | `false` |
| `--conflict-strategy` | - | Resolution: `Highest`, `Lowest`, `Fail` | `Highest` |
| `--interactive-conflicts` | - | Manually choose version for every conflict | `false` |

#### Output & CI/CD

| Option | Description | Default |
|--------|-------------|---------|
| `--output` | Output format: `Terminal` or `Json` | `Terminal` |
| `--output-file` | Write JSON output to file instead of stdout | - |
| `--quiet` | Suppress progress bars and spinners | `false` |

#### Batch Processing & Backups

| Option | Description | Default |
|--------|-------------|---------|
| `--batch` | Scan directory for .sln files and process each | - |
| `--batch-parallel` | Process solutions in parallel | `false` |
| `--prune-backups` | Delete old backups, keeping most recent | `false` |
| `--retention` | Number of backups to keep when pruning | `5` |

## Interactive Mission Control

When you start CPMigrate, you're presented with a high-density dashboard:

```text
 ┌───────────────── REPOSITORY CONTEXT ─────────────────┐
 │ Directory  /Users/dev/MyProject                      │
 │ Solutions  3 solution(s) detected                    │
 │ Using CPM  NO                                        │
 │ Git Status Clean                                     │
 │ Backups    2 backup set(s) available                 │
 └──────────────────────────────────────────────────────┘
 ┌───────────────────── ASSESSMENT ─────────────────────┐
 │ Migration Risk: MEDIUM                               │
 │ Impact Area:    12 projects                          │
 │ Assessment:     Minor version divergence detected.    │
 └──────────────────────────────────────────────────────┘

 What's the mission?
 🚀 Fast-Track Migration (Auto-resolve 5 conflicts)
 🛠  Migrate & Review Conflicts Individually
 📦 Batch migrate multiple solutions
 Exit
```

## What is Central Package Management?

Central Package Management (CPM) allows you to manage all package versions in a single `Directory.Packages.props` file. This eliminates "version drift" where different projects use different versions of the same library.

### Before (Traditional)

```xml
<!-- Project1.csproj --> <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
<!-- Project2.csproj --> <PackageReference Include="Newtonsoft.Json" Version="12.0.3" /> <!-- Conflict! -->
```

### After (CPM)

```xml
<!-- Directory.Packages.props -->
<ItemGroup>
  <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
</ItemGroup>

<!-- Project1.csproj & Project2.csproj -->
<PackageReference Include="Newtonsoft.Json" /> <!-- version is inherited -->
```

## Terminal UI Features

The stunning cyberpunk-inspired terminal UI includes:

- **Mission Status Tracker**: Real-time blueprint showing progress through `DISCOVERY -> ANALYSIS -> BACKUP -> MIGRATION -> VERIFICATION`.
- **Risk Gauge**: Visual color-coded assessment of migration complexity.
- **Impact-Aware Choice**: Selection menus that show project usage counts for package versions.
- **Animated Dashboards**: Framed panels and grids for professional information density.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

**George Wall**

- GitHub: [@georgepwall1991](https://github.com/georgepwall1991)

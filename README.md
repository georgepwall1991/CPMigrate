# CPMigrate: The Ultimate .NET Central Package Management (CPM) Migration Tool
<div align="center">
  <img src="./docs/images/logo.png" alt="CPMigrate Logo" width="128" />
  <br/>
  <img src="./docs/images/banner.png" alt="CPMigrate Banner" width="100%" />
</div>

<div align="center">

[![.NET](https://img.shields.io/badge/.NET-10.0+-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](https://opensource.org/licenses/MIT)
[![NuGet](https://img.shields.io/nuget/v/CPMigrate.svg?style=flat-square&logo=nuget)](https://www.nuget.org/packages/CPMigrate/)
[![Downloads](https://img.shields.io/nuget/dt/CPMigrate.svg?style=flat-square&color=blue)](https://www.nuget.org/packages/CPMigrate/)

</div>

![CPMigrate Interactive Wizard](./docs/images/cpmigrate-interactive.gif)

## 🚀 Why CPMigrate?

Stop wrestling with **dependency hell** and **version drift**. CPMigrate is the advanced **CLI tool** that instantly modernizes your **.NET solutions** to use **NuGet Central Package Management (CPM)**.

It doesn't just move XML around; it is a full-featured **repository health auditor** that:
*   **Analyzes** your entire dependency graph.
*   **Resolves** deep version conflicts automatically.
*   **Cleans** redundant `<PackageReference>` entries to reduce technical debt.
*   **Secures** your codebase by detecting high-severity vulnerabilities before they are locked in.

---

## ✨ Key Features & Capabilities

### 🛡️ **Intelligent Dependency Management**
*   **Transitive Conflict Resolution:** Automatically detects and resolves deep dependency conflicts that `dotnet restore` often misses.
*   **Dependency Lifting (Cleanup):** Identifies redundant explicit package references that are already transitively provided, keeping your `.csproj` files clean and minimal.
*   **Smart Versioning Strategies:** Choose between *Highest*, *Lowest*, or *Fail* strategies to handle version mismatches across your repository.

### 🔒 **Security-First Architecture**
*   **Automated Vulnerability Audits:** Runs integrated security scans (`dotnet list package --vulnerable`) to prevent locking in insecure packages.
*   **Secure Execution:** Strict path resolution prevents PATH injection attacks.
*   **Supply Chain Hardening:** CI/CD workflows are pinned to secure hashes to prevent upstream compromises.

### 🚀 **Modern Development Workflow**
*   **SLNX Support:** Fully compatible with the new Visual Studio 17.10+ `.slnx` solution format.
*   **Directory.Build.props Unification:** Automatically promotes repeated properties (Authors, TargetFramework, etc.) to a root-level configuration file, enforcing consistency across 100+ projects instantly.
*   **Self-Updating:** Includes a built-in update checker to ensure you're always using the latest version of the tool.
    *   `cpmigrate --update`

### 🎮 **Mission Control Dashboard**
An immersive, keyboard-driven Terminal User Interface (TUI) that provides:
*   **Real-time Risk Assessment:** Scans your repo and calculates a "Migration Risk" score.
*   **Dry-Run Previews:** Visualize massively destructive changes before they happen.
*   **Live Verification:** Automatically verifies build integrity after every migration step.

---

## 📦 Installation & Updating

### As a .NET Global Tool (Recommended)

Requires **.NET SDK 8.0** or later (supports .NET 10).

**Install:**
```bash
dotnet tool install --global CPMigrate
```

**Update:**
You can let the tool update itself:
```bash
cpmigrate --update
```
Or manually via .NET CLI:
```bash
dotnet tool update --global CPMigrate
```

> **Note:** NuGet indexing may take up to 15 minutes after a new release. Clear your HTTP cache if updates aren't found immediately:
> `dotnet nuget locals http-cache --clear`

### From Source

```bash
git clone https://github.com/georgepwall1991/CPMigrate.git
cd CPMigrate
dotnet build
```

---

## 🕹️ Usage Scenarios

### 1. The "Mission Control" (Interactive Mode)
Ideal for first-time users or complex repositories.

```bash
cpmigrate
```
*   Drives the entire process via a step-by-step wizard.
*   Offers rollbacks, backups, and detailed explanations.

### 2. CI/CD & Automation (Headless Mode)
Perfect for GitHub Actions, Azure DevOps, or Git hooks.

**Migrate a specific solution:**
```bash
cpmigrate -s ./MySolution.sln
```

**Analyze repository health (No changes):**
```bash
cpmigrate --analyze
```

**Auto-fix common issues (No migration):**
```bash
cpmigrate --analyze --fix
```

### 3. Repository Modernization
Refactor your entire codebase structure in one command.

**Unify generic properties to `Directory.Build.props`:**
```bash
cpmigrate --unify-props
```

**Batch migrate a monorepo with 50+ solutions:**
```bash
cpmigrate --batch /path/to/repo --batch-parallel
```


### Options Reference

| Option | Short | Description |
|--------|-------|-------------|
| `--interactive` | `-i` | Launch the Mission Control TUI (Default if no args). |
| `--solution` | `-s` | Path to `.sln` or `.slnx` file or directory. |
| `--unify-props` | - | Migrate common project properties to `Directory.Build.props`. |
| `--dry-run` | `-d` | Simulate operations without writing files. |
| `--analyze` | `-a` | Run health checks (duplicates, security, transitive). |
| `--fix` | - | Apply automatic fixes to discovered analysis issues. |
| `--rollback` | `-r` | Restore the last backup state. |
| `--prune-backups` | - | Clean up old backup files to save space. |
| `--output` | - | Output format: `Terminal` (default) or `Json` (for CI pipes). |

---

## 🖼️ Gallery

### Mission Control Dashboard
![CPMigrate Interactive](./docs/images/cpmigrate-interactive.gif)
*The state-driven dashboard assessing migration risk.*

### Risk Analysis & Dry Run
![CPMigrate Demo](./docs/images/cpmigrate-demo.gif)
*Previewing massive changes safely before committing.*

### Security & Package Analysis
![CPMigrate Analyze](./docs/images/cpmigrate-analyze.gif)
*Scanning for vulnerabilities and redundant dependencies.*

---

## 🤝 Contributing

Contributions are what make the open-source community such an amazing place to learn, inspire, and create. Any contributions you make are **greatly appreciated**.

1.  Fork the Project
2.  Create your Feature Branch (`git checkout -b feature/AmazingFeature`)
3.  Commit your Changes (`git commit -m 'Add some AmazingFeature'`)
4.  Push to the Branch (`git push origin feature/AmazingFeature`)
5.  Open a Pull Request

---

## 📄 License

Distributed under the MIT License. See `LICENSE` for more information.

## 👤 Author

**George Wall**
-   GitHub: [@georgepwall1991](https://github.com/georgepwall1991)
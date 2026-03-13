param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallerUrl,

    [Parameter(Mandatory = $true)]
    [string]$InstallerSha256,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$PackageIdentifier = "GeorgeWall.CPMigrate",
    [string]$Publisher = "George Wall",
    [string]$PackageName = "CPMigrate",
    [string]$DocsUrl = "https://georgepwall1991.github.io/CPMigrate/",
    [string]$PublisherUrl = "https://github.com/georgepwall1991",
    [string]$PublisherSupportUrl = "https://github.com/georgepwall1991/CPMigrate/issues",
    [string]$PackageUrl = "https://github.com/georgepwall1991/CPMigrate",
    [string]$LicenseUrl = "https://github.com/georgepwall1991/CPMigrate/blob/main/LICENSE",
    [string]$ReleaseNotesUrl = "",
    [string]$ReleaseDate = ""
)

$manifestVersion = "1.10.0"
$releaseNotes = if ([string]::IsNullOrWhiteSpace($ReleaseNotesUrl)) { "https://github.com/georgepwall1991/CPMigrate/releases/tag/v$Version" } else { $ReleaseNotesUrl }
$releaseDateValue = if ([string]::IsNullOrWhiteSpace($ReleaseDate)) { [DateTime]::UtcNow.ToString("yyyy-MM-dd") } else { $ReleaseDate }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$defaultLocalePath = Join-Path $OutputDirectory "$PackageIdentifier.locale.en-US.yaml"
$installerPath = Join-Path $OutputDirectory "$PackageIdentifier.installer.yaml"
$versionPath = Join-Path $OutputDirectory "$PackageIdentifier.yaml"

$defaultLocale = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.10.0.schema.json

PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
PackageLocale: en-US
Publisher: $Publisher
PublisherUrl: $PublisherUrl
PublisherSupportUrl: $PublisherSupportUrl
PackageName: $PackageName
PackageUrl: $PackageUrl
ShortDescription: Migrate .NET solutions to NuGet Central Package Management, analyze dependency health, and update packages safely with rollback.
Moniker: cpmigrate
License: MIT
LicenseUrl: $LicenseUrl
Tags:
- nuget
- central-package-management
- directory-packages-props
- dotnet
- dependency-analysis
- package-updates
- rollback
ReleaseNotesUrl: $releaseNotes
Documentations:
- DocumentLabel: Docs Hub
  DocumentUrl: $DocsUrl
ManifestType: defaultLocale
ManifestVersion: $manifestVersion
"@

$installer = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.10.0.schema.json

PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
InstallerType: zip
NestedInstallerType: portable
NestedInstallerFiles:
- RelativeFilePath: CPMigrate.exe
  PortableCommandAlias: cpmigrate
ReleaseDate: $releaseDateValue
Installers:
- Architecture: x64
  InstallerUrl: $InstallerUrl
  InstallerSha256: $InstallerSha256
Commands:
- cpmigrate
ManifestType: installer
ManifestVersion: $manifestVersion
"@

$versionManifest = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.10.0.schema.json

PackageIdentifier: $PackageIdentifier
PackageVersion: $Version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: $manifestVersion
"@

Set-Content -Path $defaultLocalePath -Value $defaultLocale -Encoding UTF8NoBOM
Set-Content -Path $installerPath -Value $installer -Encoding UTF8NoBOM
Set-Content -Path $versionPath -Value $versionManifest -Encoding UTF8NoBOM

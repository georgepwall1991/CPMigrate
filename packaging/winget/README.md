# Winget Packaging

CPMigrate publishes Windows packaging metadata for the `GeorgeWall.CPMigrate` package identifier.

Install after the Winget package is indexed:

```powershell
winget install GeorgeWall.CPMigrate
```

Maintenance flow:

1. Publish the `CPMigrate-portable-win-x64.zip` asset from the release workflow.
2. Generate manifests with `scripts/release/generate-winget-manifests.ps1`.
3. Validate locally or in CI with `winget install --manifest`.
4. Submit the manifest set to `microsoft/winget-pkgs`.

The repository keeps the current manifest payload under `packaging/winget/manifests/` so release submissions stay reviewable.

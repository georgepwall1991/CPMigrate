# Winget Packaging

CPMigrate is not currently indexed in Microsoft Winget. The `GeorgeWall.CPMigrate` command below is a planned install path, not a claim that the package is available today.

```powershell
winget install GeorgeWall.CPMigrate
```

Maintenance flow:

1. Publish the `CPMigrate-portable-win-x64.zip` asset from the release workflow.
2. Generate manifests with `scripts/release/generate-winget-manifests.ps1`.
3. Validate locally or in CI with `winget install --manifest`.
4. Submit the manifest set to `microsoft/winget-pkgs` and wait for indexing.

The release workflow attaches a versioned manifest bundle to each GitHub Release. Keep only a manifest generated from that release asset in a Winget submission; do not infer availability until Microsoft indexes it.

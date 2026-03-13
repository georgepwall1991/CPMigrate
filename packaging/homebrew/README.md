# Homebrew Packaging

CPMigrate ships a Homebrew formula via the `georgepwall1991/homebrew-cpmigrate` tap.

Install:

```bash
brew tap georgepwall1991/cpmigrate
brew install cpmigrate
```

Maintenance flow:

1. Release a tagged GitHub release with the `.nupkg` asset.
2. Generate a versioned formula with `scripts/release/generate-homebrew-formula.sh`.
3. Push the generated formula into the tap repository.
4. Smoke test with `brew install` and `cpmigrate --help`.

The release workflow can update the tap automatically when `HOMEBREW_TAP_TOKEN` is configured.

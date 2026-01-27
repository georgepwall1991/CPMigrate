# SonarCloud Configuration Guide

## Overview

This project uses SonarCloud for code quality analysis and coverage tracking. The configuration has been optimized to:

✅ **Track test coverage** - Measure how much production code is tested
✅ **Exclude test code** - Avoid noisy quality checks on test files
✅ **Focus on production code quality** - Only production code affects quality gates

## Configuration Files

### `sonar-project.properties`
Central configuration file that defines:
- Project metadata (name, key, organization)
- Source and test directories
- Coverage report paths
- **Rule exclusions for test files** (10 common test code patterns)
- Quality thresholds for production code

### `.github/workflows/ci.yml`
CI workflow that:
- Runs tests with coverage collection
- Uploads results to SonarCloud
- Uses `sonar-project.properties` for configuration

## What Gets Checked

### ✅ Production Code (`CPMigrate/`)
- Full quality gate enforcement
- Cognitive complexity limit: 30
- All SonarCloud rules apply
- Must maintain quality standards

### ⚠️ Test Code (`CPMigrate.Tests/`)
- **Coverage is tracked** (shows which production code is tested)
- **Quality rules are relaxed** for:
  - Cognitive complexity
  - Magic numbers
  - Code duplication
  - Method length
  - Number of assertions
  - Hardcoded test data
  - And 4 other test-specific patterns

## Why Exclude Test Code Rules?

Test code has different quality standards:

| Rule | Why It's Noisy for Tests |
|------|--------------------------|
| **Cognitive Complexity** | Test methods naturally have setup → act → assert → cleanup |
| **Magic Numbers** | Test data (e.g., `Assert.Equal(42, result)`) is self-documenting |
| **Code Duplication** | Common test setup is often duplicated for clarity |
| **Method Length** | Integration tests cover complete scenarios |
| **Too Many Assertions** | Complex behavior needs multiple checks |
| **Hardcoded Credentials** | Test credentials like `"test@example.com"` are acceptable |

## Running Locally

To run SonarCloud analysis locally:

```bash
# 1. Install scanner
dotnet tool install --global dotnet-sonarscanner

# 2. Set token (get from https://sonarcloud.io/account/security)
export SONAR_TOKEN="your_token_here"

# 3. Begin analysis (uses sonar-project.properties)
dotnet sonarscanner begin \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.host.url="https://sonarcloud.io"

# 4. Build
dotnet build --configuration Release

# 5. Test with coverage
dotnet test \
  --configuration Release \
  --no-build \
  --logger "trx" \
  --collect:"XPlat Code Coverage" \
  -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=opencover

# 6. End analysis
dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
```

## Viewing Results

- **SonarCloud Dashboard**: https://sonarcloud.io/project/overview?id=georgepwall1991_CPMigrate
- **Coverage**: Shows production code coverage from tests
- **Quality Gate**: Only production code issues affect the gate
- **Issues**: Test code issues are filtered out

## Modifying Configuration

### To adjust thresholds:
Edit `sonar-project.properties`:
```properties
# Example: Increase cognitive complexity limit
sonar.cs.S3776.maxComplexity=40
```

### To exclude additional test rules:
Add to the `sonar.issue.ignore.multicriteria` section:
```properties
sonar.issue.ignore.multicriteria=t1,t2,...,t11  # Add t11
sonar.issue.ignore.multicriteria.t11.ruleKey=csharpsquid:SXXXX
sonar.issue.ignore.multicriteria.t11.resourceKey=**/*Tests.cs
```

### To exclude files from coverage:
```properties
sonar.coverage.exclusions=**/*.Tests/**,**/Program.cs,**/YourFile.cs
```

## CI Integration

The GitHub Actions workflow (`.github/workflows/ci.yml`) automatically:
1. Runs on push to `main` or pull requests
2. Collects coverage during test execution
3. Uploads results to SonarCloud
4. Uses settings from `sonar-project.properties`

No manual intervention needed!

## Benefits of This Configuration

✅ **Clean Quality Gates** - Only production code issues fail builds
✅ **Accurate Coverage** - See what production code lacks tests
✅ **Less Noise** - No complaints about test code patterns
✅ **Better Signal** - Focus on real production code issues
✅ **Consistent** - Same rules locally and in CI

## Current Coverage Status

**Production Code Coverage: 43.23%** (as of latest run)
- Baseline: 26.85%
- Improvement: +16.38 percentage points
- Goal: 80%+

## Questions?

- **"Why is my test file showing issues?"** - Some rules still apply (like actual bugs). Only quality patterns are excluded.
- **"Can I see test code quality?"** - Yes, but it won't affect quality gates. View issues in SonarCloud filtered by test files.
- **"How do I change the coverage goal?"** - Edit the quality gate in SonarCloud project settings.

---

**Last Updated**: January 2026
**Configuration Version**: 1.0

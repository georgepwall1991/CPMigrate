using CPMigrate.Models;
using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services.Verify;

/// <summary>
/// Reading the resolved graph out of <c>project.assets.json</c>.
///
/// The fixtures deliberately use the shapes NuGet actually writes, for the reason recorded in
/// <see cref="DependencyGraphRealAssetsTests"/>: <c>project.frameworks.&lt;tf&gt;.dependencies</c> holds a
/// version *range* while <c>targets</c> is keyed by the *resolved* version. A reader built against the
/// bare-version shape matches nothing on any real project and reports an empty graph — which, for a
/// feature whose whole job is to compare two graphs, would report every migration as a clean no-op.
/// </summary>
public class ResolvedGraphSnapshotTests : IDisposable
{
    private readonly string _testDir;

    public ResolvedGraphSnapshotTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ResolvedGraph_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_testDir, "obj"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadsTheResolvedVersionOfEveryPackage()
    {
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog.Sinks.File/7.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "4.3.0" }
                  },
                  "Serilog/4.3.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        var framework = Read()!.Frameworks.Single();

        framework.TargetFramework.Should().Be("net10.0");
        framework.Resolved.Should().BeTrue();
        framework
            .Packages.Select(p => (p.PackageId, p.Version))
            .Should()
            .BeEquivalentTo([("Serilog.Sinks.File", "7.0.0"), ("Serilog", "4.3.0")]);
    }

    [Fact]
    public void MarksWhichPackagesTheProjectReferencesDirectly()
    {
        // The distinction is what lets a report say "you changed Serilog, and that dragged
        // System.Text.Json with it" rather than listing thirty equally-weighted rows.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog.Sinks.File/7.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "4.3.0" }
                  },
                  "Serilog/4.3.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        var packages = Read()!.Frameworks.Single().Packages;

        packages.Single(p => p.PackageId == "Serilog.Sinks.File").IsDirect.Should().BeTrue();
        packages.Single(p => p.PackageId == "Serilog").IsDirect.Should().BeFalse();
    }

    [Fact]
    public void ExcludesProjectReferences_WhichAreNotPackagesAtAll()
    {
        // A ProjectReference appears in targets with "type": "project" and a version that is the
        // referenced project's, not a package version. Counting one as a package would make every
        // version bump of a sibling project read as dependency drift.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog/4.3.0": { "type": "package" },
                  "Shared/1.0.0": { "type": "project" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Shared": { "target": "Project" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Read()!.Frameworks.Single().Packages.Select(p => p.PackageId).Should().Equal("Serilog");
    }

    [Fact]
    public void ReadsEveryTargetFrameworkSeparately()
    {
        // Multi-targeting resolves independently per framework, so a package can move under one and
        // stay put under another. Collapsing them would hide exactly that.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": { "Serilog/4.3.0": { "type": "package" } },
                "netstandard2.0": { "Serilog/2.12.0": { "type": "package" } }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  },
                  "netstandard2.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[2.12.0, )" } }
                  }
                }
              }
            }
            """
        );

        var graph = Read()!;

        graph
            .Frameworks.Select(f => (f.TargetFramework, f.Packages.Single().Version))
            .Should()
            .BeEquivalentTo([("net10.0", "4.3.0"), ("netstandard2.0", "2.12.0")]);
    }

    [Fact]
    public void RecordsAFrameworkAsUnresolved_WhenTargetsDoesNotDescribeIt()
    {
        // A framework the project declares but that restore did not write is not "a framework with no
        // packages". Treating it as empty is the failure this whole feature exists to catch: a
        // framework that stops resolving between the two snapshots would read as "every package
        // removed" or, worse, as nothing at all.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": { "Serilog/4.3.0": { "type": "package" } }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  },
                  "netstandard2.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[2.12.0, )" } }
                  }
                }
              }
            }
            """
        );

        var unresolved = Read()!.Frameworks.Single(f => f.TargetFramework == "netstandard2.0");

        unresolved.Resolved.Should().BeFalse();
        unresolved.Packages.Should().BeEmpty();
    }

    [Theory]
    [InlineData("4.3", "4.3.0", "a two-part version is the same release as its three-part form")]
    [InlineData("4.3.0.0", "4.3.0", "a trailing zero revision is not a different release")]
    [InlineData("4.3.0+build.5", "4.3.0", "build metadata does not change which package restores")]
    public void NormalizesVersions_SoAnEquivalentFormIsNotReportedAsDrift(
        string written,
        string expected,
        string why
    )
    {
        // Two restores of the same tree can spell the same version differently. Comparing the raw
        // strings would manufacture drift that nothing actually caused, and a report that cries wolf
        // is one nobody reads.
        WriteAssets(
            $$"""
            {
              "version": 3,
              "targets": {
                "net10.0": { "Serilog/{{written}}": { "type": "package" } }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  }
                }
              }
            }
            """
        );

        Read()!.Frameworks.Single().Packages.Single().Version.Should().Be(expected, why);
    }

    [Fact]
    public void MatchesPackageIdsCaseInsensitively()
    {
        // NuGet package IDs are case-insensitive and assets files are not consistent about casing
        // between the dependency list and the target keys.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": { "serilog/4.3.0": { "type": "package" } }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  }
                }
              }
            }
            """
        );

        Read()!.Frameworks.Single().Packages.Single().IsDirect.Should().BeTrue();
    }

    [Fact]
    public void RecordsTheEdgesBetweenPackages()
    {
        // Carried so a report can prove "System.Text.Json moved because you moved Serilog" from the
        // graph rather than inferring it from proximity. Without the edges that claim is a guess, and
        // a guess that is usually right is the kind that gets believed when it is wrong.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog.Sinks.File/7.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "4.3.0", "System.Text.Json": "9.0.0" }
                  },
                  "Serilog/4.3.0": { "type": "package" },
                  "System.Text.Json/9.0.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        var packages = Read()!.Frameworks.Single().Packages;

        packages
            .Single(p => p.PackageId == "Serilog.Sinks.File")
            .Dependencies.Should()
            .BeEquivalentTo(["Serilog", "System.Text.Json"]);
        packages.Single(p => p.PackageId == "Serilog").Dependencies.Should().BeEmpty();
    }

    [Fact]
    public void ReturnsNothing_WhenTheAssetsFileDescribesADifferentProject()
    {
        // An assets file records the project it restored. Asking is the only way to tell a current
        // graph from a merely plausible one — a project that redirects its intermediate output leaves
        // a stale obj/project.assets.json here from before the redirect, which reads perfectly, is
        // compared against itself in both snapshots, and reports unchanged over a migration that
        // changed the build. Cross-review caught it: failing closed on the file being *absent* is not
        // the same as failing closed on it being *wrong*.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": { "net10.0": { "Serilog/4.3.0": { "type": "package" } } },
              "project": {
                "restore": { "projectPath": "/somewhere/else/Other.csproj" },
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  }
                }
              }
            }
            """
        );

        Read().Should().BeNull();
    }

    [Fact]
    public void ReadsAnAssetsFileThatDescribesThisProject()
    {
        // The other half, so the check cannot become "reject everything".
        var projectPath = Path.Combine(_testDir, "Project.csproj");

        WriteAssets(
            $$"""
            {
              "version": 3,
              "targets": { "net10.0": { "Serilog/4.3.0": { "type": "package" } } },
              "project": {
                "restore": { "projectPath": {{System.Text.Json.JsonSerializer.Serialize(
                projectPath
            )}} },
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  }
                }
              }
            }
            """
        );

        Read()!.Frameworks.Single().Packages.Single().PackageId.Should().Be("Serilog");
    }

    [Fact]
    public void ReadsAnAssetsFileThatDoesNotSayWhatItDescribes()
    {
        // Older NuGet versions omit the field. Refusing every project on those would make
        // verification unavailable rather than careful.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": { "net10.0": { "Serilog/4.3.0": { "type": "package" } } },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": { "Serilog": { "target": "Package", "version": "[4.3.0, )" } }
                  }
                }
              }
            }
            """
        );

        Read().Should().NotBeNull();
    }

    [Fact]
    public void ReturnsNothing_WhenTheAssetsFileIsMissing()
    {
        // Not an empty graph. An unrestored project has to be distinguishable from one that restored
        // to nothing, because the caller turns the first into a failed run.
        Read().Should().BeNull();
    }

    [Fact]
    public void ReturnsNothing_WhenTheAssetsFileLacksTheSectionsItReads()
    {
        // Restore can leave a truncated file behind. Reading it as an empty graph would report every
        // package in the project as removed.
        WriteAssets("""{ "version": 3 }""");

        Read().Should().BeNull();
    }

    [Fact]
    public void ReturnsNothing_WhenTheAssetsFileIsNotValidJson()
    {
        WriteAssets("{ this is not json");

        Read().Should().BeNull();
    }

    private void WriteAssets(string json)
    {
        File.WriteAllText(Path.Combine(_testDir, "obj", "project.assets.json"), json);
    }

    private ProjectResolvedGraph? Read()
    {
        return new DependencyGraphService(new FakeConsoleService()).TryReadResolvedGraph(
            Path.Combine(_testDir, "Project.csproj")
        );
    }
}

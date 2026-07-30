using CPMigrate.Services;
using CPMigrate.Tests.TestDoubles;
using FluentAssertions;

namespace CPMigrate.Tests.Services;

/// <summary>
/// These fixtures use the shapes a real <c>project.assets.json</c> actually contains, which turns out to
/// be the whole point.
///
/// The existing tests declare direct dependencies as <c>"version": "1.0.0"</c>. NuGet never writes that:
/// <c>project.frameworks.&lt;tf&gt;.dependencies</c> holds a version *range* — <c>"[1.0.0, )"</c> — while
/// the <c>targets</c> section is keyed by the *resolved* version, <c>"Package/1.0.0"</c>. Building a
/// lookup key from the range therefore matched nothing on any real project, the transitive closure came
/// back empty, and the analyzer reported no findings while appearing to succeed. The tests passed because
/// their fixtures could not occur.
///
/// Verified against this repository's own <c>CPMigrate/obj/project.assets.json</c>, where the framework
/// dependency reads <c>"[8.0.0, )"</c> and the target key is <c>Buildalyzer/8.0.0</c>.
/// </summary>
public class DependencyGraphRealAssetsTests : IDisposable
{
    private readonly string _testDir;

    public DependencyGraphRealAssetsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"DependencyGraphReal_{Guid.NewGuid():N}");
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
    public void FindsARedundantReference_WhenVersionsAreRangesAsNuGetWritesThem()
    {
        // The case the analyzer exists for, expressed the way NuGet expresses it.
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
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Contain("Serilog");
    }

    [Fact]
    public void DoesNotReportAReference_ThatPinsHigherThanAnythingElseRequires()
    {
        // Cross-review caught this, and it is the case my own end-to-end check had produced: a real
        // Serilog.Sinks.File 7.0.0 requires Serilog 4.2.0, while the project asks for 4.3.0 directly.
        // Restore settled on 4.3.0 *because of* that direct reference, so Serilog is reachable — and
        // reachability alone calls the reference redundant. Removing it silently downgrades Serilog to
        // 4.2.0. The finding would read as a tidy-up and land as a regression, which is worse than the
        // missing finding this release set out to fix.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog.Sinks.File/7.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "4.2.0" }
                  },
                  "Serilog/4.3.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant()
            .Should()
            .BeEmpty("removing the direct reference would downgrade Serilog from 4.3.0 to 4.2.0");
    }

    [Fact]
    public void ReportsAReference_WhenTheTransitiveRequirementIsHigherThanThePin()
    {
        // The other direction: another package needs Serilog 5.0.0, so the direct 4.3.0 reference
        // constrains nothing and removing it changes nothing. "Same or higher" is the documented contract.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Serilog.Sinks.File/7.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "5.0.0" }
                  },
                  "Serilog/5.0.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Contain("Serilog");
    }

    [Fact]
    public void TakesTheHighestRequirement_WhenSeveralPackagesRequireTheSameDependency()
    {
        // One package asking for less must not veto a finding another package's higher requirement
        // already guarantees.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Old.Consumer/1.0.0": {
                    "type": "package",
                    "dependencies": { "Serilog": "4.0.0" }
                  },
                  "New.Consumer/2.0.0": {
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
                      "Old.Consumer": { "target": "Package", "version": "[1.0.0, )" },
                      "New.Consumer": { "target": "Package", "version": "[2.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Contain("Serilog");
    }

    [Fact]
    public void DoesNotReportAReference_ThatIsOnlyRedundantUnderOneOfSeveralFrameworks()
    {
        // Cross-review caught this too: unioning per-framework findings advises removing a reference that
        // is transitive under net10.0 but independently required under netstandard2.0 — and the advice
        // looks exactly as confident as a correct one. Removing it breaks the framework where it was not
        // transitive.
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
                },
                "netstandard2.0": {
                  "Legacy.Helper/1.0.0": { "type": "package" },
                  "Serilog/4.3.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  },
                  "netstandard2.0": {
                    "dependencies": {
                      "Legacy.Helper": { "target": "Package", "version": "[1.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant()
            .Should()
            .BeEmpty(
                "Serilog is directly required under netstandard2.0, where nothing provides it"
            );
    }

    [Fact]
    public void ReportsAReference_RedundantUnderEveryFrameworkThatDeclaresIt()
    {
        // Multi-targeting must not block a finding that holds everywhere.
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
                },
                "netstandard2.0": {
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
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  },
                  "netstandard2.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Equal("Serilog");
    }

    [Fact]
    public void DoesNotReportAReference_DeclaredByAFrameworkThatCannotBeJudged()
    {
        // A framework the project declares but that is missing from targets cannot be evaluated. Reporting
        // anyway would be a guess, and the cost of a wrong guess here is a broken restore while the cost of
        // silence is one missed finding.
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
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  },
                  "netstandard2.0": {
                    "dependencies": {
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().BeEmpty();
    }

    [Fact]
    public void FindsNothing_WhenNoDirectReferenceIsProvidedTransitively()
    {
        // The negative case, so the fix cannot be "report everything".
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
                  "Serilog/4.3.0": { "type": "package" },
                  "Moq/4.20.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Moq": { "target": "Package", "version": "[4.20.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().BeEmpty();
    }

    [Theory]
    [InlineData("[7.0.0, )", "the ordinary floor-only range NuGet writes for a plain reference")]
    [InlineData("7.0.0", "a bare version, which some hand-written and older assets files carry")]
    [InlineData("[7.0.0]", "an exact pin")]
    [InlineData("[7.0.0, 8.0.0)", "a bounded range")]
    [InlineData("(7.0.0, )", "an exclusive floor")]
    public void ResolvesTheDependency_WhateverFormTheRangeTakes(string range, string why)
    {
        // Each of these appears in real assets files depending on how the reference was written. A lookup
        // built from the literal string only ever worked for the one form that does not occur in practice.
        WriteAssets(
            $$"""
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
                      "Serilog.Sinks.File": { "target": "Package", "version": "{{range}}" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Contain("Serilog", why);
    }

    [Fact]
    public void FollowsAChainSeveralLevelsDeep()
    {
        // Redundancy is not only one hop away: a package pulled in by something pulled in by a direct
        // reference is just as redundant, and the recursion has to survive resolving each level's version.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Top/1.0.0": { "type": "package", "dependencies": { "Middle": "2.0.0" } },
                  "Middle/2.0.0": { "type": "package", "dependencies": { "Leaf": "3.0.0" } },
                  "Leaf/3.0.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Top": { "target": "Package", "version": "[1.0.0, )" },
                      "Leaf": { "target": "Package", "version": "[3.0.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Contain("Leaf");
    }

    [Fact]
    public void IsCaseInsensitiveAboutPackageIds()
    {
        // NuGet treats IDs case-insensitively and assets files are not consistent about casing between
        // the dependency list and the target keys.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "serilog.sinks.file/7.0.0": {
                    "type": "package",
                    "dependencies": { "SERILOG": "4.3.0" }
                  },
                  "SeriLog/4.3.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog.Sinks.File": { "target": "Package", "version": "[7.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().Contain("Serilog");
    }

    [Fact]
    public void IgnoresAProjectReference_WhichIsNotAPackageAtAll()
    {
        // project.frameworks.dependencies also lists ProjectReference entries, which have no version range
        // and no entry in targets under a package key. Treating one as a package is how a missing lookup
        // turns into a wrong answer rather than no answer.
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

        var act = () => Redundant();

        act.Should().NotThrow();
        Redundant().Should().BeEmpty();
    }

    [Fact]
    public void SurvivesAFrameworkPresentInTheProjectButNotInTargets()
    {
        // A partially written or hand-edited assets file. Reported as nothing found rather than throwing,
        // because a crash here would fail the whole analysis over one unreadable input.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {},
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        Redundant().Should().BeEmpty();
    }

    [Fact]
    public void SurvivesAnAssetsFileMissingTheSectionsItReads()
    {
        // Restore can leave a truncated file behind, and the analyzer must not take the run down with it.
        WriteAssets("""{ "version": 3 }""");
        var console = new FakeConsoleService();

        var redundant = new DependencyGraphService(console).IdentifyRedundantDirectReferences(
            Path.Combine(_testDir, "Project.csproj")
        );

        redundant.Should().BeEmpty();
    }

    [Fact]
    public void ToleratesACycleInTheGraph()
    {
        // Malformed or mutually-referencing packages must not spin forever.
        WriteAssets(
            """
            {
              "version": 3,
              "targets": {
                "net10.0": {
                  "Left/1.0.0": { "type": "package", "dependencies": { "Right": "1.0.0" } },
                  "Right/1.0.0": { "type": "package", "dependencies": { "Left": "1.0.0" } },
                  "Serilog/4.3.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net10.0": {
                    "dependencies": {
                      "Left": { "target": "Package", "version": "[1.0.0, )" },
                      "Serilog": { "target": "Package", "version": "[4.3.0, )" }
                    }
                  }
                }
              }
            }
            """
        );

        var act = () => Redundant();

        act.Should().NotThrow();
    }

    private void WriteAssets(string json)
    {
        File.WriteAllText(Path.Combine(_testDir, "obj", "project.assets.json"), json);
    }

    private List<string> Redundant()
    {
        return new DependencyGraphService(
            new FakeConsoleService()
        ).IdentifyRedundantDirectReferences(Path.Combine(_testDir, "Project.csproj"));
    }
}

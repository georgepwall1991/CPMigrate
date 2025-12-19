using CPMigrate.Models;
using CPMigrate.Services;
using FluentAssertions;
using Xunit;

namespace CPMigrate.Tests.Services;

public class ConfigServiceTests
{
    [Fact]
    public void MergeConfig_MergeExistingFromConfig_WhenCliNotProvided()
    {
        var options = new Options { MergeExisting = false };
        var config = new ConfigModel { MergeExisting = true };
        var service = new ConfigService();

        service.MergeConfig(options, config, new HashSet<string>());

        options.MergeExisting.Should().BeTrue();
    }

    [Fact]
    public void MergeConfig_MergeExistingDoesNotOverrideCliProvidedValue()
    {
        var options = new Options { MergeExisting = false };
        var config = new ConfigModel { MergeExisting = true };
        var service = new ConfigService();

        service.MergeConfig(options, config, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "merge" });

        options.MergeExisting.Should().BeFalse();
    }
}

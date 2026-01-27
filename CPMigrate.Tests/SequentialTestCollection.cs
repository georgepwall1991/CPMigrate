using Xunit;

namespace CPMigrate.Tests;

/// <summary>
/// Collection definition for tests that must run sequentially due to Spectre.Console threading limitations.
/// Tests using Status or Progress displays cannot run concurrently.
/// </summary>
[CollectionDefinition("Sequential", DisableParallelization = true)]
public class SequentialTestCollection
{
}

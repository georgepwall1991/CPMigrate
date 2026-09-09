using CPMigrate.Fixers;
using FluentAssertions;

namespace CPMigrate.Tests.Fixers;

/// <summary>
/// <see cref="FixWriteException"/> is the line between "nothing to change" and "could not change
/// it": its message is what a user reads over a locked or read-only file, so the message must
/// carry the cause and name the file without dumping a full path into the output.
/// </summary>
public class FixWriteExceptionTests
{
    [Fact]
    public void Message_NamesTheFileWithoutItsDirectoryAndCarriesTheCause()
    {
        var ex = new FixWriteException(
            Path.Combine("some", "dir", "App.csproj"),
            new UnauthorizedAccessException("Access to the path is denied."));

        ex.Message.Should().Be("Could not modify App.csproj: Access to the path is denied.");
    }

    [Fact]
    public void ProjectPath_KeepsTheFullPath()
    {
        var path = Path.Combine("some", "dir", "App.csproj");

        new FixWriteException(path, new IOException("locked")).ProjectPath.Should().Be(path);
    }

    [Fact]
    public void InnerException_IsPreserved()
    {
        var inner = new IOException("locked");

        new FixWriteException("App.csproj", inner).InnerException.Should().BeSameAs(inner);
    }
}

using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class CodexPathResolverTests
{
    [Fact]
    public void ResolveCodexHome_UsesConfiguredEnvironmentVariable()
    {
        var previous = Environment.GetEnvironmentVariable("CODEX_HOME");
        var expected = Path.Combine(Path.GetTempPath(), "custom-codex-home");
        try
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", expected);

            Assert.Equal(Path.GetFullPath(expected), CodexPathResolver.ResolveCodexHome());
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", previous);
        }
    }
}

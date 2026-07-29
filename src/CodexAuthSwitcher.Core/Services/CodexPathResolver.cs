namespace CodexAuthSwitcher.Core.Services;

public static class CodexPathResolver
{
    private const string CodexHomeVariableName = "CODEX_HOME";

    public static string ResolveCodexHome()
    {
        var configuredHome = Environment.GetEnvironmentVariable(CodexHomeVariableName);
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredHome.Trim()));
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}

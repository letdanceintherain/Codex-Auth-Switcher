namespace CodexAuthSwitcher.Core.Models;

public sealed class CodexEnvironment
{
    public string CodexHomePath { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public string AuthPath { get; init; } = string.Empty;
    public string CurrentAuthMode { get; init; } = "unknown";
    public string ModelProvider { get; init; } = "(default)";
    public string Model { get; init; } = "(default)";
    public LiveAuthIdentity? LiveIdentity { get; init; }
    public CurrentProfileState? CurrentProfile { get; init; }
}

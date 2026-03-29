namespace CodexAuthSwitcher.Core.Models;

public sealed class DesktopAppState
{
    public bool IsRunning { get; init; }
    public string? DesktopAppPath { get; init; }
    public IReadOnlyList<int> ProcessIds { get; init; } = Array.Empty<int>();
}

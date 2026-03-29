namespace CodexAuthSwitcher.Core.Models;

public sealed class SwitchResult
{
    public string AppliedProfileName { get; init; } = string.Empty;
    public ProfileKind ProfileKind { get; init; }
    public string BackupPath { get; init; } = string.Empty;
    public bool RestartRequired { get; init; }
}

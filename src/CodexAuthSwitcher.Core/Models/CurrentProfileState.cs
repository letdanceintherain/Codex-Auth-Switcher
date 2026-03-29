namespace CodexAuthSwitcher.Core.Models;

public sealed class CurrentProfileState
{
    public string ProfileName { get; init; } = string.Empty;
    public ProfileKind Kind { get; init; }
    public string? IdentityFingerprint { get; init; }
    public string? Email { get; init; }
    public string? AccountId { get; init; }
    public string? Provider { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiHost { get; init; }
    public DateTimeOffset SwitchedAt { get; init; }
}

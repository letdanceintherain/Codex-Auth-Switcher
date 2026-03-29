namespace CodexAuthSwitcher.Core.Models;

public sealed class ProfileSummary
{
    public string Name { get; init; } = string.Empty;
    public ProfileKind Kind { get; init; }
    public string? Email { get; init; }
    public string? AccountId { get; init; }
    public string? Provider { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiHost { get; init; }
    public string? Model { get; init; }
    public string? ApiKeyMask { get; init; }
    public string? IdentityFingerprint { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public bool IsAutoCaptured { get; init; }
    public bool IsLegacyPlaintextApiProfile { get; init; }
}

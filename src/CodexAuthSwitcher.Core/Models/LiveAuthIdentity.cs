namespace CodexAuthSwitcher.Core.Models;

public sealed class LiveAuthIdentity
{
    public ProfileKind Kind { get; init; }
    public string AuthMode { get; init; } = "unknown";
    public string IdentityFingerprint { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? AccountId { get; init; }
    public string? Provider { get; init; }
    public string? BaseUrl { get; init; }
    public string? ApiHost { get; init; }
    public string? Model { get; init; }
    public string? ApiKeyMask { get; init; }
}

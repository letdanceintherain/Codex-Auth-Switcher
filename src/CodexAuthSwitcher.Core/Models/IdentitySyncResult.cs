namespace CodexAuthSwitcher.Core.Models;

public sealed class IdentitySyncResult
{
    public LiveAuthIdentity? LiveIdentity { get; init; }
    public string? MatchedProfileName { get; init; }
    public ProfileKind? MatchedProfileKind { get; init; }
    public bool CreatedProfile { get; init; }
    public bool CurrentProfileUpdated { get; init; }
    public bool IsManaged { get; init; }
}

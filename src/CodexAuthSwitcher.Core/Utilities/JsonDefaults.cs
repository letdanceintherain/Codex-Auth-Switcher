using System.Text.Json;

namespace CodexAuthSwitcher.Core.Utilities;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

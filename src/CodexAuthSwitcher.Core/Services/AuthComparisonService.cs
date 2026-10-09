using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAuthSwitcher.Core.Services;

// Compare the credentials Codex will use, rather than incidental auth.json fields.
// A trusted local account ID allows refreshed ChatGPT tokens to remain authoritative.
public static class AuthComparisonService
{
    public static bool Equivalent(string currentAuth, string targetAuth)
    {
        try
        {
            using var current = JsonDocument.Parse(currentAuth);
            using var target = JsonDocument.Parse(targetAuth);
            var currentMode = ReadEffectiveMode(current.RootElement);
            var targetMode = ReadEffectiveMode(target.RootElement);
            if (currentMode is null || targetMode is null || currentMode != targetMode) return false;

            if (currentMode == "apikey")
            {
                var currentKey = ReadString(current.RootElement, "OPENAI_API_KEY");
                var targetKey = ReadString(target.RootElement, "OPENAI_API_KEY");
                return !string.IsNullOrWhiteSpace(currentKey)
                    && string.Equals(currentKey, targetKey, StringComparison.Ordinal);
            }

            var currentAccount = ReadUsableChatGptAccountId(current.RootElement);
            var targetAccount = ReadUsableChatGptAccountId(target.RootElement);
            if (currentAccount is not null && targetAccount is not null)
                return string.Equals(currentAccount, targetAccount, StringComparison.Ordinal);

            // Email or an opaque refresh token alone cannot identify the same account.
            return JsonNode.DeepEquals(JsonNode.Parse(currentAuth), JsonNode.Parse(targetAuth));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? TryGetChatGptAccountId(string authText)
    {
        try
        {
            using var auth = JsonDocument.Parse(authText);
            return ReadEffectiveMode(auth.RootElement) == "chatgpt"
                ? ReadUsableChatGptAccountId(auth.RootElement)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadEffectiveMode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (root.TryGetProperty("auth_mode", out var mode) && mode.ValueKind != JsonValueKind.Null)
        {
            if (mode.ValueKind != JsonValueKind.String) return null;
            var value = mode.GetString();
            if (string.Equals(value, "apikey", StringComparison.OrdinalIgnoreCase)) return "apikey";
            if (string.Equals(value, "chatgpt", StringComparison.OrdinalIgnoreCase)) return "chatgpt";
            return null;
        }

        if (!string.IsNullOrWhiteSpace(ReadString(root, "OPENAI_API_KEY"))) return "apikey";
        return root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object
            ? "chatgpt"
            : null;
    }

    private static string? ReadUsableChatGptAccountId(JsonElement root)
    {
        if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object) return null;
        var accountId = ReadString(tokens, "account_id");
        var hasCredential = !string.IsNullOrWhiteSpace(ReadString(tokens, "access_token"))
            || !string.IsNullOrWhiteSpace(ReadString(tokens, "refresh_token"));
        return !string.IsNullOrWhiteSpace(accountId) && hasCredential ? accountId : null;
    }

    private static string? ReadString(JsonElement root, string propertyName) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

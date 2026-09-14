using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.Core.Services;

public sealed class LiveAuthInspector
{
    private readonly ICodexRuntimeEnvironmentService _runtimeEnvironment;

    public LiveAuthInspector()
        : this(new CodexRuntimeEnvironmentService())
    {
    }

    public LiveAuthInspector(ICodexRuntimeEnvironmentService runtimeEnvironment)
    {
        _runtimeEnvironment = runtimeEnvironment;
    }

    public LiveAuthIdentity? Inspect(string configText, string authText)
    {
        try
        {
            using var document = JsonDocument.Parse(authText);
            var root = document.RootElement;
            var authMode = TryGetString(root, "auth_mode");
            if (string.Equals(authMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
            {
                return InspectChatGpt(root);
            }

            var apiKey = TryGetString(root, "OPENAI_API_KEY");
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                return InspectApi(configText, apiKey);
            }
        }
        catch
        {
        }

        return null;
    }

    public LiveAuthIdentity? InspectChatGptSnapshot(string authText)
    {
        try
        {
            using var document = JsonDocument.Parse(authText);
            return InspectChatGpt(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public LiveAuthIdentity CreateApiIdentity(ApiProfileSpec spec)
    {
        var effectiveProvider = spec.GetEffectiveProviderId();
        var normalizedBaseUrl = NormalizeBaseUrl(spec.BaseUrl);
        var host = TryGetHost(spec.BaseUrl);
        return new LiveAuthIdentity
        {
            Kind = ProfileKind.ApiKey,
            AuthMode = "apikey",
            Provider = spec.GetDisplayProvider(),
            BaseUrl = normalizedBaseUrl,
            ApiHost = host,
            Model = spec.Model,
            ApiKeyMask = ProfileStore.MaskSecret(spec.ApiKey),
            IdentityFingerprint = BuildApiFingerprint(effectiveProvider, normalizedBaseUrl, spec.ApiKey)
        };
    }

    private LiveAuthIdentity? InspectChatGpt(JsonElement root)
    {
        var tokens = TryGetProperty(root, "tokens");
        if (tokens is null)
        {
            return null;
        }

        var accountId = TryGetString(tokens.Value, "account_id");
        var idToken = TryGetString(tokens.Value, "id_token");
        var accessToken = TryGetString(tokens.Value, "access_token");
        var refreshToken = TryGetString(tokens.Value, "refresh_token");

        var email = TryReadJwtPayloadString(idToken, "email")
            ?? TryReadJwtNestedPayloadString(accessToken, "https://api.openai.com/profile", "email");

        var fingerprint = BuildChatGptFingerprint(accountId, email, refreshToken);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }

        return new LiveAuthIdentity
        {
            Kind = ProfileKind.ChatGptSnapshot,
            AuthMode = "chatgpt",
            IdentityFingerprint = fingerprint,
            Email = email,
            AccountId = accountId
        };
    }

    private LiveAuthIdentity? InspectApi(string configText, string apiKey)
    {
        var provider = TomlOverlayService.TryReadScalar(configText, "model_provider") ?? "openai";
        var model = TomlOverlayService.TryReadScalar(configText, "model") ?? "(default)";
        var baseUrl = TomlOverlayService.TryReadSectionScalar(configText, TomlOverlayService.ProviderSection(provider), "base_url")
            ?? (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase)
                ? TomlOverlayService.TryReadScalar(configText, "openai_base_url")
                    ?? _runtimeEnvironment.GetOpenAiBaseUrl()
                : null)
            ?? string.Empty;
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var host = TryGetHost(baseUrl);

        return new LiveAuthIdentity
        {
            Kind = ProfileKind.ApiKey,
            AuthMode = "apikey",
            Provider = provider,
            BaseUrl = normalizedBaseUrl,
            ApiHost = host,
            Model = model,
            ApiKeyMask = ProfileStore.MaskSecret(apiKey),
            IdentityFingerprint = BuildApiFingerprint(provider, normalizedBaseUrl, apiKey)
        };
    }

    private static string BuildChatGptFingerprint(string? accountId, string? email, string? refreshToken)
    {
        if (!string.IsNullOrWhiteSpace(accountId))
        {
            return $"chatgpt:{accountId.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            return $"chatgpt-email:{email.Trim().ToLowerInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            return $"chatgpt-token:{HashForIdentity(refreshToken)}";
        }

        return string.Empty;
    }

    private static string BuildApiFingerprint(string? provider, string? normalizedBaseUrl, string apiKey)
    {
        var normalizedProvider = string.IsNullOrWhiteSpace(provider)
            ? "(default)"
            : provider.Trim().ToLowerInvariant();
        var normalizedUrl = string.IsNullOrWhiteSpace(normalizedBaseUrl)
            ? "(none)"
            : normalizedBaseUrl.Trim().ToLowerInvariant();
        return $"apikey:{normalizedProvider}|{normalizedUrl}|{HashForIdentity(apiKey)}";
    }

    private static string HashForIdentity(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string NormalizeBaseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        return trimmed.TrimEnd('/');
    }

    private static string? TryGetHost(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            ? uri.Host
            : raw.Trim();
    }

    private static JsonElement? TryGetProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? value : null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;
    }

    private static string? TryReadJwtPayloadString(string? jwt, string key)
    {
        if (TryDecodeJwtPayload(jwt) is not { } payload)
        {
            return null;
        }

        return TryGetString(payload, key);
    }

    private static string? TryReadJwtNestedPayloadString(string? jwt, string parentKey, string childKey)
    {
        if (TryDecodeJwtPayload(jwt) is not { } payload)
        {
            return null;
        }

        return payload.TryGetProperty(parentKey, out var parent) && parent.ValueKind == JsonValueKind.Object
            ? TryGetString(parent, childKey)
            : null;
    }

    private static JsonElement? TryDecodeJwtPayload(string? jwt)
    {
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payloadBytes = DecodeBase64Url(parts[1]);
            using var document = JsonDocument.Parse(payloadBytes);
            return document.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecodeBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized
        };
        return Convert.FromBase64String(normalized);
    }
}

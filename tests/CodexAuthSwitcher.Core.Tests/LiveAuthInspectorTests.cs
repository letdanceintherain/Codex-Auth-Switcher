using System.Text;
using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class LiveAuthInspectorTests
{
    [Fact]
    public void Inspect_ChatGptAuth_ExtractsAccountIdAndEmail()
    {
        var inspector = new LiveAuthInspector();
        var authJson = $$"""
                         {
                           "auth_mode": "chatgpt",
                           "tokens": {
                             "account_id": "acct-123",
                             "id_token": "{{BuildJwt(new { email = "alice@example.com" })}}",
                             "refresh_token": "rt-123"
                           }
                         }
                         """;

        var identity = inspector.Inspect("model = \"gpt-5.4\"", authJson);

        Assert.NotNull(identity);
        Assert.Equal(ProfileKind.ChatGptSnapshot, identity!.Kind);
        Assert.Equal("chatgpt", identity.AuthMode);
        Assert.Equal("alice@example.com", identity.Email);
        Assert.Equal("acct-123", identity.AccountId);
        Assert.Equal("chatgpt:acct-123", identity.IdentityFingerprint);
    }

    [Fact]
    public void Inspect_ApiAuth_ExtractsProviderHostAndMaskedKey()
    {
        var inspector = new LiveAuthInspector();
        var configText = """
                         model_provider = "crs"
                         model = "gpt-5.4"

                         [model_providers.crs]
                         base_url = "https://api.funai.vip"
                         """;
        var authJson = """
                       {
                         "OPENAI_API_KEY": "sk-test-1234567890"
                       }
                       """;

        var identity = inspector.Inspect(configText, authJson);

        Assert.NotNull(identity);
        Assert.Equal(ProfileKind.ApiKey, identity!.Kind);
        Assert.Equal("apikey", identity.AuthMode);
        Assert.Equal("crs", identity.Provider);
        Assert.Equal("api.funai.vip", identity.ApiHost);
        Assert.Equal("gpt-5.4", identity.Model);
        Assert.Equal("sk-tes...7890", identity.ApiKeyMask);
        Assert.StartsWith("apikey:crs|https://api.funai.vip|", identity.IdentityFingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_OpenAiCompatibility_UsesRuntimeBaseUrlWhenBuiltInProviderHasNoOverrideSection()
    {
        var runtimeEnvironment = new FakeRuntimeEnvironmentService
        {
            OpenAiBaseUrl = "https://api.funai.vip"
        };
        var inspector = new LiveAuthInspector(runtimeEnvironment);
        var configText = """
                         model_provider = "openai"
                         model = "gpt-5.4"
                         """;
        var authJson = """
                       {
                         "OPENAI_API_KEY": "sk-test-1234567890"
                       }
                       """;

        var identity = inspector.Inspect(configText, authJson);

        Assert.NotNull(identity);
        Assert.Equal(ProfileKind.ApiKey, identity!.Kind);
        Assert.Equal("openai", identity.Provider);
        Assert.Equal("api.funai.vip", identity.ApiHost);
        Assert.StartsWith("apikey:openai|https://api.funai.vip|", identity.IdentityFingerprint, StringComparison.Ordinal);
    }

    private static string BuildJwt(object payload)
    {
        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "none", typ = "JWT" }));
        var body = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        return $"{header}.{body}.";
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class FakeRuntimeEnvironmentService : ICodexRuntimeEnvironmentService
    {
        public string? OpenAiBaseUrl { get; set; }

        public string? GetOpenAiBaseUrl() => OpenAiBaseUrl;

        public void ApplyForProfile(ApiProfileSpec? spec)
        {
            OpenAiBaseUrl = spec is { UseOpenAiThreadView: true }
                ? spec.BaseUrl.TrimEnd('/')
                : null;
        }
    }
}

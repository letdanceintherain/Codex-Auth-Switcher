using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class CodexAuthSwitcherServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "CodexAuthSwitcherTests", Guid.NewGuid().ToString("N"));
    private string CodexHome => Path.Combine(_testRoot, ".codex");

    [Fact]
    public void SwitchProfile_ChangesOnlyConfigAndAuth_NotThreadFiles()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "config.toml"), """
                                                             model = "gpt-5.4"
                                                             model_reasoning_effort = "xhigh"

                                                             [windows]
                                                             sandbox = "elevated"
                                                             """);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), """
                                                           {
                                                             "auth_mode": "chatgpt",
                                                             "OPENAI_API_KEY": null,
                                                             "tokens": {
                                                               "refresh_token": "rt_test"
                                                             }
                                                           }
                                                           """);

        Directory.CreateDirectory(Path.Combine(CodexHome, "sessions"));
        File.WriteAllText(Path.Combine(CodexHome, "sessions", "thread.jsonl"), "thread-data");
        File.WriteAllText(Path.Combine(CodexHome, "state_5.sqlite"), "sqlite-data");

        var runtimeEnvironment = new FakeRuntimeEnvironmentService();
        var service = CreateService(runtimeEnvironment);
        service.CaptureCurrentChatGptSnapshot("chatgpt");
        service.SaveApiProfile(new ApiProfileSpec
        {
            Name = "funai",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            DisableResponseStorage = false,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-1234567890"
        });

        var sessionBefore = File.ReadAllText(Path.Combine(CodexHome, "sessions", "thread.jsonl"));
        var sqliteBefore = File.ReadAllText(Path.Combine(CodexHome, "state_5.sqlite"));

        var switchResult = service.SwitchProfile("funai");

        Assert.True(switchResult.RestartRequired);
        var switchedConfig = File.ReadAllText(Path.Combine(CodexHome, "config.toml"));
        Assert.Contains("model_provider = \"openai\"", switchedConfig);
        Assert.DoesNotContain("[model_providers.openai]", switchedConfig);
        Assert.Contains("disable_response_storage = false", switchedConfig);
        Assert.Equal("https://api.funai.vip", runtimeEnvironment.OpenAiBaseUrl);
        using (var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CodexHome, "auth.json"))))
        {
            Assert.Equal("sk-test-1234567890", document.RootElement.GetProperty("OPENAI_API_KEY").GetString());
        }

        Assert.Equal(sessionBefore, File.ReadAllText(Path.Combine(CodexHome, "sessions", "thread.jsonl")));
        Assert.Equal(sqliteBefore, File.ReadAllText(Path.Combine(CodexHome, "state_5.sqlite")));

        service.SwitchProfile("chatgpt");
        var environment = service.GetEnvironment();
        Assert.Equal("chatgpt", environment.CurrentAuthMode);
        Assert.Equal("gpt-5.4", environment.Model);
    }

    [Fact]
    public void EnsureCurrentIdentityTracked_AutoCapturesNewChatGptAccountOnlyOnce()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "config.toml"), "model = \"gpt-5.4\"" + Environment.NewLine);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), $$"""
                                                           {
                                                             "auth_mode": "chatgpt",
                                                             "OPENAI_API_KEY": null,
                                                             "tokens": {
                                                               "account_id": "acct-auto-001",
                                                               "id_token": "{{BuildJwt(new { email = "auto@example.com" })}}",
                                                               "refresh_token": "rt-auto"
                                                             }
                                                           }
                                                           """);

        var service = CreateService();

        var firstSync = service.EnsureCurrentIdentityTracked();
        var profilesAfterFirstSync = service.GetProfiles();
        var secondSync = service.EnsureCurrentIdentityTracked();
        var profilesAfterSecondSync = service.GetProfiles();
        var environment = service.GetEnvironment();

        Assert.True(firstSync.CreatedProfile);
        Assert.True(firstSync.IsManaged);
        Assert.Equal("chatgpt-auto@example.com", firstSync.MatchedProfileName);
        Assert.False(secondSync.CreatedProfile);
        Assert.True(secondSync.IsManaged);
        Assert.Single(profilesAfterFirstSync);
        Assert.Single(profilesAfterSecondSync);

        var profile = profilesAfterSecondSync[0];
        Assert.Equal("auto@example.com", profile.Email);
        Assert.Equal("acct-auto-001", profile.AccountId);
        Assert.True(profile.IsAutoCaptured);

        Assert.NotNull(environment.CurrentProfile);
        Assert.Equal(profile.Name, environment.CurrentProfile!.ProfileName);
        Assert.Equal(profile.IdentityFingerprint, environment.CurrentProfile.IdentityFingerprint);
    }

    [Fact]
    public void EnsureCurrentIdentityTracked_RecognizesApiProfileUsingOpenAiThreadView()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "config.toml"), """
                                                             model = "gpt-5.4"
                                                             model_reasoning_effort = "xhigh"
                                                             """);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), """
                                                           {
                                                             "auth_mode": "chatgpt",
                                                             "tokens": {
                                                               "refresh_token": "rt_test"
                                                             }
                                                           }
                                                           """);

        var runtimeEnvironment = new FakeRuntimeEnvironmentService();
        var service = CreateService(runtimeEnvironment);
        service.SaveApiProfile(new ApiProfileSpec
        {
            Name = "funai",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            DisableResponseStorage = false,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-1234567890",
            UseOpenAiThreadView = true
        });

        service.SwitchProfile("funai");
        var sync = service.EnsureCurrentIdentityTracked();
        var environment = service.GetEnvironment();

        Assert.True(sync.IsManaged);
        Assert.Equal("funai", sync.MatchedProfileName);
        Assert.Equal("https://api.funai.vip", runtimeEnvironment.OpenAiBaseUrl);
        Assert.NotNull(environment.CurrentProfile);
        Assert.Equal("funai", environment.CurrentProfile!.ProfileName);
        Assert.Equal("openai", environment.ModelProvider);
    }

    [Fact]
    public void SwitchProfile_ForcesLocalResponseStorageOn_ForLegacyApiProfiles()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "config.toml"), """
                                                             model = "gpt-5.4"
                                                             model_reasoning_effort = "xhigh"
                                                             disable_response_storage = true
                                                             """);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), """
                                                           {
                                                             "auth_mode": "chatgpt",
                                                             "tokens": {
                                                               "refresh_token": "rt_test"
                                                             }
                                                           }
                                                           """);

        var service = CreateService();
        service.SaveApiProfile(new ApiProfileSpec
        {
            Name = "legacy-api",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            DisableResponseStorage = true,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-legacy"
        });

        var profilePath = Path.Combine(CodexHome, "auth-switcher", "profiles", "legacy-api", "profile.json");
        var legacyMetadata = File.ReadAllText(profilePath).Replace("\"disableResponseStorage\": false", "\"disableResponseStorage\": true", StringComparison.Ordinal);
        File.WriteAllText(profilePath, legacyMetadata);

        service.SwitchProfile("legacy-api");

        var configText = File.ReadAllText(Path.Combine(CodexHome, "config.toml"));
        Assert.Contains("disable_response_storage = false", configText);

        var loaded = service.LoadApiProfile("legacy-api");
        Assert.NotNull(loaded);
        Assert.False(loaded!.DisableResponseStorage);
    }

    private CodexAuthSwitcherService CreateService(FakeRuntimeEnvironmentService? runtimeEnvironment = null)
    {
        runtimeEnvironment ??= new FakeRuntimeEnvironmentService();
        var inspector = new LiveAuthInspector(runtimeEnvironment);
        var profileStore = new ProfileStore(CodexHome, new ProtectedSecretStore(), inspector);
        return new CodexAuthSwitcherService(profileStore, inspector, runtimeEnvironment);
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

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
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

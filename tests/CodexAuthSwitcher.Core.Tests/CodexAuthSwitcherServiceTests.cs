using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class CodexAuthSwitcherServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "CodexAuthSwitcherTests", Guid.NewGuid().ToString("N"));
    private string CodexHome => Path.Combine(_testRoot, ".codex");

    [Fact]
    public void SwitchProfile_RoundTripPreservesThreadLibraryAndUnrelatedConfig()
    {
        WriteLiveFiles(
            """
            model_provider = "openai"
            model = "gpt-5.4"
            model_reasoning_effort = "xhigh"
            model_auto_compact_token_limit = 256000

            [windows]
            sandbox = "elevated"

            [mcp_servers.original]
            url = "https://example.test/mcp"
            """,
            ChatGptAuth("acct-original", "rt-original"));
        var protectedFiles = CreateThreadLibraryFixture();

        var service = CreateService();
        service.CaptureCurrentChatGptSnapshot("chatgpt");

        var chatGptProfilePath = Path.Combine(CodexHome, "auth-switcher", "profiles", "chatgpt");
        Assert.True(File.Exists(Path.Combine(chatGptProfilePath, "auth.bin")));
        Assert.False(File.Exists(Path.Combine(chatGptProfilePath, "auth.json")));

        File.AppendAllText(Path.Combine(CodexHome, "config.toml"), """

            [mcp_servers.added_after_capture]
            url = "https://after.example/mcp"

            [plugins.sample]
            enabled = true
            """);
        service.SaveApiProfile(CreateApiProfile());

        var switchResult = service.SwitchProfile("funai");

        Assert.True(switchResult.RestartRequired);
        var apiConfig = File.ReadAllText(Path.Combine(CodexHome, "config.toml"));
        Assert.Contains("model_provider = \"openai\"", apiConfig);
        Assert.Contains("openai_base_url = \"https://api.funai.vip\"", apiConfig);
        Assert.DoesNotContain("preferred_auth_method", apiConfig);
        Assert.DoesNotContain("disable_response_storage", apiConfig);
        Assert.Contains("[mcp_servers.original]", apiConfig);
        Assert.Contains("[mcp_servers.added_after_capture]", apiConfig);
        Assert.Contains("[plugins.sample]", apiConfig);
        AssertApiKey("sk-test-1234567890");
        AssertFilesUnchanged(protectedFiles);

        service.SwitchProfile("chatgpt");

        var chatGptConfig = File.ReadAllText(Path.Combine(CodexHome, "config.toml"));
        Assert.Contains("model_provider = \"openai\"", chatGptConfig);
        Assert.Contains("model = \"gpt-5.4\"", chatGptConfig);
        Assert.DoesNotContain("openai_base_url", chatGptConfig);
        Assert.Contains("[mcp_servers.original]", chatGptConfig);
        Assert.Contains("[mcp_servers.added_after_capture]", chatGptConfig);
        Assert.Contains("[plugins.sample]", chatGptConfig);
        Assert.Equal("chatgpt", service.GetEnvironment().CurrentAuthMode);
        AssertFilesUnchanged(protectedFiles);
    }

    [Fact]
    public void EnsureCurrentIdentityTracked_AutoCapturesNewChatGptAccountOnlyOnce()
    {
        WriteLiveFiles("model = \"gpt-5.4\"" + Environment.NewLine, $$"""
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
        var secondSync = service.EnsureCurrentIdentityTracked();
        var profiles = service.GetProfiles();

        Assert.True(firstSync.CreatedProfile);
        Assert.False(secondSync.CreatedProfile);
        var profile = Assert.Single(profiles);
        Assert.Equal("auto@example.com", profile.Email);
        Assert.Equal("acct-auto-001", profile.AccountId);
        Assert.True(profile.IsAutoCaptured);
        var profilePath = Path.Combine(CodexHome, "auth-switcher", "profiles", profile.Name);
        Assert.True(File.Exists(Path.Combine(profilePath, "auth.bin")));
        Assert.False(File.Exists(Path.Combine(profilePath, "auth.json")));
    }

    [Fact]
    public void EnsureCurrentIdentityTracked_RefreshesSavedChatGptAuthForMatchedAccount()
    {
        WriteLiveFiles("model = \"gpt-5.4\"" + Environment.NewLine, ChatGptAuth("acct-refresh", "rt-old"));
        var service = CreateService();
        var firstSync = service.EnsureCurrentIdentityTracked();
        Assert.NotNull(firstSync.MatchedProfileName);

        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), ChatGptAuth("acct-refresh", "rt-new"));
        service.EnsureCurrentIdentityTracked();
        service.SaveApiProfile(CreateApiProfile());
        service.SwitchProfile("funai");
        service.SwitchProfile(firstSync.MatchedProfileName!);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CodexHome, "auth.json")));
        Assert.Equal("rt-new", document.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString());
    }

    [Fact]
    public void EnsureCurrentIdentityTracked_RecognizesApiProfileUsingConfiguredOpenAiBaseUrl()
    {
        WriteLiveFiles("model = \"gpt-5.4\"" + Environment.NewLine, ChatGptAuth("acct-api", "rt-api"));
        var service = CreateService();
        service.SaveApiProfile(CreateApiProfile());

        service.SwitchProfile("funai");
        var sync = service.EnsureCurrentIdentityTracked();
        var environment = service.GetEnvironment();

        Assert.True(sync.IsManaged);
        Assert.Equal("funai", sync.MatchedProfileName);
        Assert.NotNull(environment.CurrentProfile);
        Assert.Equal("funai", environment.CurrentProfile!.ProfileName);
        Assert.Equal("openai", environment.ModelProvider);
        Assert.Equal("api.funai.vip", environment.LiveIdentity?.ApiHost);
    }

    [Fact]
    public void SwitchProfile_LegacyChatGptSnapshotMigratesWithoutReplacingWholeConfig()
    {
        WriteLiveFiles("""
            model_provider = "openai"
            model = "gpt-current"

            [mcp_servers.current]
            url = "https://current.example/mcp"
            """, ChatGptAuth("acct-legacy", "rt-live"));
        var service = CreateService();
        service.CaptureCurrentChatGptSnapshot("legacy");

        var profilePath = Path.Combine(CodexHome, "auth-switcher", "profiles", "legacy");
        var protectedStore = new ProtectedSecretStore();
        var authText = protectedStore.LoadSecret(Path.Combine(profilePath, "auth.bin"));
        File.Delete(Path.Combine(profilePath, "auth.bin"));
        File.WriteAllText(Path.Combine(profilePath, "auth.json"), authText);
        File.WriteAllText(Path.Combine(profilePath, "config.toml"), """
            model_provider = "openai"
            model = "gpt-snapshot"

            [mcp_servers.old_snapshot]
            url = "https://old.example/mcp"
            """);

        service.SwitchProfile("legacy");

        var configText = File.ReadAllText(Path.Combine(CodexHome, "config.toml"));
        Assert.Contains("model = \"gpt-snapshot\"", configText);
        Assert.Contains("[mcp_servers.current]", configText);
        Assert.DoesNotContain("[mcp_servers.old_snapshot]", configText);
        Assert.True(File.Exists(Path.Combine(profilePath, "auth.bin")));
        Assert.False(File.Exists(Path.Combine(profilePath, "auth.json")));
    }

    [Theory]
    [InlineData("keyring")]
    [InlineData("auto")]
    [InlineData("ephemeral")]
    public void SwitchProfile_BlocksNonFileCredentialStores(string credentialStore)
    {
        WriteLiveFiles($$"""
            cli_auth_credentials_store = "{{credentialStore}}"
            model = "gpt-5.4"
            """, ChatGptAuth("acct-store", "rt-store"));
        var service = CreateService();
        service.SaveApiProfile(CreateApiProfile());

        var error = Assert.Throws<InvalidOperationException>(() => service.SwitchProfile("funai"));

        Assert.Contains("cli_auth_credentials_store", error.Message);
        Assert.Contains("auth.json", error.Message);
    }

    [Fact]
    public void SwitchProfile_CanCreateMissingAuthFileInFileMode()
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "config.toml"), "model = \"gpt-5.4\"" + Environment.NewLine);
        var service = CreateService();
        service.SaveApiProfile(CreateApiProfile());

        service.SwitchProfile("funai");

        Assert.True(File.Exists(Path.Combine(CodexHome, "auth.json")));
        AssertApiKey("sk-test-1234567890");
    }

    [Fact]
    public void SwitchProfile_RollsBackConfigWhenAuthCannotBeReplaced()
    {
        WriteLiveFiles("model = \"gpt-original\"" + Environment.NewLine, ChatGptAuth("acct-lock", "rt-lock"));
        var originalConfig = File.ReadAllText(Path.Combine(CodexHome, "config.toml"));
        var originalAuth = File.ReadAllText(Path.Combine(CodexHome, "auth.json"));
        var service = CreateService();
        service.SaveApiProfile(CreateApiProfile());

        using var authLock = new FileStream(
            Path.Combine(CodexHome, "auth.json"),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        Assert.Throws<IOException>(() => service.SwitchProfile("funai"));

        Assert.Equal(originalConfig, File.ReadAllText(Path.Combine(CodexHome, "config.toml")));
        Assert.Equal(originalAuth, File.ReadAllText(Path.Combine(CodexHome, "auth.json")));
    }

    private CodexAuthSwitcherService CreateService()
    {
        var runtimeEnvironment = new FakeRuntimeEnvironmentService();
        var inspector = new LiveAuthInspector(runtimeEnvironment);
        var profileStore = new ProfileStore(CodexHome, new ProtectedSecretStore(), inspector);
        return new CodexAuthSwitcherService(profileStore, inspector, runtimeEnvironment);
    }

    private static ApiProfileSpec CreateApiProfile()
    {
        return new ApiProfileSpec
        {
            Name = "funai",
            Provider = "crs",
            BaseUrl = "https://api.funai.vip/",
            Model = "gpt-5.4",
            ReasoningEffort = "xhigh",
            WireApi = "responses",
            RequiresOpenAiAuth = true,
            ModelAutoCompactTokenLimit = 256000,
            ApiKey = "sk-test-1234567890",
            UseOpenAiThreadView = true
        };
    }

    private void WriteLiveFiles(string configText, string authText)
    {
        Directory.CreateDirectory(CodexHome);
        File.WriteAllText(Path.Combine(CodexHome, "config.toml"), configText);
        File.WriteAllText(Path.Combine(CodexHome, "auth.json"), authText);
    }

    private Dictionary<string, byte[]> CreateThreadLibraryFixture()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["sessions/thread.jsonl"] = "thread-data"u8.ToArray(),
            ["archived_sessions/archived.jsonl"] = "archived-data"u8.ToArray(),
            ["session_index.jsonl"] = "index-data"u8.ToArray(),
            ["state_5.sqlite"] = [0, 1, 2, 3, 4],
            ["thread_history_1.sqlite"] = [5, 6, 7, 8]
        };

        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(CodexHome, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        return files;
    }

    private void AssertFilesUnchanged(IReadOnlyDictionary<string, byte[]> expectedFiles)
    {
        foreach (var (relativePath, expectedContent) in expectedFiles)
        {
            var path = Path.Combine(CodexHome, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.Equal(expectedContent, File.ReadAllBytes(path));
        }
    }

    private void AssertApiKey(string expected)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(CodexHome, "auth.json")));
        Assert.Equal(expected, document.RootElement.GetProperty("OPENAI_API_KEY").GetString());
    }

    private static string ChatGptAuth(string accountId, string refreshToken)
    {
        return JsonSerializer.Serialize(new
        {
            auth_mode = "chatgpt",
            OPENAI_API_KEY = (string?)null,
            tokens = new
            {
                account_id = accountId,
                refresh_token = refreshToken
            }
        });
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
        public string? GetOpenAiBaseUrl() => null;

        public void ApplyForProfile(ApiProfileSpec? spec)
        {
        }
    }
}

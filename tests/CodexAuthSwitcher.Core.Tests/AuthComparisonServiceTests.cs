using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class AuthComparisonServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "CodexAuthSwitcherAuthTests", Guid.NewGuid().ToString("N"));
    private string AuthPath => Path.Combine(_testRoot, "auth.json");
    private string ConfigPath => Path.Combine(_testRoot, "config.toml");
    private const string ChatConfig = "sqlite_home = '.'\nmodel_provider = \"openai\"\nmodel = \"gpt-test\"\n";

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"auth_mode\":\"apikey\",\"tokens\":null,\"last_refresh\":\"2026-10-09\"}")]
    [InlineData("{\"auth_mode\":null,\"tokens\":null,\"extra\":{\"version\":2}}")]
    public void Equivalent_ApiIgnoresIncidentalFields(string incidentalFields)
    {
        using var fields = JsonDocument.Parse(incidentalFields);
        var value = JsonSerializer.Deserialize<Dictionary<string, object?>>(fields.RootElement.GetRawText())!;
        value["OPENAI_API_KEY"] = "fake-api-key";
        Assert.True(AuthComparisonService.Equivalent("{\"OPENAI_API_KEY\":\"fake-api-key\"}", JsonSerializer.Serialize(value)));
    }

    [Theory]
    [InlineData("{\"auth_mode\":\"chatgpt\",\"OPENAI_API_KEY\":\"fake-api-key\"}")]
    [InlineData("{\"auth_mode\":\"chatgptAuthTokens\",\"OPENAI_API_KEY\":\"fake-api-key\"}")]
    [InlineData("{\"auth_mode\":\"unknown\",\"OPENAI_API_KEY\":\"fake-api-key\"}")]
    [InlineData("{\"auth_mode\":false,\"OPENAI_API_KEY\":\"fake-api-key\"}")]
    [InlineData("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"other-fake-key\"}")]
    [InlineData("{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":null}")]
    public void Equivalent_ApiRejectsDifferentModeOrKey(string otherAuth)
    {
        Assert.False(AuthComparisonService.Equivalent("{\"OPENAI_API_KEY\":\"fake-api-key\"}", otherAuth));
    }

    [Fact]
    public void Equivalent_ChatGptAllowsTokenRotationForSameAccount()
    {
        Assert.True(AuthComparisonService.Equivalent(ChatAuth("acct-one", "old"), ChatAuth("acct-one", "new")));
        Assert.False(AuthComparisonService.Equivalent(ChatAuth("acct-one", "old"), ChatAuth("acct-two", "new")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Equivalent_ChatGptDoesNotUseEmailAsAccountIdentity(string? accountId)
    {
        Assert.False(AuthComparisonService.Equivalent(ChatAuth(accountId, "old"), ChatAuth(accountId, "new")));
    }

    [Fact]
    public void Equivalent_ChatGptWithoutUsableTokensRequiresExactSnapshot()
    {
        const string noTokens = "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"acct-one\"}}";
        Assert.False(AuthComparisonService.Equivalent(noTokens, ChatAuth("acct-one", "new")));
        Assert.Null(AuthComparisonService.TryGetChatGptAccountId(noTokens));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("malformed")]
    [InlineData("{\"auth_mode\":\"chatgpt\",\"tokens\":false}")]
    public void Equivalent_MalformedAuthDoesNotMatchValidCredential(string malformed)
    {
        Assert.False(AuthComparisonService.Equivalent(malformed, ChatAuth("acct-one", "new")));
        Assert.Null(AuthComparisonService.TryGetChatGptAccountId(malformed));
    }

    [Fact]
    public void SwitchProfile_ApiWithNormalizedAuthIsReadOnlyWhileCodexRuns()
    {
        var spec = ApiSpec();
        var auth = "{\"auth_mode\":\"apikey\",\"OPENAI_API_KEY\":\"fake-api-key\",\"tokens\":null,\"extra\":true}";
        WriteLive(TomlOverlayService.ApplyApiOverlay(ChatConfig, spec), auth);
        var service = CreateService(() => true);
        service.SaveApiProfile(spec);
        var before = ReadAllFiles();

        Assert.True(service.IsProfileActive(spec.Name));
        var result = service.SwitchProfile(spec.Name);

        Assert.True(result.AlreadyActive);
        Assert.False(result.RestartRequired);
        Assert.Equal(string.Empty, result.BackupPath);
        AssertFilesEqual(before);
    }

    [Fact]
    public void SwitchProfile_SameChatGptWithRotatedTokensIsReadOnlyWhileCodexRuns()
    {
        WriteLive(ChatConfig, ChatAuth("acct-one", "old"));
        var service = CreateService(() => true);
        service.CaptureCurrentChatGptSnapshot("one");
        File.WriteAllText(AuthPath, ChatAuth("acct-one", "new"));
        var before = ReadAllFiles();

        Assert.True(service.IsProfileActive("one"));
        var result = service.SwitchProfile("one");

        Assert.True(result.AlreadyActive);
        Assert.False(result.RestartRequired);
        AssertFilesEqual(before);
        Assert.Equal("new", ReadToken(AuthPath));
    }

    [Fact]
    public void SwitchProfile_CapturesLatestTokensBeforeLeavingAccount()
    {
        WriteLive(ChatConfig, ChatAuth("acct-one", "old"));
        var service = CreateService();
        service.CaptureCurrentChatGptSnapshot("one");
        service.SaveApiProfile(ApiSpec());
        File.WriteAllText(AuthPath, ChatAuth("acct-one", "new"));
        var snapshotConfig = File.ReadAllBytes(ProfilePath("one", "config.toml"));

        service.SwitchProfile("api");
        service.SwitchProfile("one");

        Assert.Equal("new", ReadToken(AuthPath));
        Assert.Equal(snapshotConfig, File.ReadAllBytes(ProfilePath("one", "config.toml")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_testRoot, "auth-switcher", "backups")));
    }

    [Fact]
    public void SwitchProfile_RealAccountChangeKeepsAccountsDistinctAndRefreshesOutgoingTokens()
    {
        WriteLive(ChatConfig, ChatAuth("acct-one", "one-old"));
        var service = CreateService();
        service.CaptureCurrentChatGptSnapshot("one");
        File.WriteAllText(AuthPath, ChatAuth("acct-two", "two-old"));
        service.CaptureCurrentChatGptSnapshot("two");
        File.WriteAllText(AuthPath, ChatAuth("acct-two", "two-new"));

        Assert.False(service.IsProfileActive("one"));
        var result = service.SwitchProfile("one");

        Assert.False(result.AlreadyActive);
        Assert.Equal("one-old", ReadToken(AuthPath));
        service.SwitchProfile("two");
        Assert.Equal("two-new", ReadToken(AuthPath));
    }

    [Fact]
    public void RefreshSavedChatGptAuth_UpdatesMatchingDuplicatesWithoutChangingModelSnapshots()
    {
        WriteLive(ChatConfig, ChatAuth("acct-one", "old"));
        var inspector = new LiveAuthInspector(new FakeRuntime());
        var store = new ProfileStore(_testRoot, new ProtectedSecretStore(), inspector);
        store.SaveChatGptSnapshot("one", ChatConfig, ChatAuth("acct-one", "old"));
        store.SaveChatGptSnapshot("same-account-other-model", ChatConfig.Replace("gpt-test", "gpt-other"), ChatAuth("acct-one", "older"));
        store.SaveChatGptSnapshot("different-account", ChatConfig, ChatAuth("acct-two", "unchanged"));
        var otherModel = File.ReadAllBytes(ProfilePath("same-account-other-model", "config.toml"));
        var differentAuth = File.ReadAllBytes(ProfilePath("different-account", "auth.bin"));

        store.RefreshSavedChatGptAuth(ChatAuth("acct-one", "new"));

        var protectedStore = new ProtectedSecretStore();
        foreach (var name in new[] { "one", "same-account-other-model" })
        {
            using var auth = JsonDocument.Parse(protectedStore.LoadSecret(ProfilePath(name, "auth.bin")));
            Assert.Equal("new", auth.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString());
        }
        Assert.Equal(otherModel, File.ReadAllBytes(ProfilePath("same-account-other-model", "config.toml")));
        Assert.Equal(differentAuth, File.ReadAllBytes(ProfilePath("different-account", "auth.bin")));
    }

    [Fact]
    public void EnsureCurrentIdentityTracked_DoesNotMergeUnknownAccountsByEmail()
    {
        WriteLive(ChatConfig, ChatAuth(null, "first"));
        var service = CreateService();
        var first = service.EnsureCurrentIdentityTracked();
        Assert.True(first.CreatedProfile);
        var firstSnapshot = File.ReadAllBytes(ProfilePath(first.MatchedProfileName!, "auth.bin"));

        File.WriteAllText(AuthPath, ChatAuth(null, "second"));
        var second = service.EnsureCurrentIdentityTracked();
        var repeat = service.EnsureCurrentIdentityTracked();

        Assert.True(second.CreatedProfile);
        Assert.NotEqual(first.MatchedProfileName, second.MatchedProfileName);
        Assert.False(repeat.CreatedProfile);
        Assert.Equal(second.MatchedProfileName, repeat.MatchedProfileName);
        Assert.Equal(firstSnapshot, File.ReadAllBytes(ProfilePath(first.MatchedProfileName!, "auth.bin")));
        Assert.Equal(2, service.GetProfiles().Count);
    }

    private CodexAuthSwitcherService CreateService(Func<bool>? hasRunningCodex = null)
    {
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        var store = new ProfileStore(_testRoot, new ProtectedSecretStore(), inspector);
        return new CodexAuthSwitcherService(store, inspector, runtime, hasRunningCodex);
    }

    private static ApiProfileSpec ApiSpec() => new()
    {
        Name = "api", Provider = "relay", BaseUrl = "https://example.test/v1", Model = "gpt-test",
        ReasoningEffort = "high", ApiKey = "fake-api-key", ModelAutoCompactTokenLimit = 10000
    };

    private static string ChatAuth(string? accountId, string refreshToken) => JsonSerializer.Serialize(new
    {
        auth_mode = "chatgpt",
        OPENAI_API_KEY = (string?)null,
        tokens = new { account_id = accountId, refresh_token = refreshToken, access_token = "fake-access-" + refreshToken,
            id_token = "e30." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { email = "same@example.test" })).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." }
    });

    private void WriteLive(string config, string auth)
    {
        Directory.CreateDirectory(_testRoot);
        File.WriteAllText(ConfigPath, config);
        File.WriteAllText(AuthPath, auth);
    }

    private Dictionary<string, byte[]> ReadAllFiles() => Directory.GetFiles(_testRoot, "*", SearchOption.AllDirectories)
        .ToDictionary(path => path, File.ReadAllBytes);

    private void AssertFilesEqual(Dictionary<string, byte[]> before)
    {
        var after = ReadAllFiles();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }

    private static string? ReadToken(string authPath)
    {
        using var auth = JsonDocument.Parse(File.ReadAllText(authPath));
        return auth.RootElement.GetProperty("tokens").GetProperty("refresh_token").GetString();
    }

    private string ProfilePath(string profile, string file) => Path.Combine(_testRoot, "auth-switcher", "profiles", profile, file);

    public void Dispose()
    {
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, recursive: true);
    }

    private sealed class FakeRuntime : ICodexRuntimeEnvironmentService
    {
        public string? GetOpenAiBaseUrl() => null;
        public void ApplyForProfile(ApiProfileSpec? spec) { }
    }
}

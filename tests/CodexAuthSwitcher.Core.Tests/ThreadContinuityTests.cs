using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class ThreadContinuityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwitcherContinuity", Guid.NewGuid().ToString("N"));
    private readonly CodexAuthSwitcherService _service;
    private string StatePath => Path.Combine(_root, "state_5.sqlite");
    private string HistoryPath => Path.Combine(_root, "thread_history_1.sqlite");

    public ThreadContinuityTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "config.toml"), "sqlite_home = '.'\nmodel_provider = \"openai\"\nmodel = \"gpt-5.4\"\n");
        WriteAccount("a");
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        _service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime);
        _service.CaptureCurrentChatGptSnapshot("account-a");
        WriteAccount("b");
        _service.CaptureCurrentChatGptSnapshot("account-b");
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api-a", Provider = "my-provider", BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "test-a" });
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api-b", Provider = "second-provider", BaseUrl = "http://127.0.0.1:2/v1", ApiKey = "test-b" });
    }

    [Fact]
    public void RoundTrip_ListsActiveIdsUnderEveryProvider_AndLeavesArchiveUntouched()
    {
        CreateDatabases();
        var first = CreateRollout("first", "crs", false, "\r\n", true);
        var archived = CreateRollout("archived", "krill", true, "\n", false);
        var originalMessage = ReadMessage(first);
        var originalArchiveMessage = ReadMessage(archived);
        var archiveBytes = File.ReadAllBytes(archived);
        var archiveOffset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'archived'");
        var snapshot = ReadMetadata();
        foreach (var profile in new[] { "api-a", "account-a", "api-b", "account-b", "api-a" })
        {
            var result = _service.SwitchProfile(profile);
            var provider = profile.StartsWith("account-") ? "openai" : profile == "api-a" ? "my-provider" : "second-provider";
            using var state = Open(StatePath);
            using var query = state.CreateCommand();
            query.CommandText = "SELECT id FROM threads WHERE model_provider = $provider ORDER BY id";
            query.Parameters.AddWithValue("$provider", provider);
            using var reader = query.ExecuteReader();
            var visibleIds = new List<string>();
            while (reader.Read()) visibleIds.Add(reader.GetString(0));
            Assert.Equal(new[] { "first" }, visibleIds);
            Assert.Equal(snapshot, ReadMetadata());
            Assert.Equal(originalMessage, ReadMessage(first));
            Assert.Equal(originalArchiveMessage, ReadMessage(archived));
            AssertProvider(first, provider);
            AssertProvider(archived, "krill");
            Assert.Equal(archiveBytes, File.ReadAllBytes(archived));
            Assert.Equal("krill", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'archived'"));
            Assert.Equal(archiveOffset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'archived'"));
            Assert.Equal(1, result.SynchronizedThreads);
            Assert.Equal(new FileInfo(first).Length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'first'"));
            Assert.Equal(2L, Scalar(HistoryPath, "SELECT COUNT(*) FROM thread_items"));
            Assert.Equal("body with crs and openai unchanged", TextScalar(HistoryPath, "SELECT item_json FROM thread_items WHERE thread_id = 'first'"));
            Assert.True(File.Exists(Path.Combine(result.BackupPath, "continuity", "state_5.sqlite")));
            Assert.Equal(profile, _service.EnsureCurrentIdentityTracked().MatchedProfileName);
        }
    }

    [Fact]
    public void DuplicateArchivedCopies_AndInvalidOrCompressedArchives_DoNotBlockSwitch()
    {
        CreateDatabases();
        var active = CreateRollout("shared", "crs", false, "\n", false);
        var archived = CreateRollout("shared", "crs", true, "\n", false, false);
        var archiveDirectory = Path.GetDirectoryName(archived)!;
        for (var index = 1; index < 5; index++)
            File.Copy(archived, Path.Combine(archiveDirectory, $"shared-copy-{index}.jsonl"));
        File.WriteAllText(Path.Combine(archiveDirectory, "broken.jsonl"), "{invalid archive");
        File.WriteAllText(Path.Combine(archiveDirectory, "compressed.jsonl.zst"), "not read");
        var before = Directory.GetFiles(archiveDirectory).ToDictionary(path => path, File.ReadAllBytes);

        var result = _service.SwitchProfile("api-a");

        AssertProvider(active, "my-provider");
        Assert.Equal(1, result.SynchronizedThreads);
        Assert.Equal(new FileInfo(active).Length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(before.Count, Directory.GetFiles(archiveDirectory).Length);
        Assert.Single(Directory.GetFiles(Path.Combine(result.BackupPath, "continuity"), "rollout-*.jsonl"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ArchivedFlagOrDirectory_ExcludesDatabaseRowFileAndHistoryCache(bool archiveFlag, bool archiveDirectory)
    {
        CreateDatabases();
        var archived = CreateRollout("archived", "krill", archiveDirectory, "\n", false);
        Execute(StatePath, $"UPDATE threads SET archived = {(archiveFlag ? 1 : 0)} WHERE id = 'archived'");
        var bytes = File.ReadAllBytes(archived);
        var offset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state");
        // An unindexed copy in sessions must not shift the archived ID's cache.
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        var copy = Path.Combine(_root, "sessions", "stray-archive-copy.jsonl");
        File.Copy(archived, copy);
        CreateRollout("active", "crs", false, "\n", false);

        var result = _service.SwitchProfile("api-a");

        Assert.Equal(1, result.SynchronizedThreads);
        Assert.Equal(bytes, File.ReadAllBytes(archived));
        Assert.Equal(bytes, File.ReadAllBytes(copy));
        Assert.Equal("krill", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'archived'"));
        Assert.Equal(offset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'archived'"));
    }

    [Fact]
    public void DuplicateActiveCopies_StillBlockAmbiguousCacheUpdates()
    {
        CreateDatabases();
        var active = CreateRollout("active", "crs", false, "\n", false);
        var bytes = File.ReadAllBytes(active);
        File.Copy(active, Path.Combine(_root, "sessions", "active-copy.jsonl"));
        var error = Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Contains("Duplicate session identity", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(active));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads"));
    }

    [Fact]
    public void FailedAuthWrite_RestoresRolloutsDatabasesConfigAndHistoryOffsets()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        var bytes = File.ReadAllBytes(path);
        var config = File.ReadAllBytes(Path.Combine(_root, "config.toml"));
        var offset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state");
        using (var authLock = File.Open(Path.Combine(_root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(config, File.ReadAllBytes(Path.Combine(_root, "config.toml")));
        Assert.Equal("openai", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal(offset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        // The rolled-back journal must allow a retry.
        _service.SwitchProfile("api-b");
        AssertProvider(path, "second-provider");
    }

    [Fact]
    public void LockedStateDatabase_PreventsAuthSwitch()
    {
        CreateDatabases();
        var before = File.ReadAllBytes(Path.Combine(_root, "auth.json"));
        using var state = Open(StatePath);
        using var transaction = state.BeginTransaction();
        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "auth.json")));
    }

    [Fact]
    public void InvalidRollout_PreventsPartialMigration()
    {
        CreateDatabases();
        var first = CreateRollout("first", "crs", false, "\n", false);
        var bytes = File.ReadAllBytes(first);
        File.WriteAllText(Path.Combine(_root, "sessions", "broken.jsonl"), "{invalid");
        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Equal(bytes, File.ReadAllBytes(first));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal("chatgpt", _service.GetEnvironment().CurrentAuthMode);
    }

    [Fact]
    public void RolloutOnlyHistory_IsMigratedWithoutCreatingDatabase()
    {
        var path = CreateRollout("first", "crs", false, "\n", false, false);
        _service.SwitchProfile("api-a");
        AssertProvider(path, "my-provider");
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void MissingRollout_StillPreservesAndMigratesThreadRow()
    {
        CreateDatabases();
        var path = CreateRollout("first", "crs", false, "\n", false);
        File.Delete(path);
        _service.SwitchProfile("api-a");
        Assert.Equal("my-provider", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WindowsVerbatimRolloutPath_IsTheSameLocalFile()
    {
        CreateDatabases();
        var path = CreateRollout("first", "crs", false, "\n", false);
        using (var state = Open(StatePath))
        using (var update = state.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET rollout_path=$path";
            update.Parameters.AddWithValue("$path", @"\\?\" + path);
            update.ExecuteNonQuery();
        }
        _service.SwitchProfile("api-a");
        AssertProvider(path, "my-provider");
        Assert.Equal(new FileInfo(path).Length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
    }

    [Fact]
    public void SqliteHome_IsRespectedWithoutChangingInactiveDatabase()
    {
        CreateDatabases();
        var path = CreateRollout("first", "crs", false, "\n", false);
        var stateHome = Path.Combine(_root, "custom-state");
        Directory.CreateDirectory(stateHome);
        File.Copy(StatePath, Path.Combine(stateHome, "state_5.sqlite"));
        File.Copy(HistoryPath, Path.Combine(stateHome, "thread_history_1.sqlite"));
        var configPath = Path.Combine(_root, "config.toml");
        File.WriteAllText(configPath, TomlOverlayService.SetScalar(File.ReadAllText(configPath), "sqlite_home", "custom-state"));
        _service.SwitchProfile("api-a");
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal("my-provider", TextScalar(Path.Combine(stateHome, "state_5.sqlite"), "SELECT model_provider FROM threads"));
        AssertProvider(path, "my-provider");
    }

    [Fact]
    public void LegacyApiFingerprint_IsRecognizedAfterMigration()
    {
        var metadataPath = Path.Combine(_root, "auth-switcher", "profiles", "api-a", "profile.json");
        var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!;
        // Metadata uses snake_case naming.
        var key = metadata.AsObject().Select(pair => pair.Key).Single(name => name.Replace("_", "").Equals("identityfingerprint", StringComparison.OrdinalIgnoreCase));
        metadata[key] = "apikey:openai|http://127.0.0.1:1/v1|oldhash";
        File.WriteAllText(metadataPath, metadata.ToJsonString());
        _service.SwitchProfile("api-a");
        Assert.Equal("api-a", _service.EnsureCurrentIdentityTracked().MatchedProfileName);
        Assert.Equal("my-provider", _service.GetEnvironment().LiveIdentity!.Provider);
    }

    [Fact]
    public void OAuthSnapshotWithStaleApiRouting_ReturnsToOfficialProvider()
    {
        File.WriteAllText(Path.Combine(_root, "config.toml"), "sqlite_home = '.'\nmodel_provider = 'stale-api'\nopenai_base_url = 'https://stale.invalid'\n");
        _service.CaptureCurrentChatGptSnapshot("stale");
        _service.SwitchProfile("api-a");
        _service.SwitchProfile("stale");
        Assert.Equal("openai", _service.GetEnvironment().ModelProvider);
        Assert.DoesNotContain("openai_base_url", File.ReadAllText(Path.Combine(_root, "config.toml")));
    }

    [Fact]
    public void RunningCodexGuard_PreventsAllWrites()
    {
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        var service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime, () => true);
        Assert.Throws<InvalidOperationException>(() => service.SwitchProfile("api-a"));
        Assert.Equal("chatgpt", service.GetEnvironment().CurrentAuthMode);
    }

    [Fact]
    public void BackupIncludesUncheckpointedWalRows()
    {
        CreateDatabases();
        using var state = Open(StatePath);
        using (var command = state.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
            command.ExecuteNonQuery();
        }
        CreateRollout("first", "crs", false, "\n", false);
        var result = _service.SwitchProfile("api-a");
        Assert.Equal("crs", TextScalar(Path.Combine(result.BackupPath, "continuity", "state_5.sqlite"), "SELECT model_provider FROM threads"));
    }

    private void CreateDatabases()
    {
        Execute(StatePath, """
            CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, model_provider TEXT, title TEXT,
                archived INTEGER, created_at INTEGER, updated_at INTEGER, is_pinned INTEGER, section_position INTEGER);
            """);
        Execute(HistoryPath, """
            CREATE TABLE thread_history_projection_state (thread_id TEXT PRIMARY KEY, next_rollout_byte_offset INTEGER, next_rollout_ordinal INTEGER);
            CREATE TABLE thread_items (thread_id TEXT, item_json TEXT);
            """);
    }

    private string CreateRollout(string id, string provider, bool archived, string newline, bool bom, bool withDb = true)
    {
        var directory = Path.Combine(_root, archived ? "archived_sessions" : "sessions");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, id + ".jsonl");
        var header = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id, model_provider = provider, cwd = "C:/project", title = "unchanged" } });
        var settings = JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "thread_settings_applied", thread_settings = new { model_provider_id = provider, model = "gpt-5.4" } } });
        var message = """{"type":"response_item", "payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"中文聊天 crs openai; model_provider must stay in text"}]}}""";
        File.WriteAllText(path, header + newline + settings + newline + message + newline, new UTF8Encoding(bom));
        if (withDb)
        {
            using var state = Open(StatePath);
            using var insert = state.CreateCommand();
            insert.CommandText = "INSERT INTO threads VALUES($id,$path,$provider,'saved title',$archived,100,200,1,3)";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$provider", provider);
            insert.Parameters.AddWithValue("$archived", archived ? 1 : 0);
            insert.ExecuteNonQuery();
            using var history = Open(HistoryPath);
            using var historyInsert = history.CreateCommand();
            historyInsert.CommandText = "INSERT INTO thread_history_projection_state VALUES($id,$offset,3); INSERT INTO thread_items VALUES($id,'body with crs and openai unchanged');";
            historyInsert.Parameters.AddWithValue("$id", id);
            historyInsert.Parameters.AddWithValue("$offset", new FileInfo(path).Length);
            historyInsert.ExecuteNonQuery();
        }
        return path;
    }

    private static byte[] ReadMessage(string path) => Encoding.UTF8.GetBytes(File.ReadAllLines(path).Last());
    private static void AssertProvider(string path, string provider)
    {
        var lines = File.ReadAllLines(path);
        using var header = JsonDocument.Parse(lines[0]);
        using var settings = JsonDocument.Parse(lines[1]);
        Assert.Equal(provider, header.RootElement.GetProperty("payload").GetProperty("model_provider").GetString());
        Assert.Equal(provider, settings.RootElement.GetProperty("payload").GetProperty("thread_settings").GetProperty("model_provider_id").GetString());
    }

    private string ReadMetadata() => TextScalar(StatePath, "SELECT group_concat(id || title || archived || created_at || updated_at || is_pinned || section_position, '|') FROM (SELECT * FROM threads ORDER BY id)");
    private void WriteAccount(string id) => File.WriteAllText(Path.Combine(_root, "auth.json"), JsonSerializer.Serialize(new { auth_mode = "chatgpt", tokens = new { account_id = id, refresh_token = "fake-" + id } }));
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static string TextScalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }
    private static long Scalar(string path, string sql) => long.Parse(TextScalar(path, sql));
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class FakeRuntime : ICodexRuntimeEnvironmentService
    {
        public string? GetOpenAiBaseUrl() => null;
        public void ApplyForProfile(ApiProfileSpec? spec) { }
    }
}

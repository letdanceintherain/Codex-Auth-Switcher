using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class FixedWidthContinuityTests : IDisposable
{
    private const string ThreadId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string SourceId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwitcherFixedWidth", Guid.NewGuid().ToString("N"));
    private readonly CodexAuthSwitcherService _service;
    private string StatePath => Path.Combine(_root, "state_5.sqlite");
    private string HistoryPath => Path.Combine(_root, "thread_history_1.sqlite");
    private string SessionsPath => Path.Combine(_root, "sessions", "2026", "10", "09");
    private string ConfigPath => Path.Combine(_root, "config.toml");
    private string AuthPath => Path.Combine(_root, "auth.json");
    private string BackupsPath => Path.Combine(_root, "auth-switcher", "backups");

    public FixedWidthContinuityTests()
    {
        Directory.CreateDirectory(SessionsPath);
        File.WriteAllText(ConfigPath, "sqlite_home = '.'\nmodel_provider = 'openai'\nmodel = 'gpt-5.4'\n");
        File.WriteAllText(AuthPath, "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"synthetic\",\"refresh_token\":\"synthetic-only\"}}");
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        _service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime);
        _service.CaptureCurrentChatGptSnapshot("account");
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api-a", Provider = "provider-alpha", BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "fake-alpha" });
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api-b", Provider = "provider-beta", BaseUrl = "http://127.0.0.1:2/v1", ApiKey = "fake-beta" });
        Execute(StatePath, "CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, model_provider TEXT, archived INTEGER);");
        Execute(HistoryPath, """
            CREATE TABLE thread_history_projection_state (thread_id TEXT PRIMARY KEY, next_rollout_byte_offset INTEGER, next_rollout_ordinal INTEGER);
            CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT, rollout_byte_offset INTEGER, rollout_end_byte_offset INTEGER);
            CREATE TABLE thread_items (thread_id TEXT, item_json TEXT);
            """);
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void CompleteSettingsAppendedBetweenSwitches_KeepOneFileAndExistingByteBoundaries(string newline, bool bom)
    {
        var path = CreateRollout(padded: true, newline, bom);
        var history = File.ReadAllBytes(HistoryPath);
        foreach (var profile in new[] { "api-a", "api-b", "account", "api-a", "account" })
        {
            // A normal resumed conversation writes a fresh complete settings
            // event before further messages; older reusable files are stale.
            AppendSettingsAndMessage(path, CurrentProvider(), newline);
            var before = File.ReadAllBytes(path);
            var headerLength = HeaderLength(before);
            var previousRecords = ReadRecords(path);
            var lastOrdinal = previousRecords.Last()["ordinal"]!.GetValue<long>();
            var expectedSettings = previousRecords[^2]["payload"]!["thread_settings"]!.DeepClone();

            var result = _service.SwitchProfile(profile);

            Assert.Equal(path, CurrentPath());
            Assert.Single(Directory.GetFiles(SessionsPath, "*.jsonl"));
            var after = File.ReadAllBytes(path);
            Assert.Equal(headerLength, HeaderLength(after));
            Assert.Equal(before.AsSpan(headerLength).ToArray(), after.AsSpan(headerLength, before.Length - headerLength).ToArray());
            Assert.True(after.Length > before.Length);
            var provider = ExpectedProvider(profile);
            var records = ReadRecords(path);
            expectedSettings["model_provider_id"] = provider;
            Assert.Equal(provider, records[0]["payload"]!["model_provider"]!.GetValue<string>());
            Assert.Equal(provider, records[^1]["payload"]!["thread_settings"]!["model_provider_id"]!.GetValue<string>());
            Assert.Equal(ThreadId, records[^1]["payload"]!["thread_id"]!.GetValue<string>());
            Assert.Equal(lastOrdinal + 1, records[^1]["ordinal"]!.GetValue<long>());
            Assert.True(JsonNode.DeepEquals(expectedSettings, records[^1]["payload"]!["thread_settings"]));
            Assert.Equal(history, File.ReadAllBytes(HistoryPath));
            AssertSparseJournal(result.BackupPath, path);
            var unchanged = File.ReadAllBytes(path);
            Assert.True(_service.SwitchProfile(profile).AlreadyActive);
            Assert.Equal(unchanged, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void ShortHeader_FirstCreatesReservedCopy_ThenNormalAppendsAndSwitchesKeepThatCopy()
    {
        var original = CreateRollout(padded: false);
        var originalBytes = File.ReadAllBytes(original);
        var history = File.ReadAllBytes(HistoryPath);

        _service.SwitchProfile("api-a");

        var reserved = CurrentPath();
        Assert.NotEqual(original, reserved);
        Assert.Equal(2, Directory.GetFiles(SessionsPath, "*.jsonl").Length);
        Assert.True(HeaderLength(File.ReadAllBytes(reserved)) >= HeaderLength(originalBytes) + 512);
        foreach (var profile in new[] { "api-b", "account", "api-a", "api-b" })
        {
            AppendSettingsAndMessage(reserved, CurrentProvider());
            var before = File.ReadAllBytes(reserved);
            var headerLength = HeaderLength(before);

            var result = _service.SwitchProfile(profile);

            Assert.Equal(reserved, CurrentPath());
            Assert.Equal(2, Directory.GetFiles(SessionsPath, "*.jsonl").Length);
            Assert.Equal(originalBytes, File.ReadAllBytes(original));
            Assert.Equal(history, File.ReadAllBytes(HistoryPath));
            Assert.Equal(before.AsSpan(headerLength).ToArray(), File.ReadAllBytes(reserved).AsSpan(headerLength, before.Length - headerLength).ToArray());
            AssertSparseJournal(result.BackupPath, reserved);
        }
    }

    [Fact]
    public void LockedAuthAfterPatch_RestoresEveryOriginalRolloutByteAndDatabaseValue()
    {
        var path = CreateRollout(padded: true);
        var before = Snapshot(path, ConfigPath, AuthPath, HistoryPath);

        using (var authLock = File.Open(AuthPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        AssertUnchanged(before);
        Assert.Equal("openai", CurrentProvider());
        Assert.Equal(path, CurrentPath());
        Assert.Single(Directory.GetFiles(SessionsPath));
        var pending = Path.Combine(BackupsPath, "pending");
        var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(pending, "continuity-journal.json")))!;
        Assert.Equal("rolled-back", journal["status"]!.GetValue<string>());
        Assert.Single(journal["routingPatches"]!.AsArray());
        _service.SwitchProfile("api-b");
        Assert.Equal(path, CurrentPath());
        Assert.Equal("provider-beta", CurrentProvider());
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("partial-tail")]
    [InlineData("partial-header")]
    public void InterruptedPatch_RestoresOriginalBytesBeforeContinuing(string interruptedStage)
    {
        var path = CreateRollout(padded: true);
        var before = Snapshot(path, ConfigPath, AuthPath, HistoryPath);
        var result = _service.SwitchProfile("api-a");
        var pending = SimulateInterrupted(result.BackupPath);
        var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(pending, "continuity-journal.json")))!;
        if (interruptedStage != "complete")
        {
            var patch = journal["routingPatches"]![0]!;
            var originalLength = patch["originalLength"]!.GetValue<long>();
            var expectedLength = patch["expectedLength"]!.GetValue<long>();
            using var rollout = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            if (interruptedStage == "partial-tail")
                rollout.SetLength(originalLength + (expectedLength - originalLength) / 2);
            else
            {
                var beforeHeader = File.ReadAllBytes(Path.Combine(pending, patch["headerBefore"]!.GetValue<string>()));
                var afterHeader = File.ReadAllBytes(Path.Combine(pending, patch["headerAfter"]!.GetValue<string>()));
                var changed = Enumerable.Range(0, beforeHeader.Length).Where(index => beforeHeader[index] != afterHeader[index]).ToArray();
                var split = changed[changed.Length / 2];
                rollout.SetLength(originalLength);
                rollout.Position = 0;
                rollout.Write(afterHeader.AsSpan(0, split));
                rollout.Write(beforeHeader.AsSpan(split));
            }
        }

        var recovered = _service.SwitchProfile("account");

        Assert.True(recovered.AlreadyActive);
        AssertUnchanged(before);
        Assert.Equal("openai", CurrentProvider());
        Assert.Equal(path, CurrentPath());
        Assert.False(Directory.Exists(pending));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterruptedPatchWithIndependentTail_RefusesRecoveryWithoutTruncation(bool replaceWithinExpectedLength)
    {
        var path = CreateRollout(padded: true);
        var result = _service.SwitchProfile("api-a");
        var pending = SimulateInterrupted(result.BackupPath);
        if (replaceWithinExpectedLength)
        {
            var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(pending, "continuity-journal.json")))!;
            var originalLength = journal["routingPatches"]![0]!["originalLength"]!.GetValue<long>();
            using var rollout = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
            rollout.SetLength(originalLength);
            rollout.Position = originalLength;
            rollout.Write(Encoding.UTF8.GetBytes("independent new content\n"));
        }
        else AppendSettingsAndMessage(path, CurrentProvider());
        var before = Snapshot(path, ConfigPath, AuthPath, StatePath, HistoryPath);

        Assert.Throws<IOException>(() => _service.SwitchProfile("account"));

        AssertUnchanged(before);
        Assert.Equal("provider-alpha", CurrentProvider());
        Assert.True(Directory.Exists(pending));
        var unchangedJournal = JsonNode.Parse(File.ReadAllText(Path.Combine(pending, "continuity-journal.json")))!;
        Assert.Equal("applying", unchangedJournal["status"]!.GetValue<string>());
    }

    private string CreateRollout(bool padded, string newline = "\n", bool bom = false)
    {
        var path = Path.Combine(SessionsPath, $"rollout-2026-10-09T00-00-00-{ThreadId}.jsonl");
        var header = JsonSerializer.Serialize(new
        {
            ordinal = 100, timestamp = "2026-10-09T00:00:00Z", type = "session_meta",
            payload = new
            {
                id = ThreadId, session_id = SourceId, history_mode = "paginated", model_provider = "openai",
                cwd = "C:/project", originator = "codex", cli_version = "0.154.0",
                history_base = new { thread_id = SourceId, end_ordinal_exclusive = 100, end_byte_offset = 12345 }
            }
        });
        var settings = SettingsRecord(101, "openai").ToJsonString();
        var message = MessageRecord(102, "Original message 中文 " + new string('x', 8192)).ToJsonString();
        File.WriteAllText(path, header + (padded ? new string(' ', 512) : "") + newline + settings + newline + message + newline, new UTF8Encoding(bom));
        using (var state = Open(StatePath))
        using (var insert = state.CreateCommand())
        {
            insert.CommandText = "INSERT INTO threads VALUES ($id, $path, 'openai', 0)";
            insert.Parameters.AddWithValue("$id", ThreadId);
            insert.Parameters.AddWithValue("$path", path);
            insert.ExecuteNonQuery();
        }
        var length = new FileInfo(path).Length;
        var firstLength = HeaderLength(File.ReadAllBytes(path));
        Execute(HistoryPath, $"""
            INSERT INTO thread_history_projection_state VALUES ('{ThreadId}', {length}, 103);
            INSERT INTO thread_turns VALUES ('{ThreadId}', 'turn-1', {firstLength}, {length});
            INSERT INTO thread_items VALUES ('{ThreadId}', 'unchanged cached message');
            """);
        return path;
    }

    private static JsonObject SettingsRecord(long ordinal, string provider) => new()
    {
        ["ordinal"] = ordinal, ["timestamp"] = "2026-10-09T00:00:01Z", ["type"] = "event_msg",
        ["payload"] = new JsonObject
        {
            ["type"] = "thread_settings_applied", ["thread_id"] = ThreadId,
            ["thread_settings"] = new JsonObject
            {
                ["model"] = "gpt-5.4", ["model_provider_id"] = provider, ["approval_policy"] = "never",
                ["approvals_reviewer"] = "user", ["cwd"] = "C:/project", ["reasoning_effort"] = "high",
                ["sandbox_policy"] = new JsonObject { ["type"] = "danger-full-access" },
                ["permission_profile"] = new JsonObject { ["type"] = "builtin", ["name"] = "danger-full-access" },
                ["runtime_workspace_roots"] = new JsonArray("C:/project")
            }
        }
    };

    private static JsonObject MessageRecord(long ordinal, string text) => new()
    {
        ["ordinal"] = ordinal, ["timestamp"] = "2026-10-09T00:00:02Z", ["type"] = "response_item",
        ["payload"] = new JsonObject
        {
            ["type"] = "message", ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = text })
        }
    };

    private static void AppendSettingsAndMessage(string path, string provider, string newline = "\n")
    {
        var ordinal = ReadRecords(path).Last()["ordinal"]!.GetValue<long>();
        File.AppendAllText(path, SettingsRecord(ordinal + 1, provider).ToJsonString() + newline
            + MessageRecord(ordinal + 2, "New actual message " + ordinal).ToJsonString() + newline, new UTF8Encoding(false));
    }

    private static List<JsonNode> ReadRecords(string path) => File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).ToList();
    private static int HeaderLength(byte[] bytes) => Array.IndexOf(bytes, (byte)'\n') + 1;
    private static Dictionary<string, byte[]> Snapshot(params string[] paths) => paths.ToDictionary(path => path, File.ReadAllBytes);
    private static void AssertUnchanged(Dictionary<string, byte[]> before)
    {
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }
    private string CurrentPath() => Scalar(StatePath, "SELECT rollout_path FROM threads");
    private string CurrentProvider() => Scalar(StatePath, "SELECT model_provider FROM threads");
    private static string ExpectedProvider(string profile) => profile == "api-a" ? "provider-alpha" : profile == "api-b" ? "provider-beta" : "openai";

    private static void AssertSparseJournal(string backup, string path)
    {
        var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(backup, "continuity-journal.json")))!;
        var patch = Assert.Single(journal["routingPatches"]!.AsArray());
        Assert.Equal(path, patch!["target"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(Path.Combine(backup, "continuity"), "rollout-*.jsonl"));
        Assert.False(File.Exists(Path.Combine(backup, "continuity", "thread_history_1.sqlite")));
        foreach (var key in new[] { "headerBefore", "headerAfter", "append" })
        {
            var artifact = Path.Combine(backup, patch[key]!.GetValue<string>());
            Assert.True(File.Exists(artifact));
            Assert.True(new FileInfo(artifact).Length < 4096);
        }
    }

    private static string SimulateInterrupted(string completed)
    {
        var pending = Path.Combine(Path.GetDirectoryName(completed)!, "pending");
        Directory.Move(completed, pending);
        var path = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(path))!;
        journal["status"] = "applying";
        File.WriteAllText(path, journal.ToJsonString());
        return pending;
    }

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
    private static string Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private sealed class FakeRuntime : ICodexRuntimeEnvironmentService
    {
        public string? GetOpenAiBaseUrl() => null;
        public void ApplyForProfile(ApiProfileSpec? spec) { }
    }
}

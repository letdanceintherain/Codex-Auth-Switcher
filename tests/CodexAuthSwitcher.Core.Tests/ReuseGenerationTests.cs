using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class ReuseGenerationTests : IDisposable
{
    private const string LogicalId = "11111111-1111-4111-8111-111111111111";
    private const string PhysicalId = "22222222-2222-4222-8222-222222222222";
    private const string BaseId = "33333333-3333-4333-8333-333333333333";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwitcherReuse", Guid.NewGuid().ToString("N"));
    private readonly CodexAuthSwitcherService _service;
    private string StatePath => Path.Combine(_root, "state_5.sqlite");
    private string HistoryPath => Path.Combine(_root, "thread_history_1.sqlite");
    private string Sessions => Path.Combine(_root, "sessions", "2026", "10", "09");

    public ReuseGenerationTests()
    {
        Directory.CreateDirectory(Sessions);
        File.WriteAllText(Path.Combine(_root, "config.toml"), "sqlite_home = '.'\nmodel_provider = \"openai\"\n");
        File.WriteAllText(Path.Combine(_root, "auth.json"), "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"test-account\",\"refresh_token\":\"synthetic-token\"}}");
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        _service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime);
        _service.CaptureCurrentChatGptSnapshot("account");
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api-a", Provider = "provider-a", BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "synthetic-a" });
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api-b", Provider = "provider-b", BaseUrl = "http://127.0.0.1:2/v1", ApiKey = "synthetic-b" });
        Execute(StatePath, "CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, model_provider TEXT, archived INTEGER);");
        Execute(HistoryPath, """
            CREATE TABLE thread_history_projection_state (thread_id TEXT PRIMARY KEY, next_rollout_byte_offset INTEGER, next_rollout_ordinal INTEGER);
            CREATE TABLE thread_turns (thread_id TEXT, turn_id TEXT, rollout_byte_offset INTEGER, rollout_end_byte_offset INTEGER);
            CREATE TABLE thread_items (thread_id TEXT, item_json TEXT);
            """);
    }

    [Fact]
    public void ProviderRoundTrip_WithUnchangedContent_ReusesOriginalAndExistingGenerations()
    {
        var original = CreateOriginal();
        var originalBytes = File.ReadAllBytes(original);
        var historyBefore = File.ReadAllBytes(HistoryPath);
        _service.SwitchProfile("api-a");
        var providerA = CurrentPath();
        _service.SwitchProfile("api-b");
        var providerB = CurrentPath();
        Assert.Equal(3, GenerationCount());
        var generations = new[] { original, providerA, providerB }.ToDictionary(path => path, File.ReadAllBytes);

        foreach (var profile in new[] { "account", "api-a", "api-b", "account", "api-a", "account" })
        {
            var result = _service.SwitchProfile(profile);
            Assert.Equal(profile == "account" ? original : profile == "api-a" ? providerA : providerB, CurrentPath());
            Assert.Equal(3, GenerationCount());
            Assert.Equal(1, result.SynchronizedThreads);
            Assert.False(File.Exists(Path.Combine(result.BackupPath, "continuity", "thread_history_1.sqlite")));
            foreach (var pair in generations) Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key));
        }
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(historyBefore, File.ReadAllBytes(HistoryPath));
    }

    [Fact]
    public void AppendedMessages_RejectStaleGeneration_AndLaterReuseCompleteContent()
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var providerA = CurrentPath();
        var appended = "{\"ordinal\":103,\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"New message after switching\"}]}}\n";
        File.AppendAllText(providerA, appended, new UTF8Encoding(false));
        var completeA = File.ReadAllBytes(providerA);

        _service.SwitchProfile("account");
        var completeOpenAi = CurrentPath();
        Assert.NotEqual(original, completeOpenAi);
        Assert.Equal(3, GenerationCount());
        Assert.Contains("New message after switching", File.ReadAllText(completeOpenAi));

        _service.SwitchProfile("api-a");
        Assert.Equal(providerA, CurrentPath());
        Assert.Equal(3, GenerationCount());
        Assert.Equal(completeA, File.ReadAllBytes(providerA));
    }

    [Theory]
    [InlineData("history_base")]
    [InlineData("session_id")]
    [InlineData("base_instructions")]
    [InlineData("model")]
    public void RoutingRecordOtherFieldsDiffer_RejectsOlderGeneration(string changedField)
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var active = CurrentPath();
        var lines = File.ReadAllLines(active);
        var index = changedField == "model" ? 1 : 0;
        var record = JsonNode.Parse(lines[index])!;
        if (changedField == "history_base") record["payload"]!["history_base"]!["end_byte_offset"] = 9999;
        else if (changedField == "model") record["payload"]!["thread_settings"]!["model"] = "changed-model";
        else record["payload"]![changedField] = "changed metadata must not be discarded";
        lines[index] = record.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(active, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

        _service.SwitchProfile("account");

        Assert.NotEqual(original, CurrentPath());
        Assert.Equal(3, GenerationCount());
        var restored = JsonNode.Parse(File.ReadAllLines(CurrentPath())[index])!;
        if (changedField == "history_base") Assert.Equal(9999, restored["payload"]!["history_base"]!["end_byte_offset"]!.GetValue<int>());
        else if (changedField == "model") Assert.Equal("changed-model", restored["payload"]!["thread_settings"]!["model"]!.GetValue<string>());
        else Assert.Equal("changed metadata must not be discarded", restored["payload"]![changedField]!.GetValue<string>());
    }

    [Fact]
    public void NonRoutingRecordEquivalentJsonWithDifferentBytes_IsNotReused()
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var active = CurrentPath();
        var lines = File.ReadAllLines(active);
        lines[2] = "  " + lines[2] + "  ";
        File.WriteAllText(active, string.Join("\n", lines) + "\n", new UTF8Encoding(false));

        _service.SwitchProfile("account");

        Assert.NotEqual(original, CurrentPath());
        Assert.Equal(lines[2], File.ReadAllLines(CurrentPath())[2]);
        Assert.Equal(3, GenerationCount());
    }

    [Fact]
    public void CandidateWithMatchingHeaderButConflictingLaterProvider_IsNotReused()
    {
        var original = CreateOriginal();
        var lines = File.ReadAllLines(original);
        var settings = JsonNode.Parse(lines[1])!;
        settings["payload"]!["thread_settings"]!["model_provider_id"] = "outdated-provider";
        lines[1] = settings.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(original, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var originalBytes = File.ReadAllBytes(original);
        _service.SwitchProfile("api-a");

        _service.SwitchProfile("account");

        Assert.NotEqual(original, CurrentPath());
        Assert.Equal(3, GenerationCount());
        var rewrittenSettings = JsonNode.Parse(File.ReadAllLines(CurrentPath())[1])!;
        Assert.Equal("openai", rewrittenSettings["payload"]!["thread_settings"]!["model_provider_id"]!.GetValue<string>());
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
    }

    [Fact]
    public void UnreadableOldCandidate_DoesNotPreventCreatingCompleteNewGeneration()
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var active = CurrentPath();
        File.AppendAllText(original, "malformed old record\n", new UTF8Encoding(false));

        _service.SwitchProfile("account");

        Assert.NotEqual(original, CurrentPath());
        Assert.Equal(3, GenerationCount());
        Assert.Equal(File.ReadAllLines(active)[2], File.ReadAllLines(CurrentPath())[2]);
    }

    [Fact]
    public void RoutingJsonFormattingDifferences_CanReuseOriginalWithoutChangingItsCachedOffsets()
    {
        var original = CreateOriginal();
        var lines = File.ReadAllLines(original);
        lines[0] = "  " + lines[0].Replace("\":", "\" : ") + "  ";
        lines[1] = " " + lines[1].Replace("\":", "\" : ") + " ";
        File.WriteAllText(original, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        Execute(HistoryPath, $"UPDATE thread_history_projection_state SET next_rollout_byte_offset={new FileInfo(original).Length};");
        var originalBytes = File.ReadAllBytes(original);
        var cacheBytes = File.ReadAllBytes(HistoryPath);

        _service.SwitchProfile("api-a");
        _service.SwitchProfile("account");

        Assert.Equal(original, CurrentPath());
        Assert.Equal(2, GenerationCount());
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(cacheBytes, File.ReadAllBytes(HistoryPath));
    }

    [Fact]
    public void EquivalentContentFromDifferentCanonicalThread_IsNeverReused()
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var otherThread = "44444444-4444-4444-8444-444444444444";
        var other = Path.Combine(Sessions, $"rollout-2026-10-09T00-00-00-{otherThread}_{Guid.NewGuid()}.jsonl");
        File.Move(original, other);
        var otherBytes = File.ReadAllBytes(other);

        _service.SwitchProfile("account");

        Assert.NotEqual(other, CurrentPath());
        Assert.Contains(LogicalId + "_", Path.GetFileName(CurrentPath()));
        Assert.Equal(otherBytes, File.ReadAllBytes(other));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateIndexedByAnotherThread_IsNotReused(bool archived)
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        using (var state = Open(StatePath))
        using (var insert = state.CreateCommand())
        {
            insert.CommandText = "INSERT INTO threads VALUES($id,$path,'openai',$archived)";
            insert.Parameters.AddWithValue("$id", "44444444-4444-4444-8444-444444444444");
            insert.Parameters.AddWithValue("$path", original);
            insert.Parameters.AddWithValue("$archived", archived ? 1 : 0);
            insert.ExecuteNonQuery();
        }

        _service.SwitchProfile("account");

        Assert.NotEqual(original, CurrentPath());
        Assert.Equal(3, GenerationCount());
    }

    [Fact]
    public void FailedSwitchAfterSelectingReusableGeneration_PreservesBothExistingFilesAndOriginalIndex()
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var providerA = CurrentPath();
        var originalBytes = File.ReadAllBytes(original);
        var providerBytes = File.ReadAllBytes(providerA);
        using (File.Open(Path.Combine(_root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => _service.SwitchProfile("account"));

        Assert.Equal(providerA, CurrentPath());
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(providerBytes, File.ReadAllBytes(providerA));
        Assert.Equal(2, GenerationCount());
    }

    [Fact]
    public void InterruptedSwitchUsingExistingGeneration_RestoresIndexWithoutDeletingReusableHistory()
    {
        var original = CreateOriginal();
        _service.SwitchProfile("api-a");
        var providerA = CurrentPath();
        var originalBytes = File.ReadAllBytes(original);
        var providerBytes = File.ReadAllBytes(providerA);
        var result = _service.SwitchProfile("account");
        Assert.Equal(original, CurrentPath());
        var pending = Path.Combine(Path.GetDirectoryName(result.BackupPath)!, "pending");
        Directory.Move(result.BackupPath, pending);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        Assert.DoesNotContain(journal["files"]!.AsArray(), file => file!["target"]!.GetValue<string>() == original);
        journal["status"] = "applying";
        File.WriteAllText(journalPath, journal.ToJsonString());

        Assert.True(_service.SwitchProfile("api-a").AlreadyActive);

        Assert.Equal(providerA, CurrentPath());
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(providerBytes, File.ReadAllBytes(providerA));
        Assert.Equal(2, GenerationCount());
    }

    private string CreateOriginal()
    {
        var path = Path.Combine(Sessions, $"rollout-2026-10-09T00-00-00-{LogicalId}_{PhysicalId}.jsonl");
        var header = JsonSerializer.Serialize(new
        {
            ordinal = 100,
            type = "session_meta",
            payload = new
            {
                id = LogicalId,
                model_provider = "openai",
                history_mode = "paginated",
                session_id = "preserved-session",
                base_instructions = "preserved-instructions",
                history_base = new { thread_id = BaseId, end_ordinal_exclusive = 100, end_byte_offset = 1000 }
            }
        });
        var settings = JsonSerializer.Serialize(new
        {
            ordinal = 101,
            type = "event_msg",
            payload = new { type = "thread_settings_applied", thread_settings = new { model_provider_id = "openai", model = "unchanged-model" } }
        });
        var message = "{\"ordinal\":102,\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Preserved conversation 中文\"}]}}";
        File.WriteAllText(path, header + "\n" + settings + "\n" + message + "\n", new UTF8Encoding(false));
        using (var state = Open(StatePath))
        using (var insert = state.CreateCommand())
        {
            insert.CommandText = "INSERT INTO threads VALUES($id,$path,'openai',0)";
            insert.Parameters.AddWithValue("$id", LogicalId);
            insert.Parameters.AddWithValue("$path", path);
            insert.ExecuteNonQuery();
        }
        Execute(HistoryPath, $"INSERT INTO thread_history_projection_state VALUES('{PhysicalId}',{new FileInfo(path).Length},103);");
        return path;
    }

    private string CurrentPath()
    {
        using var state = Open(StatePath);
        using var query = state.CreateCommand();
        query.CommandText = "SELECT rollout_path FROM threads WHERE id=$id";
        query.Parameters.AddWithValue("$id", LogicalId);
        return (string)query.ExecuteScalar()!;
    }

    private int GenerationCount() => Directory.GetFiles(Sessions, "*.jsonl").Length;

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

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FakeRuntime : ICodexRuntimeEnvironmentService
    {
        public string? GetOpenAiBaseUrl() => null;
        public void ApplyForProfile(ApiProfileSpec? spec) { }
    }
}

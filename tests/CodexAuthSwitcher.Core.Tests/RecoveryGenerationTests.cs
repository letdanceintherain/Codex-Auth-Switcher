using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class RecoveryGenerationTests : IDisposable
{
    private const string LogicalId = "11111111-1111-4111-8111-111111111111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwitcherRecoveryGeneration", Guid.NewGuid().ToString("N"));
    private readonly CodexAuthSwitcherService _service;
    private readonly string _original;
    private string StatePath => Path.Combine(_root, "state_5.sqlite");
    private string ConfigPath => Path.Combine(_root, "config.toml");
    private string AuthPath => Path.Combine(_root, "auth.json");

    public RecoveryGenerationTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sessions"));
        File.WriteAllText(ConfigPath, "sqlite_home = '.'\nmodel_provider = \"openai\"\n");
        File.WriteAllText(AuthPath, "{\"auth_mode\":\"chatgpt\",\"tokens\":{\"account_id\":\"synthetic-account\",\"refresh_token\":\"synthetic-refresh-token\"}}");
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        _service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime);
        _service.CaptureCurrentChatGptSnapshot("account");
        _service.SaveApiProfile(new ApiProfileSpec { Name = "api", Provider = "provider-a", BaseUrl = "http://127.0.0.1:1/v1", ApiKey = "synthetic-api-key" });
        _original = Path.Combine(_root, "sessions", "rollout-2026-10-09T00-00-00-" + LogicalId + ".jsonl");
        // No complete native settings snapshot: this intentionally exercises
        // the fallback generation, rather than sparse in-place routing.
        File.WriteAllText(_original, JsonSerializer.Serialize(new
        {
            ordinal = 0, type = "session_meta",
            payload = new { id = LogicalId, history_mode = "paginated", model_provider = "openai", cwd = "C:/synthetic-project" }
        }) + "\n" + JsonSerializer.Serialize(new
        {
            ordinal = 1, type = "response_item",
            payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Synthetic preserved conversation" } } }
        }) + "\n", new UTF8Encoding(false));
        using var state = Open(StatePath);
        using var insert = state.CreateCommand();
        insert.CommandText = "CREATE TABLE threads (id TEXT PRIMARY KEY, rollout_path TEXT, model_provider TEXT, archived INTEGER); INSERT INTO threads VALUES($id,$path,'openai',0)";
        insert.Parameters.AddWithValue("$id", LogicalId);
        insert.Parameters.AddWithValue("$path", _original);
        insert.ExecuteNonQuery();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewlyCreatedGenerationWithAdditionalOrChangedConversation_BlocksRecoveryBeforeAnyWrites(bool sameLengthMutation)
    {
        var result = _service.SwitchProfile("api");
        var generated = CurrentPath();
        Assert.NotEqual(_original, generated);
        var pending = MarkInterrupted(result.BackupPath);
        if (sameLengthMutation)
        {
            var bytes = File.ReadAllBytes(generated);
            var marker = Encoding.UTF8.GetBytes("Synthetic preserved conversation");
            var index = IndexOf(bytes, marker);
            Assert.True(index >= 0);
            bytes[index] = (byte)'X';
            File.WriteAllBytes(generated, bytes);
        }
        else File.AppendAllText(generated, "{\"ordinal\":2,\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"Actual new message must survive\"}}\n", new UTF8Encoding(false));
        var live = new[] { generated, _original, ConfigPath, AuthPath, StatePath }.ToDictionary(path => path, File.ReadAllBytes);

        Assert.Throws<IOException>(() => _service.SwitchProfile("account"));

        foreach (var file in live) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(generated, CurrentPath());
        Assert.True(Directory.Exists(pending));
    }

    [Fact]
    public void UntouchedNewGeneration_RecoveryDeletesOnlyGeneratedFileAndRestoresOriginalRoute()
    {
        var original = new[] { _original, ConfigPath, AuthPath }.ToDictionary(path => path, File.ReadAllBytes);
        var result = _service.SwitchProfile("api");
        var generated = CurrentPath();
        Assert.NotEqual(_original, generated);
        MarkInterrupted(result.BackupPath);

        Assert.True(_service.SwitchProfile("account").AlreadyActive);

        Assert.False(File.Exists(generated));
        Assert.Equal(_original, CurrentPath());
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public void PreparedNewGenerationMissingOnDisk_RecoveryStillRestoresOriginalRoute()
    {
        var result = _service.SwitchProfile("api");
        var generated = CurrentPath();
        MarkInterrupted(result.BackupPath);
        File.Delete(generated);

        Assert.True(_service.SwitchProfile("account").AlreadyActive);

        Assert.Equal(_original, CurrentPath());
        Assert.True(File.Exists(_original));
    }

    [Fact]
    public void LegacyJournalWithoutGenerationGuardFields_CanStillRecoverUntouchedGeneration()
    {
        var result = _service.SwitchProfile("api");
        var generated = CurrentPath();
        var pending = MarkInterrupted(result.BackupPath);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        foreach (var file in journal["files"]!.AsArray())
        {
            file!.AsObject().Remove("expectedLength");
            file.AsObject().Remove("expectedHash");
        }
        File.WriteAllText(journalPath, journal.ToJsonString());

        Assert.True(_service.SwitchProfile("account").AlreadyActive);

        Assert.Equal(_original, CurrentPath());
        Assert.False(File.Exists(generated));
    }

    [Fact]
    public void NewGenerationGuardHashMismatch_BlocksBeforeRestoringConfigAuthenticationAndDatabase()
    {
        var result = _service.SwitchProfile("api");
        var generated = CurrentPath();
        var pending = MarkInterrupted(result.BackupPath);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        var generatedEntry = Assert.Single(journal["files"]!.AsArray(), file => file!["target"]!.GetValue<string>() == generated);
        Assert.NotNull(generatedEntry!["expectedLength"]);
        Assert.NotNull(generatedEntry["expectedHash"]);
        generatedEntry["expectedHash"] = new string('0', 64);
        File.WriteAllText(journalPath, journal.ToJsonString());
        var before = new[] { generated, _original, ConfigPath, AuthPath, StatePath }.ToDictionary(path => path, File.ReadAllBytes);

        Assert.Throws<IOException>(() => _service.SwitchProfile("account"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(generated, CurrentPath());
    }

    private static int IndexOf(byte[] source, byte[] marker)
    {
        for (var index = 0; index <= source.Length - marker.Length; index++)
            if (source.AsSpan(index, marker.Length).SequenceEqual(marker)) return index;
        return -1;
    }

    private static string MarkInterrupted(string completed)
    {
        var pending = Path.Combine(Path.GetDirectoryName(completed)!, "pending");
        Directory.Move(completed, pending);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        journal["status"] = "applying";
        File.WriteAllText(journalPath, journal.ToJsonString());
        return pending;
    }

    private string CurrentPath()
    {
        using var state = Open(StatePath);
        using var query = state.CreateCommand();
        query.CommandText = "SELECT rollout_path FROM threads WHERE id=$id";
        query.Parameters.AddWithValue("$id", LogicalId);
        return (string)query.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FakeRuntime : ICodexRuntimeEnvironmentService
    {
        public string? GetOpenAiBaseUrl() => null;
        public void ApplyForProfile(ApiProfileSpec? spec) { }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class ReasoningCompatibilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwitcherReasoning", Guid.NewGuid().ToString("N"));
    private const string Id = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string ParentId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private string Sessions => Path.Combine(_root, "sessions");
    private string Recovery => Path.Combine(_root, "recovery");
    private static string Reasoning => JsonSerializer.Serialize(new
    {
        ordinal = 2, timestamp = "2026-11-12T00:00:00Z", type = "response_item",
        payload = new { type = "reasoning", id = "rs_unportable", summary = new[] { new { type = "summary_text", text = "Visible summary 中文" } },
            encrypted_content = new string('x', 2048) }
    });
    private static string Message => "{\"ordinal\":3,\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\",\"content\":[{\"type\":\"input_text\",\"text\":\"Keep this exactly\"}]}}";
    private static string Header(string? parent = null, long boundary = 0) => JsonSerializer.Serialize(new
    {
        ordinal = 0, type = "session_meta", payload = new { id = Id, history_mode = "paginated", model_provider = "openai",
            history_base = parent is null ? null : new { thread_id = parent, end_byte_offset = boundary, end_ordinal_exclusive = 4 } }
    });

    public ReasoningCompatibilityTests() => Directory.CreateDirectory(Sessions);

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void Detach_PreservesRecordLengthOrdinalSummaryAndLineEnding(string newline, bool bom)
    {
        var bytes = (bom ? Encoding.UTF8.GetPreamble() : []).Concat(Encoding.UTF8.GetBytes(Reasoning + newline)).ToArray();
        var after = ReasoningCompatibilityService.DetachRecord(bytes)!;
        Assert.Equal(bytes.Length, after.Length);
        Assert.True(after.AsSpan(after.Length - newline.Length).SequenceEqual(Encoding.UTF8.GetBytes(newline)));
        using var doc = JsonDocument.Parse(after.AsMemory(bom ? 3 : 0));
        Assert.Equal(2, doc.RootElement.GetProperty("ordinal").GetInt32());
        Assert.Equal("event_msg", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("Visible summary 中文", doc.RootElement.GetProperty("payload").GetProperty("text").GetString());
        Assert.DoesNotContain("encrypted_content", Encoding.UTF8.GetString(after));
        Assert.Null(ReasoningCompatibilityService.DetachRecord(after));
    }

    [Theory]
    [InlineData("message")]
    [InlineData("function_call")]
    [InlineData("function_call_output")]
    [InlineData("custom_tool_call")]
    [InlineData("compaction")]
    public void OtherResponseKinds_AreNeverStrippedEvenWhenEncrypted(string type)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "response_item", payload = new { type, encrypted_content = "must-stay" } }));
        Assert.Null(ReasoningCompatibilityService.DetachRecord(bytes));
    }

    [Fact]
    public void CompactedReplacement_OnlyRemovesOpaqueReasoning_NotOpaqueCompactionOrMessages()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"compacted\",\"payload\":{\"message\":\"retain summary\",\"replacement_history\":[{\"type\":\"reasoning\",\"id\":\"rs_bad\",\"encrypted_content\":\"opaque\",\"summary\":[]},{\"type\":\"compaction\",\"encrypted_content\":\"critical context\"},{\"type\":\"message\",\"role\":\"user\"}]}}\n");
        var after = ReasoningCompatibilityService.DetachRecord(bytes)!;
        Assert.Equal(bytes.Length, after.Length);
        var node = JsonNode.Parse(after)!;
        Assert.Equal(2, node["payload"]!["replacement_history"]!.AsArray().Count);
        Assert.Contains("critical context", Encoding.UTF8.GetString(after));
        Assert.Contains("retain summary", Encoding.UTF8.GetString(after));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SparsePatchAndRollback_PreserveUnchangedBytesIncludingCompressedHistory(bool compressed)
    {
        var bytes = Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n" + Message + "\n");
        var path = Write(bytes, compressed);
        var patch = Assert.Single(ReasoningCompatibilityService.Prepare([path], Recovery));
        ReasoningCompatibilityService.Apply(patch);
        var after = Decoded(path);
        Assert.Equal(bytes.Length, after.Length);
        Assert.EndsWith(Message + "\n", Encoding.UTF8.GetString(after));
        Assert.DoesNotContain("rs_unportable", Encoding.UTF8.GetString(after));
        Assert.True(new FileInfo(patch.RecoveryPath).Length < bytes.Length);
        ReasoningCompatibilityService.ValidateRestore(patch);
        ReasoningCompatibilityService.Restore(patch);
        Assert.Equal(bytes, Decoded(path));
    }

    [Fact]
    public void InheritedPrefix_DoesNotTouchParentRecordsPastForkBoundary()
    {
        var prefix = Encoding.UTF8.GetBytes(Header() + "\n");
        var bytes = prefix.Concat(Encoding.UTF8.GetBytes(Reasoning + "\n")).ToArray();
        var path = Write(bytes, false);
        Assert.Empty(ReasoningCompatibilityService.Prepare(new Dictionary<string, long> { [path] = prefix.Length }, Recovery));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Throws<IOException>(() => ReasoningCompatibilityService.Prepare(new Dictionary<string, long> { [path] = prefix.Length + 5 }, Recovery));
    }

    [Fact]
    public void Recovery_DoesNotTruncateLaterMessages_AndRejectsIndependentRecordChanges()
    {
        var bytes = Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n");
        var path = Write(bytes, false);
        var patch = Assert.Single(ReasoningCompatibilityService.Prepare([path], Recovery));
        ReasoningCompatibilityService.Apply(patch);
        File.AppendAllText(path, Message + "\n");
        ReasoningCompatibilityService.Restore(patch);
        Assert.Equal(bytes.Concat(Encoding.UTF8.GetBytes(Message + "\n")).ToArray(), File.ReadAllBytes(path));
        File.WriteAllBytes(path, bytes);
        ReasoningCompatibilityService.Apply(patch);
        var changed = File.ReadAllBytes(path);
        changed[Encoding.UTF8.GetByteCount(Header() + "\n") + 10] = (byte)'!';
        File.WriteAllBytes(path, changed);
        Assert.Throws<IOException>(() => ReasoningCompatibilityService.Restore(patch));
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public void Apply_RejectsChangesAfterPreparation()
    {
        var path = Write(Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n"), false);
        var patch = Assert.Single(ReasoningCompatibilityService.Prepare([path], Recovery));
        File.AppendAllText(path, Message + "\n");
        var before = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => ReasoningCompatibilityService.Apply(patch));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExplicitRepair_WorksOnAlreadyActiveAccount_IsIdempotent_LeavesArchivesAndCredentialsAlone()
    {
        var service = Service();
        var path = Write(Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n" + Message + "\n"), false);
        Index(path);
        var archive = Path.Combine(_root, "archived_sessions");
        Directory.CreateDirectory(archive);
        var archived = Path.Combine(archive, "archived.jsonl");
        File.Copy(path, archived);
        var originalArchive = File.ReadAllBytes(archived);
        var config = File.ReadAllBytes(Path.Combine(_root, "config.toml"));
        var auth = File.ReadAllBytes(Path.Combine(_root, "auth.json"));
        var result = service.RepairConversationHistory();
        Assert.Equal(1, result.SynchronizedThreads);
        Assert.True(result.RestartRequired);
        var backup = File.ReadAllBytes(Path.Combine(result.BackupPath, "continuity-journal.json"));
        Assert.True(service.RepairConversationHistory().AlreadyActive);
        Assert.Equal(backup, File.ReadAllBytes(Path.Combine(result.BackupPath, "continuity-journal.json")));
        Assert.Equal(config, File.ReadAllBytes(Path.Combine(_root, "config.toml")));
        Assert.Equal(auth, File.ReadAllBytes(Path.Combine(_root, "auth.json")));
        Assert.Equal(originalArchive, File.ReadAllBytes(archived));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(result.BackupPath)!));
    }

    [Fact]
    public void Repair_TraversesCompressedAncestor_AndPreservesForkByteBoundary()
    {
        var service = Service();
        var parentBytes = Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n");
        var parent = Path.Combine(Sessions, $"rollout-{ParentId}.jsonl.zst");
        Compress(parent, parentBytes);
        var path = Write(Encoding.UTF8.GetBytes(Header(ParentId, parentBytes.Length) + "\n" + Message + "\n"), false);
        var before = File.ReadAllBytes(path);
        Index(path);
        Assert.Equal(1, service.RepairConversationHistory().SynchronizedThreads);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(parentBytes.Length, Decoded(parent).Length);
        Assert.DoesNotContain("rs_unportable", Encoding.UTF8.GetString(Decoded(parent)));
    }

    [Fact]
    public void ApiCredentialChange_SameProvider_AutomaticallyDetachesOpaqueReasoning()
    {
        var service = Service();
        var path = Write(Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n"), false);
        Index(path);
        service.SaveApiProfile(new ApiProfileSpec { Name = "other", Provider = "openai", ApiKey = "new-fake-key", BaseUrl = "http://127.0.0.1:1/v1" });
        service.SwitchProfile("other");
        Assert.DoesNotContain("rs_unportable", File.ReadAllText(path));
        Assert.True(service.SwitchProfile("other").AlreadyActive);
    }

    [Fact]
    public void LockedAuth_FailsWithoutLeavingReasoningChanges()
    {
        var service = Service();
        var path = Write(Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n"), false);
        var before = File.ReadAllBytes(path);
        Index(path);
        service.SaveApiProfile(new ApiProfileSpec { Name = "other", Provider = "openai", ApiKey = "new-fake-key", BaseUrl = "http://127.0.0.1:1/v1" });
        using (var auth = File.Open(Path.Combine(_root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => service.SwitchProfile("other"));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void RunningCodex_RepairRefusesBeforeReadingOrWritingHistory()
    {
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        Service();
        var service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime, () => true);
        Assert.Throws<InvalidOperationException>(() => service.RepairConversationHistory());
        Assert.False(Directory.Exists(Path.Combine(_root, "auth-switcher", "backups")));
    }

    [Fact]
    public void InterruptedRepair_IsRecoveredBeforeAnOtherwiseNoOpSwitch()
    {
        var service = Service();
        service.SaveApiProfile(new ApiProfileSpec { Name = "current", Provider = "openai", ApiKey = "old-fake-key", BaseUrl = "http://127.0.0.1:1/v1" });
        service.SwitchProfile("current");
        var original = Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n" + Message + "\n");
        var path = Write(original, false);
        Index(path);
        var result = service.RepairConversationHistory();
        var pending = Path.Combine(Path.GetDirectoryName(result.BackupPath)!, "pending");
        Directory.Move(result.BackupPath, pending);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        journal["status"] = "applying";
        File.WriteAllText(journalPath, journal.ToJsonString());
        Assert.True(service.SwitchProfile("current").AlreadyActive);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(pending));
    }

    [Fact]
    public void PartialReasoningWrite_RestoresOriginalBytes()
    {
        var bytes = Encoding.UTF8.GetBytes(Header() + "\n" + Reasoning + "\n");
        var path = Write(bytes, false);
        var patch = Assert.Single(ReasoningCompatibilityService.Prepare([path], Recovery));
        ReasoningCompatibilityService.Apply(patch);
        var after = File.ReadAllBytes(path);
        var split = Encoding.UTF8.GetByteCount(Header() + "\n") + 120;
        bytes.AsSpan(split).CopyTo(after.AsSpan(split));
        File.WriteAllBytes(path, after);
        ReasoningCompatibilityService.Restore(patch);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void LegacyRoutingAndRepair_RecoveryUsesOriginalWholeFileNotShiftedSparseOffsets()
    {
        var service = Service();
        service.SaveApiProfile(new ApiProfileSpec { Name = "current", Provider = "openai", ApiKey = "old-fake-key", BaseUrl = "http://127.0.0.1:1/v1" });
        service.SwitchProfile("current");
        var header = JsonNode.Parse(Header())!;
        header["payload"]!.AsObject().Remove("history_mode");
        var original = Encoding.UTF8.GetBytes(header.ToJsonString() + "\n" + Reasoning + "\n" + Message + "\n");
        var path = Write(original, false);
        Index(path);
        service.SaveApiProfile(new ApiProfileSpec { Name = "other", Provider = "a-much-longer-provider", ApiKey = "new-fake-key", BaseUrl = "http://127.0.0.1:2/v1" });
        var result = service.SwitchProfile("other");
        Assert.DoesNotContain("rs_unportable", File.ReadAllText(path));
        var pending = Path.Combine(Path.GetDirectoryName(result.BackupPath)!, "pending");
        Directory.Move(result.BackupPath, pending);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        Assert.Empty(journal["reasoningPatches"]!.AsArray());
        journal["status"] = "applying";
        File.WriteAllText(journalPath, journal.ToJsonString());
        Assert.True(service.SwitchProfile("current").AlreadyActive);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    private CodexAuthSwitcherService Service()
    {
        File.WriteAllText(Path.Combine(_root, "config.toml"), "model_provider='openai'\nmodel='synthetic'\n");
        File.WriteAllText(Path.Combine(_root, "auth.json"), "{\"OPENAI_API_KEY\":\"old-fake-key\"}");
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        return new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime);
    }
    private void Index(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state_5.sqlite"), Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "CREATE TABLE threads(id TEXT PRIMARY KEY, rollout_path TEXT, model_provider TEXT, archived INTEGER); INSERT INTO threads VALUES($id,$path,'openai',0)";
        cmd.Parameters.AddWithValue("$id", Id); cmd.Parameters.AddWithValue("$path", path); cmd.ExecuteNonQuery();
    }
    private string Write(byte[] bytes, bool compressed)
    {
        var path = Path.Combine(Sessions, $"rollout-{Id}.jsonl" + (compressed ? ".zst" : ""));
        if (compressed) Compress(path, bytes); else File.WriteAllBytes(path, bytes);
        return path;
    }
    private static void Compress(string path, byte[] bytes)
    {
        using var file = File.Create(path);
        using var stream = new ZstdSharp.CompressionStream(file, 3);
        stream.Write(bytes);
    }
    private static byte[] Decoded(string path)
    {
        using var file = ReasoningCompatibilityService.OpenDecoded(path);
        using var output = new MemoryStream(); file.CopyTo(output); return output.ToArray();
    }
    public void Dispose() => Directory.Delete(_root, true);
    private sealed class FakeRuntime : ICodexRuntimeEnvironmentService
    {
        public string? GetOpenAiBaseUrl() => null;
        public void ApplyForProfile(ApiProfileSpec? spec) { }
    }
}

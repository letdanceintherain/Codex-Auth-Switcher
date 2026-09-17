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
    public void NineSameHeaderFiles_SynchronizeOnlyIndexedCanonicalRollout()
    {
        CreateDatabases();
        var active = CreateRollout("active", "crs", false, "\n", false);
        var sessions = Path.Combine(_root, "sessions");
        var untouched = new Dictionary<string, byte[]>();
        for (var index = 1; index < 9; index++)
        {
            var copy = Path.Combine(sessions, $"active-copy-{index}.jsonl");
            File.Copy(active, copy);
            untouched.Add(copy, File.ReadAllBytes(copy));
        }
        var invalid = Path.Combine(sessions, "unindexed-broken.jsonl");
        var compressed = Path.Combine(sessions, "unindexed-compressed.jsonl.zst");
        File.WriteAllText(invalid, "{invalid unindexed history");
        File.WriteAllText(compressed, "not read");
        untouched.Add(invalid, File.ReadAllBytes(invalid));
        untouched.Add(compressed, File.ReadAllBytes(compressed));
        var message = ReadMessage(active);

        var result = _service.SwitchProfile("api-a");

        AssertProvider(active, "my-provider");
        Assert.Equal(message, ReadMessage(active));
        Assert.Equal("my-provider", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal(new FileInfo(active).Length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        Assert.Equal(1, result.SynchronizedThreads);
        Assert.Single(Directory.GetFiles(Path.Combine(result.BackupPath, "continuity"), "rollout-*.jsonl"));
        foreach (var file in untouched) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Fact]
    public void ForksWithSharedParentHeader_UseDatabaseIdsForIndependentHistoryOffsets()
    {
        CreateDatabases();
        var parent = CreateRollout("parent", "crs", true, "\n", false);
        var first = CreateRollout("fork-a", "crs", false, "\n", false);
        var second = CreateRollout("fork-b", "crs", false, "\n", false);
        var parentBytes = File.ReadAllBytes(parent);
        var parentOffset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'parent'");
        foreach (var path in new[] { first, second })
            File.WriteAllText(path, File.ReadAllText(parent) + File.ReadAllText(path), new UTF8Encoding(false));
        var firstMessage = ReadMessage(first);
        var secondMessage = ReadMessage(second);
        // These cursors intentionally point to different positions: one after
        // the inherited header, one after every inherited and own record.
        Execute(HistoryPath, $"UPDATE thread_history_projection_state SET next_rollout_byte_offset = {FirstLineByteLength(first)} WHERE thread_id = 'fork-a'");
        Execute(HistoryPath, $"UPDATE thread_history_projection_state SET next_rollout_byte_offset = {new FileInfo(second).Length} WHERE thread_id = 'fork-b'");
        var metadata = ReadMetadata();

        var result = _service.SwitchProfile("api-a");

        Assert.Equal(2, result.SynchronizedThreads);
        Assert.Equal(metadata, ReadMetadata());
        Assert.Equal(parentBytes, File.ReadAllBytes(parent));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'parent'"));
        Assert.Equal(parentOffset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'parent'"));
        Assert.Equal(FirstLineByteLength(first), Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'fork-a'"));
        Assert.Equal(new FileInfo(second).Length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'fork-b'"));
        Assert.Equal(firstMessage, ReadMessage(first));
        Assert.Equal(secondMessage, ReadMessage(second));
        foreach (var (path, id) in new[] { (first, "fork-a"), (second, "fork-b") })
        {
            var ids = new List<string>();
            foreach (var line in File.ReadAllLines(path))
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var payload = root.GetProperty("payload");
                if (root.GetProperty("type").GetString() == "session_meta")
                {
                    ids.Add(payload.GetProperty("id").GetString()!);
                    Assert.Equal("my-provider", payload.GetProperty("model_provider").GetString());
                }
                else if (root.GetProperty("type").GetString() == "event_msg")
                    Assert.Equal("my-provider", payload.GetProperty("thread_settings").GetProperty("model_provider_id").GetString());
            }
            Assert.Equal(new[] { "parent", id }, ids);
            Assert.Equal("my-provider", TextScalar(StatePath, $"SELECT model_provider FROM threads WHERE id = '{id}'"));
        }
        Assert.Equal(3L, Scalar(HistoryPath, "SELECT COUNT(*) FROM thread_items"));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void SuffixedCanonicalRollout_ShiftsPhysicalCacheAndTurnOffsets_NotLogicalOriginal(string newline, bool bom)
    {
        const string logicalId = "11111111-1111-4111-8111-111111111111";
        const string physicalId = "22222222-2222-4222-8222-222222222222";
        CreateDatabases();
        var initial = CreateRollout(logicalId, "crs", false, newline, bom);
        var directory = Path.GetDirectoryName(initial)!;
        var canonical = Path.Combine(directory, $"rollout-2026-09-15T00-00-00-{logicalId}_{physicalId}.jsonl");
        var original = Path.Combine(directory, $"rollout-2026-09-15T00-00-00-{logicalId}.jsonl");
        File.Move(initial, canonical);
        // The original is a separate, immutable, longer rollout with the same
        // logical header. Only the suffixed physical rollout is currently used.
        File.WriteAllText(original, File.ReadAllText(canonical) + File.ReadAllLines(canonical).Last() + newline, new UTF8Encoding(bom));
        var originalBytes = File.ReadAllBytes(original);
        var originalLength = new FileInfo(original).Length;
        var originalStart = FirstLineByteLength(original);
        var canonicalLength = new FileInfo(canonical).Length;
        var canonicalStart = FirstLineByteLength(canonical);
        var message = ReadMessage(canonical);
        using (var state = Open(StatePath))
        using (var update = state.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET rollout_path = $path WHERE id = $id";
            update.Parameters.AddWithValue("$path", canonical);
            update.Parameters.AddWithValue("$id", logicalId);
            update.ExecuteNonQuery();
        }
        Execute(HistoryPath, $"""
            UPDATE thread_history_projection_state SET next_rollout_byte_offset = {originalLength} WHERE thread_id = '{logicalId}';
            INSERT INTO thread_history_projection_state VALUES ('{physicalId}', {canonicalLength}, 3);
            INSERT INTO thread_turns (thread_id, turn_id, rollout_ordinal, status, rollout_byte_offset, rollout_end_ordinal, rollout_end_byte_offset)
                VALUES ('{logicalId}', 'old-turn', 1, 'completed', {originalStart}, 4, {originalLength}),
                       ('{physicalId}', 'current-turn', 1, 'completed', {canonicalStart}, 3, {canonicalLength}),
                       ('{physicalId}', 'pending-turn', 4, 'inProgress', NULL, NULL, NULL);
            INSERT INTO thread_items VALUES ('{physicalId}', 'physical rollout message stays unchanged');
            """);
        var turnMetadata = TextScalar(HistoryPath, "SELECT group_concat(thread_id || turn_id || rollout_ordinal || status || coalesce(rollout_end_ordinal, 'NULL'), '|') FROM (SELECT * FROM thread_turns ORDER BY thread_id, turn_id)");

        var result = _service.SwitchProfile("api-a");

        Assert.Equal(1, result.SynchronizedThreads);
        AssertProvider(canonical, "my-provider");
        Assert.Equal(message, ReadMessage(canonical));
        using (var header = JsonDocument.Parse(File.ReadAllLines(canonical)[0]))
            Assert.Equal(logicalId, header.RootElement.GetProperty("payload").GetProperty("id").GetString());
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
        Assert.Equal(originalLength, Scalar(HistoryPath, $"SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = '{logicalId}'"));
        Assert.Equal(originalStart, Scalar(HistoryPath, $"SELECT rollout_byte_offset FROM thread_turns WHERE thread_id = '{logicalId}'"));
        Assert.Equal(originalLength, Scalar(HistoryPath, $"SELECT rollout_end_byte_offset FROM thread_turns WHERE thread_id = '{logicalId}'"));
        Assert.Equal(new FileInfo(canonical).Length, Scalar(HistoryPath, $"SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = '{physicalId}'"));
        Assert.Equal(FirstLineByteLength(canonical), Scalar(HistoryPath, $"SELECT rollout_byte_offset FROM thread_turns WHERE thread_id = '{physicalId}' AND turn_id = 'current-turn'"));
        Assert.Equal(new FileInfo(canonical).Length, Scalar(HistoryPath, $"SELECT rollout_end_byte_offset FROM thread_turns WHERE thread_id = '{physicalId}' AND turn_id = 'current-turn'"));
        Assert.Equal(1L, Scalar(HistoryPath, $"SELECT COUNT(*) FROM thread_turns WHERE thread_id = '{physicalId}' AND turn_id = 'pending-turn' AND rollout_byte_offset IS NULL AND rollout_end_byte_offset IS NULL"));
        Assert.Equal(turnMetadata, TextScalar(HistoryPath, "SELECT group_concat(thread_id || turn_id || rollout_ordinal || status || coalesce(rollout_end_ordinal, 'NULL'), '|') FROM (SELECT * FROM thread_turns ORDER BY thread_id, turn_id)"));
        Assert.Equal("physical rollout message stays unchanged", TextScalar(HistoryPath, $"SELECT item_json FROM thread_items WHERE thread_id = '{physicalId}'"));
        Assert.Single(Directory.GetFiles(Path.Combine(result.BackupPath, "continuity"), "rollout-*.jsonl"));
    }

    [Fact]
    public void OrphanHistoryDatabaseWithoutState_PreventsAllChanges()
    {
        CreateDatabases();
        var rollout = CreateRollout("first", "crs", false, "\n", false);
        File.Delete(StatePath);
        var before = new[] { rollout, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(path => path, File.ReadAllBytes);
        var offset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state");

        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(offset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void TurnOffsetsWithoutProjectionCursor_AreStillShifted()
    {
        CreateDatabases();
        var path = CreateRollout("first", "crs", false, "\n", false);
        Execute(HistoryPath, $"""
            DELETE FROM thread_history_projection_state;
            INSERT INTO thread_turns (thread_id, turn_id, rollout_ordinal, status, rollout_byte_offset, rollout_end_byte_offset)
                VALUES ('first', 'turn', 1, 'completed', {FirstLineByteLength(path)}, {new FileInfo(path).Length});
            """);

        _service.SwitchProfile("api-a");

        Assert.Equal(FirstLineByteLength(path), Scalar(HistoryPath, "SELECT rollout_byte_offset FROM thread_turns"));
        Assert.Equal(new FileInfo(path).Length, Scalar(HistoryPath, "SELECT rollout_end_byte_offset FROM thread_turns"));
        Assert.Equal(0L, Scalar(HistoryPath, "SELECT COUNT(*) FROM thread_history_projection_state"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidTurnOffset_PreventsPartialMigration(bool beyondEnd)
    {
        CreateDatabases();
        var path = CreateRollout("first", "crs", false, "\n", false);
        var length = new FileInfo(path).Length;
        var invalidOffset = beyondEnd ? length + 1 : 1;
        Execute(HistoryPath, $"""
            INSERT INTO thread_turns (thread_id, turn_id, rollout_ordinal, status, rollout_byte_offset, rollout_end_byte_offset)
                VALUES ('first', 'turn', 1, 'completed', {invalidOffset}, {length});
            """);
        var before = new[] { path, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(file => file, File.ReadAllBytes);

        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal(length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        Assert.Equal(invalidOffset, Scalar(HistoryPath, "SELECT rollout_byte_offset FROM thread_turns"));
        Assert.Equal(length, Scalar(HistoryPath, "SELECT rollout_end_byte_offset FROM thread_turns"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PaginatedIndexedRollout_CreatesCanonicalGeneration_KeepingImmutableHistoryAndCaches(bool withSettings)
    {
        const string logicalId = "33333333-3333-4333-8333-333333333333";
        const string physicalId = "44444444-4444-4444-8444-444444444444";
        const string sourceId = "55555555-5555-4555-8555-555555555555";
        CreateDatabases();
        var initial = CreateRollout(logicalId, "crs", false, "\n", false);
        var directory = Path.GetDirectoryName(initial)!;
        var canonical = Path.Combine(directory, $"rollout-2026-09-15T00-00-00-{logicalId}_{physicalId}.jsonl");
        File.Move(initial, canonical);
        var lines = File.ReadAllLines(canonical).Select(line => JsonNode.Parse(line)!).ToList();
        lines[0]["payload"]!["history_mode"] = "paginated";
        lines[0]["payload"]!["history_base"] = new JsonObject
        {
            ["thread_id"] = sourceId,
            ["end_ordinal_exclusive"] = 100,
            ["end_byte_offset"] = 10000
        };
        lines[1]["payload"]!["thread_id"] = sourceId;
        if (!withSettings) lines.RemoveAt(1);
        for (var index = 0; index < lines.Count; index++) lines[index]["ordinal"] = 100 + index;
        File.WriteAllText(canonical, string.Join("\n", lines.Select(line => line.ToJsonString())) + "\n", new UTF8Encoding(false));
        var length = new FileInfo(canonical).Length;
        // An excluded descendant references exact bytes in this physical file.
        // Rewriting any prior record would invalidate its immutable boundary.
        var descendant = Path.Combine(directory, "unindexed-descendant.jsonl");
        var descendantHeader = lines[0].DeepClone();
        descendantHeader["payload"]!["history_base"] = new JsonObject
        {
            ["thread_id"] = physicalId,
            ["end_ordinal_exclusive"] = 100 + lines.Count,
            ["end_byte_offset"] = length
        };
        File.WriteAllText(descendant, descendantHeader.ToJsonString() + "\n", new UTF8Encoding(false));
        using (var state = Open(StatePath))
        using (var update = state.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET rollout_path = $path WHERE id = $id";
            update.Parameters.AddWithValue("$path", canonical);
            update.Parameters.AddWithValue("$id", logicalId);
            update.ExecuteNonQuery();
        }
        Execute(HistoryPath, $"""
            UPDATE thread_history_projection_state SET next_rollout_byte_offset = 10000 WHERE thread_id = '{logicalId}';
            INSERT INTO thread_history_projection_state VALUES ('{physicalId}', {length}, {100 + lines.Count});
            INSERT INTO thread_turns (thread_id, turn_id, rollout_ordinal, status, rollout_byte_offset, rollout_end_ordinal, rollout_end_byte_offset)
                VALUES ('{logicalId}', 'original-turn', 1, 'completed', 200, 100, 10000),
                       ('{physicalId}', 'physical-turn', 101, 'completed', {FirstLineByteLength(canonical)}, {100 + lines.Count}, {length}),
                       ('{physicalId}', 'pending-turn', 104, 'inProgress', NULL, NULL, NULL);
            """);
        var before = new[] { canonical, descendant, HistoryPath }.ToDictionary(path => path, File.ReadAllBytes);
        var metadata = ReadMetadata();
        var message = ReadMessage(canonical);
        var current = canonical;

        foreach (var profile in new[] { "api-a", "account-a", "api-b" })
        {
            _service.SwitchProfile(profile);
            var provider = profile == "api-a" ? "my-provider" : profile == "api-b" ? "second-provider" : "openai";
            Assert.Equal(provider, TextScalar(StatePath, "SELECT model_provider FROM threads"));
            var next = TextScalar(StatePath, "SELECT rollout_path FROM threads");
            Assert.NotEqual(current, next);
            Assert.Equal(directory, Path.GetDirectoryName(next));
            Assert.True(File.Exists(next));
            Assert.StartsWith("rollout-", Path.GetFileName(next));
            Assert.EndsWith(".jsonl", next);
            Assert.Contains(logicalId + "_", Path.GetFileName(next));
            var nextPhysicalId = Path.GetFileNameWithoutExtension(next).Split('_').Last();
            Assert.True(Guid.TryParseExact(nextPhysicalId, "D", out _));
            Assert.NotEqual(logicalId, nextPhysicalId);
            Assert.NotEqual(physicalId, nextPhysicalId);
            Assert.Equal(0L, Scalar(HistoryPath, $"SELECT COUNT(*) FROM thread_history_projection_state WHERE thread_id = '{nextPhysicalId}'"));
            Assert.Equal(0L, Scalar(HistoryPath, $"SELECT COUNT(*) FROM thread_turns WHERE thread_id = '{nextPhysicalId}'"));
            Assert.Equal(message, ReadMessage(next));
            var nextLines = File.ReadAllLines(next);
            Assert.Equal(lines.Count, nextLines.Length);
            for (var index = 0; index < lines.Count; index++)
            {
                var expected = lines[index].DeepClone();
                if (expected["type"]!.GetValue<string>() == "session_meta")
                    expected["payload"]!["model_provider"] = provider;
                else if (expected["type"]!.GetValue<string>() == "event_msg")
                    expected["payload"]!["thread_settings"]!["model_provider_id"] = provider;
                Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(nextLines[index])), $"Unexpected changes to rollout record {index}");
            }
            Assert.Equal(metadata, ReadMetadata());
            foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
            before.Add(next, File.ReadAllBytes(next));
            current = next;
            var fileCount = Directory.GetFiles(directory).Length;
            // Repeating the same provider must not create another generation.
            _service.SwitchProfile(profile);
            Assert.Equal(current, TextScalar(StatePath, "SELECT rollout_path FROM threads"));
            Assert.Equal(fileCount, Directory.GetFiles(directory).Length);
            foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        }
    }

    [Fact]
    public void PaginatedRolloutWithoutState_PreventsAllChanges()
    {
        var path = CreateRollout("first", "crs", false, "\n", false, false);
        var lines = File.ReadAllLines(path);
        var header = JsonNode.Parse(lines[0])!;
        header["payload"]!["history_mode"] = "paginated";
        lines[0] = header.ToJsonString();
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var before = new[] { path, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(file => file, File.ReadAllBytes);

        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void PaginatedFailedAuthWrite_RestoresCanonicalPathWithoutLeavingNewGeneration()
    {
        const string id = "66666666-6666-4666-8666-666666666666";
        CreateDatabases();
        var path = CreateRollout(id, "crs", false, "\n", false);
        var lines = File.ReadAllLines(path);
        var header = JsonNode.Parse(lines[0])!;
        header["payload"]!["history_mode"] = "paginated";
        lines[0] = header.ToJsonString();
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        Execute(HistoryPath, $"UPDATE thread_history_projection_state SET next_rollout_byte_offset = {new FileInfo(path).Length}");
        var before = new[] { path, HistoryPath, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(file => file, File.ReadAllBytes);
        var directory = Path.GetDirectoryName(path)!;
        var originalFiles = Directory.GetFiles(directory).OrderBy(file => file).ToArray();

        using (var authLock = File.Open(Path.Combine(_root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal(path, TextScalar(StatePath, "SELECT rollout_path FROM threads"));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal(originalFiles, Directory.GetFiles(directory).OrderBy(file => file).ToArray());
    }

    [Theory]
    [InlineData("crs", "crs")]
    [InlineData("my-provider", "crs")]
    [InlineData("my-provider", "my-provider")]
    public void RolloutOnlyDuplicateIdentities_StillBlockAmbiguousUpdates(string firstProvider, string copyProvider)
    {
        var active = CreateRollout("active", firstProvider, false, "\n", false, false);
        var copy = CreateRollout("active-copy", copyProvider, false, "\n", false, false);
        var lines = File.ReadAllLines(copy);
        var header = JsonNode.Parse(lines[0])!;
        header["payload"]!["id"] = "active";
        lines[0] = header.ToJsonString();
        File.WriteAllText(copy, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var bytes = File.ReadAllBytes(active);
        var copyBytes = File.ReadAllBytes(copy);
        var auth = File.ReadAllBytes(Path.Combine(_root, "auth.json"));

        var error = Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        Assert.Contains("Duplicate session identity", error.Message);
        Assert.Equal(bytes, File.ReadAllBytes(active));
        Assert.Equal(copyBytes, File.ReadAllBytes(copy));
        Assert.Equal(auth, File.ReadAllBytes(Path.Combine(_root, "auth.json")));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void DistinctDatabaseIdsSharingCanonicalPath_PreventAllChanges()
    {
        CreateDatabases();
        var first = CreateRollout("first", "crs", false, "\n", false);
        var second = CreateRollout("second", "krill", false, "\n", false);
        Execute(StatePath, "UPDATE threads SET rollout_path = (SELECT rollout_path FROM threads WHERE id = 'first') WHERE id = 'second'");
        var before = new[] { first, second, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(path => path, File.ReadAllBytes);
        var firstOffset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'first'");
        var secondOffset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'second'");

        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'first'"));
        Assert.Equal("krill", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'second'"));
        Assert.Equal(firstOffset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'first'"));
        Assert.Equal(secondOffset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'second'"));
    }

    [Fact]
    public void ActiveAndArchivedDatabaseIdsSharingCanonicalPath_PreventAllChanges()
    {
        CreateDatabases();
        var active = CreateRollout("active", "crs", false, "\n", false);
        var archived = CreateRollout("archived", "krill", true, "\n", false);
        Execute(StatePath, "UPDATE threads SET rollout_path = (SELECT rollout_path FROM threads WHERE id = 'active') WHERE id = 'archived'");
        var before = new[] { active, archived, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(path => path, File.ReadAllBytes);
        var activeOffset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'active'");
        var archivedOffset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'archived'");

        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));

        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal("crs", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'active'"));
        Assert.Equal("krill", TextScalar(StatePath, "SELECT model_provider FROM threads WHERE id = 'archived'"));
        Assert.Equal(activeOffset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'active'"));
        Assert.Equal(archivedOffset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = 'archived'"));
    }

    [Fact]
    public void FailedAuthWrite_RestoresRolloutsDatabasesConfigAndHistoryOffsets()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        var bytes = File.ReadAllBytes(path);
        var config = File.ReadAllBytes(Path.Combine(_root, "config.toml"));
        var offset = Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state");
        var turnStart = FirstLineByteLength(path);
        Execute(HistoryPath, $"""
            INSERT INTO thread_turns (thread_id, turn_id, rollout_ordinal, status, rollout_byte_offset, rollout_end_byte_offset)
                VALUES ('first', 'turn', 1, 'completed', {turnStart}, {offset});
            """);
        using (var authLock = File.Open(Path.Combine(_root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(config, File.ReadAllBytes(Path.Combine(_root, "config.toml")));
        Assert.Equal("openai", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal(offset, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        Assert.Equal(turnStart, Scalar(HistoryPath, "SELECT rollout_byte_offset FROM thread_turns"));
        Assert.Equal(offset, Scalar(HistoryPath, "SELECT rollout_end_byte_offset FROM thread_turns"));
        // The rolled-back journal must allow a retry.
        _service.SwitchProfile("api-b");
        AssertProvider(path, "second-provider");
    }

    [Fact]
    public void LockedStateDatabase_PreventsAuthSwitch()
    {
        CreateDatabases();
        CreateRollout("needs-sync", "openai", false, "\n", false);
        var before = File.ReadAllBytes(Path.Combine(_root, "auth.json"));
        using var state = Open(StatePath);
        using var transaction = state.BeginTransaction();
        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "auth.json")));
    }

    [Fact]
    public void InvalidIndexedRollout_PreventsPartialMigration()
    {
        CreateDatabases();
        var first = CreateRollout("first", "crs", false, "\n", false);
        var bytes = File.ReadAllBytes(first);
        var broken = CreateRollout("broken", "crs", false, "\n", false);
        File.WriteAllText(broken, "{invalid");
        Assert.Throws<IOException>(() => _service.SwitchProfile("api-a"));
        Assert.Equal(bytes, File.ReadAllBytes(first));
        Assert.Equal(0L, Scalar(StatePath, "SELECT COUNT(*) FROM threads WHERE model_provider != 'crs'"));
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

    [Fact]
    public void IdenticalProfile_IsReadOnlyEvenWhileCodexIsRunning()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        _service.SwitchProfile("api-a");
        // Formatting differences must not cause a switch or backup.
        var authPath = Path.Combine(_root, "auth.json");
        File.WriteAllText(authPath, "{\"OPENAI_API_KEY\":\"test-a\"}\n");
        var configPath = Path.Combine(_root, "config.toml");
        File.WriteAllText(configPath, File.ReadAllText(configPath).Replace("\"my-provider\"", "'my-provider'"));
        var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .ToDictionary(file => file, file => (File.ReadAllBytes(file), File.GetLastWriteTimeUtc(file)));
        var runtime = new FakeRuntime();
        var inspector = new LiveAuthInspector(runtime);
        var service = new CodexAuthSwitcherService(new ProfileStore(_root, new ProtectedSecretStore(), inspector), inspector, runtime, () => true);
        using var rolloutLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);

        Assert.True(service.IsProfileActive("api-a"));
        var result = service.SwitchProfile("api-a");

        Assert.True(result.AlreadyActive);
        Assert.False(result.RestartRequired);
        Assert.Empty(result.BackupPath);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order());
        foreach (var (file, snapshot) in before)
        {
            Assert.Equal(snapshot.Item2, File.GetLastWriteTimeUtc(file));
            if (file != path) Assert.Equal(snapshot.Item1, File.ReadAllBytes(file));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SameProvider_ChangesOnlyCredentialsOrSettingsWithoutTouchingHistory(bool changeSettings)
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        _service.SwitchProfile("api-a");
        var before = new[] { StatePath, HistoryPath, path }.ToDictionary(file => file, File.ReadAllBytes);
        var spec = _service.LoadApiProfile("api-a")!;
        spec.Name = "same-provider";
        if (changeSettings) { spec.Model = "different-model"; spec.BaseUrl = "https://different.example/v1"; }
        else spec.ApiKey = "different-key";
        _service.SaveApiProfile(spec);
        using (var rolloutLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var databaseLock = File.Open(HistoryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var state = Open(StatePath))
        using (var transaction = state.BeginTransaction())
        {
            var result = _service.SwitchProfile("same-provider");
            Assert.False(result.AlreadyActive);
            Assert.Equal(0, result.SynchronizedThreads);
            var snapshots = Directory.GetFiles(Path.Combine(result.BackupPath, "continuity"));
            Assert.Equal(changeSettings ? "config.toml" : "auth.json", Path.GetFileName(Assert.Single(snapshots)));
        }
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.True(_service.IsProfileActive("same-provider"));
    }

    [Fact]
    public void SwitchingChatGptAccounts_DoesNotBackUpOrReadUnchangedHistories()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        using var rolloutLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = _service.SwitchProfile("account-a");
        Assert.Equal(0, result.SynchronizedThreads);
        Assert.Equal("auth.json", Path.GetFileName(Assert.Single(Directory.GetFiles(Path.Combine(result.BackupPath, "continuity")))));
    }

    [Fact]
    public void RepeatedSwitches_KeepOneRecoveryPointAndNoDuplicateSnapshots()
    {
        CreateDatabases();
        CreateRollout("first", "openai", false, "\n", false);
        foreach (var profile in new[] { "api-a", "api-b", "account-a", "api-a" })
        {
            var result = _service.SwitchProfile(profile);
            Assert.Equal("latest", Path.GetFileName(Assert.Single(Directory.GetDirectories(Path.Combine(_root, "auth-switcher", "backups")))));
            Assert.Single(Directory.GetFiles(result.BackupPath, "config.toml", SearchOption.AllDirectories));
            Assert.Single(Directory.GetFiles(result.BackupPath, "auth.json", SearchOption.AllDirectories));
            var journal = JsonNode.Parse(File.ReadAllText(Path.Combine(result.BackupPath, "continuity-journal.json")))!;
            foreach (var node in journal["files"]!.AsArray())
                Assert.True(File.Exists(Path.Combine(result.BackupPath, node!["backup"]!.GetValue<string>())));
        }
    }

    [Fact]
    public void NewlyUnarchivedConversation_IsSynchronizedEvenWhenProfileMatches()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", true, "\n", false);
        _service.SwitchProfile("api-a");
        var destination = Path.Combine(_root, "sessions", "first.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(path, destination);
        using (var state = Open(StatePath))
        using (var update = state.CreateCommand())
        {
            update.CommandText = "UPDATE threads SET archived=0, rollout_path=$path";
            update.Parameters.AddWithValue("$path", destination);
            update.ExecuteNonQuery();
        }
        Assert.False(_service.IsProfileActive("api-a"));
        var result = _service.SwitchProfile("api-a");
        Assert.False(result.AlreadyActive);
        Assert.Equal(1, result.SynchronizedThreads);
        AssertProvider(destination, "my-provider");
        Assert.Empty(Directory.GetFiles(result.BackupPath, "*.toml", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(result.BackupPath, "auth.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Synchronization_SkipsAlreadyMatchingRolloutAndUnreferencedCopies()
    {
        CreateDatabases();
        var matching = CreateRollout("matching", "my-provider", false, "\n", false);
        var changing = CreateRollout("changing", "openai", false, "\n", false);
        File.WriteAllText(Path.Combine(_root, "sessions", "unreferenced.jsonl"), "invalid unused history");
        using var rolloutLock = File.Open(matching, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = _service.SwitchProfile("api-a");
        Assert.Equal(1, result.SynchronizedThreads);
        AssertProvider(changing, "my-provider");
        Assert.Single(Directory.GetFiles(Path.Combine(result.BackupPath, "continuity"), "rollout-*.jsonl"));
    }

    [Fact]
    public void FailedSwitch_KeepsLastSuccessfulRecoveryPointUntilRetrySucceeds()
    {
        CreateDatabases();
        CreateRollout("first", "openai", false, "\n", false);
        var first = _service.SwitchProfile("api-a");
        var original = Directory.GetFiles(first.BackupPath, "*", SearchOption.AllDirectories).ToDictionary(file => file, File.ReadAllBytes);
        using (var authLock = File.Open(Path.Combine(_root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Throws<IOException>(() => _service.SwitchProfile("api-b"));
        foreach (var file in original) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        _service.SwitchProfile("api-b");
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "auth-switcher", "backups")));
    }

    [Fact]
    public void InterruptedSwitch_AutomaticallyRestoresFilesDatabaseAndAuthBeforeRetry()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        var before = new[] { path, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(file => file, File.ReadAllBytes);
        var result = _service.SwitchProfile("api-a");
        var pending = SimulateInterruptedSwitch(result.BackupPath);

        var retry = _service.SwitchProfile("account-b");

        Assert.True(retry.AlreadyActive);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Equal("openai", TextScalar(StatePath, "SELECT model_provider FROM threads"));
        Assert.Equal(new FileInfo(path).Length, Scalar(HistoryPath, "SELECT next_rollout_byte_offset FROM thread_history_projection_state"));
        Assert.False(Directory.Exists(pending));
    }

    [Fact]
    public void IncompleteRecoverySource_BlocksBeforeRestoringAnyLiveFiles()
    {
        CreateDatabases();
        var path = CreateRollout("first", "openai", false, "\n", false);
        var result = _service.SwitchProfile("api-a");
        var pending = SimulateInterruptedSwitch(result.BackupPath);
        File.Delete(Path.Combine(pending, "continuity", "auth.json"));
        var before = new[] { path, StatePath, HistoryPath, Path.Combine(_root, "config.toml"), Path.Combine(_root, "auth.json") }
            .ToDictionary(file => file, File.ReadAllBytes);
        Assert.Throws<IOException>(() => _service.SwitchProfile("account-b"));
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.True(Directory.Exists(pending));
    }

    [Fact]
    public void SuccessfulSwitchInterruptedDuringPromotion_PreservesAppliedRoute()
    {
        var result = _service.SwitchProfile("api-a");
        var root = Path.GetDirectoryName(result.BackupPath)!;
        Directory.Move(result.BackupPath, Path.Combine(root, "pending"));
        var retry = _service.SwitchProfile("api-a");
        Assert.True(retry.AlreadyActive);
        Assert.Equal("my-provider", _service.GetEnvironment().ModelProvider);
        Assert.Equal("latest", Path.GetFileName(Assert.Single(Directory.GetDirectories(root))));
    }

    [Fact]
    public void AlreadyActiveLegacyAccount_DoesNotMigrateOrRewriteProfileDuringPreflight()
    {
        var profile = Path.Combine(_root, "auth-switcher", "profiles", "account-b");
        var legacy = Path.Combine(profile, "auth.json");
        File.Copy(Path.Combine(_root, "auth.json"), legacy);
        File.Delete(Path.Combine(profile, "auth.bin"));
        Assert.True(_service.IsProfileActive("account-b"));
        Assert.True(_service.SwitchProfile("account-b").AlreadyActive);
        Assert.True(File.Exists(legacy));
        Assert.False(File.Exists(Path.Combine(profile, "auth.bin")));
    }

    [Fact]
    public void PaginatedCrashRecovery_RemovesOnlyNewGenerationAndDoesNotBackUpUnchangedCache()
    {
        const string id = "77777777-7777-4777-8777-777777777777";
        CreateDatabases();
        var path = CreateRollout(id, "openai", false, "\n", false);
        var lines = File.ReadAllLines(path);
        var header = JsonNode.Parse(lines[0])!;
        header["payload"]!["history_mode"] = "paginated";
        lines[0] = header.ToJsonString();
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var original = File.ReadAllBytes(path);
        var history = File.ReadAllBytes(HistoryPath);
        var result = _service.SwitchProfile("api-a");
        var replacement = TextScalar(StatePath, "SELECT rollout_path FROM threads");
        Assert.NotEqual(path, replacement);
        Assert.False(File.Exists(Path.Combine(result.BackupPath, "continuity", "thread_history_1.sqlite")));
        SimulateInterruptedSwitch(result.BackupPath);

        Assert.True(_service.SwitchProfile("account-b").AlreadyActive);
        Assert.Equal(path, TextScalar(StatePath, "SELECT rollout_path FROM threads"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(history, File.ReadAllBytes(HistoryPath));
        Assert.False(File.Exists(replacement));
    }

    [Fact]
    public void LegacyRecovery_IsCheckedOnceAndOldCheckpointsArePreserved()
    {
        var result = _service.SwitchProfile("api-a");
        var root = Path.GetDirectoryName(result.BackupPath)!;
        var legacy = Path.Combine(root, "20260917_120000_000");
        Directory.Move(result.BackupPath, legacy);
        File.Delete(Path.Combine(root, "recovery-v2"));
        var journalPath = Path.Combine(legacy, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        journal["status"] = "applying";
        foreach (var node in journal["files"]!.AsArray())
            node!["backup"] = Path.GetFullPath(node["backup"]!.GetValue<string>(), legacy);
        File.WriteAllText(journalPath, journal.ToJsonString());

        Assert.True(_service.SwitchProfile("account-b").AlreadyActive);
        Assert.Equal("rolled-back", JsonNode.Parse(File.ReadAllText(journalPath))!["status"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(root, "recovery-v2")));
        File.WriteAllText(journalPath, "old journal deliberately unreadable after one-time migration");
        _service.SwitchProfile("api-a");
        _service.SwitchProfile("api-b");
        Assert.True(Directory.Exists(legacy));
        Assert.Equal("old journal deliberately unreadable after one-time migration", File.ReadAllText(journalPath));
        Assert.Equal(new[] { "20260917_120000_000", "latest" }, Directory.GetDirectories(root).Select(Path.GetFileName).Order());
    }

    private static string SimulateInterruptedSwitch(string completedPath)
    {
        var pending = Path.Combine(Path.GetDirectoryName(completedPath)!, "pending");
        Directory.Move(completedPath, pending);
        var journalPath = Path.Combine(pending, "continuity-journal.json");
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        journal["status"] = "applying";
        File.WriteAllText(journalPath, journal.ToJsonString());
        return pending;
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
            CREATE TABLE thread_turns (
                thread_id TEXT NOT NULL, turn_id TEXT NOT NULL, rollout_ordinal INTEGER NOT NULL,
                status TEXT NOT NULL, error_json TEXT, started_at INTEGER, completed_at INTEGER,
                duration_ms INTEGER, first_user_item_id TEXT, final_agent_item_id TEXT,
                rollout_byte_offset INTEGER, rollout_end_ordinal INTEGER, rollout_end_byte_offset INTEGER,
                PRIMARY KEY (thread_id, turn_id));
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
    private static long FirstLineByteLength(string path) => Array.IndexOf(File.ReadAllBytes(path), (byte)'\n') + 1L;
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

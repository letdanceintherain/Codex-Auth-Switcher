using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexAuthSwitcher.Core.Services;

namespace CodexAuthSwitcher.Core.Tests;

public sealed class FixedWidthRoutingServiceTests : IDisposable
{
    private const string LogicalId = "11111111-1111-4111-8111-111111111111";
    private const string ParentId = "22222222-2222-4222-8222-222222222222";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SwitcherFixedRouting", Guid.NewGuid().ToString("N"));
    private readonly JsonSerializerOptions _json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private string Recovery => Path.Combine(_root, "recovery");

    public FixedWidthRoutingServiceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\r\n", true)]
    public void Apply_PreservesEveryExistingBoundaryAndAppendsCompleteTargetSettings(string newline, bool bom)
    {
        var path = CreateHistory(newline: newline, bom: bom);
        var before = File.ReadAllBytes(path);
        var headerLength = HeaderLength(before);
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "中转商-乙", Recovery));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(headerLength, patch.HeaderLength);
        Assert.Equal(before.Length, patch.OriginalLength);

        FixedWidthRoutingService.Apply(patch);

        var after = File.ReadAllBytes(path);
        Assert.Equal(patch.ExpectedLength, after.Length);
        Assert.Equal(before[headerLength..], after[headerLength..before.Length]);
        Assert.Equal(bom, after.AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        Assert.Equal(Encoding.UTF8.GetBytes(newline), after[(headerLength - newline.Length)..headerLength]);
        var header = Header(after);
        Assert.Equal("中转商-乙", header["payload"]!["model_provider"]!.GetValue<string>());
        Assert.Equal("原始中文指令与 emoji 🌧", header["payload"]!["base_instructions"]!.GetValue<string>());
        Assert.Equal(ParentId, header["payload"]!["history_base"]!["thread_id"]!.GetValue<string>());
        var appended = JsonNode.Parse(Encoding.UTF8.GetString(after[before.Length..]))!;
        Assert.Equal(8, appended["ordinal"]!.GetValue<int>());
        Assert.Equal(LogicalId, appended["payload"]!["thread_id"]!.GetValue<string>());
        var expected = Settings().DeepClone();
        expected["model_provider_id"] = "中转商-乙";
        Assert.True(JsonNode.DeepEquals(expected, appended["payload"]!["thread_settings"]));
        FixedWidthRoutingService.ValidateRestore(patch);
        FixedWidthRoutingService.Restore(patch);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 50)]
    [InlineData(3, 100)]
    public void Restore_PartialHeaderOrAppendWrite_RestoresExactOriginalBytes(int headerStage, int appendPercent)
    {
        var path = CreateHistory(bom: true, newline: "\r\n");
        var original = File.ReadAllBytes(path);
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        var before = File.ReadAllBytes(patch.HeaderBeforePath);
        var after = File.ReadAllBytes(patch.HeaderAfterPath);
        var append = File.ReadAllBytes(patch.AppendPath);
        var differences = Enumerable.Range(0, before.Length).Where(index => before[index] != after[index]).ToArray();
        Assert.NotEmpty(differences);
        var writtenHeader = headerStage switch { 0 => 0, 1 => differences[0] + 1, 2 => differences[^1], _ => after.Length };
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Write(after.AsSpan(0, writtenHeader));
            stream.Position = patch.OriginalLength;
            stream.Write(append.AsSpan(0, append.Length * appendPercent / 100));
        }
        var partial = File.ReadAllBytes(path);

        FixedWidthRoutingService.ValidateRestore(patch);
        Assert.Equal(partial, File.ReadAllBytes(path));
        FixedWidthRoutingService.Restore(patch);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdditionalOrUnexpectedAppend_BlocksRecoveryWithoutChangingConversation(bool beyondExpectedLength)
    {
        var path = CreateHistory();
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        if (beyondExpectedLength)
        {
            FixedWidthRoutingService.Apply(patch);
            File.AppendAllText(path, "extra real conversation\n", new UTF8Encoding(false));
        }
        else File.AppendAllText(path, "unexpected content\n", new UTF8Encoding(false));
        var changed = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() => FixedWidthRoutingService.ValidateRestore(patch));
        Assert.Equal(changed, File.ReadAllBytes(path));
        Assert.Throws<IOException>(() => FixedWidthRoutingService.Restore(patch));
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnrelatedHeaderMutation_BlocksRecoveryBeforeTruncatingAppend()
    {
        var path = CreateHistory();
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        FixedWidthRoutingService.Apply(patch);
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) stream.WriteByte((byte)'!');
        var changed = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() => FixedWidthRoutingService.ValidateRestore(patch));
        Assert.Throws<IOException>(() => FixedWidthRoutingService.Restore(patch));
        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Fact]
    public void ApplyBlockedBeforeAnyWrite_ReadOnlyLockedSourceNeedsNoRestoration()
    {
        var path = CreateHistory();
        var original = File.ReadAllBytes(path);
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        using var locked = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Throws<IOException>(() => FixedWidthRoutingService.Apply(patch));
        FixedWidthRoutingService.ValidateRestore(patch);
        FixedWidthRoutingService.Restore(patch);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Restore_DoesNotOverwriteMiddleConversationBytes()
    {
        var path = CreateHistory();
        var original = File.ReadAllBytes(path);
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        FixedWidthRoutingService.Apply(patch);
        var bodyOffset = patch.HeaderLength + 5;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = bodyOffset;
            stream.WriteByte((byte)'X');
        }
        original[bodyOffset] = (byte)'X';

        FixedWidthRoutingService.Restore(patch);

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversationChangedAfterPreparation_ApplyRejectsWithoutWrites(bool append)
    {
        var path = CreateHistory();
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        if (append) File.AppendAllText(path, "new chat\n", new UTF8Encoding(false));
        else using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite)) stream.WriteByte((byte)'!');
        var changed = File.ReadAllBytes(path);

        Assert.Throws<IOException>(() => FixedWidthRoutingService.Apply(patch));

        Assert.Equal(changed, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData("model")]
    [InlineData("model_provider_id")]
    [InlineData("approval_policy")]
    [InlineData("execution_context")]
    [InlineData("settings_record")]
    public void MissingCompleteNativeSettings_FallsBackWithoutWritingRecoveryFiles(string missing)
    {
        var path = CreateHistory();
        var lines = File.ReadAllLines(path);
        if (missing == "settings_record") lines = lines.Where(line => !line.Contains("thread_settings_applied")).ToArray();
        else
        {
            var record = JsonNode.Parse(lines[1])!;
            var settings = record["payload"]!["thread_settings"]!.AsObject();
            if (missing == "execution_context") { settings.Remove("sandbox_policy"); settings.Remove("cwd"); }
            else settings.Remove(missing);
            lines[1] = record.ToJsonString(_json);
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        var original = File.ReadAllBytes(path);

        Assert.Null(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Recovery));
    }

    [Theory]
    [InlineData("missing_ordinal")]
    [InlineData("descending_ordinal")]
    [InlineData("max_ordinal")]
    [InlineData("second_metadata")]
    [InlineData("no_final_newline")]
    [InlineData("malformed_tail")]
    public void UnsupportedHistory_FallsBackWithoutTouchingSource(string problem)
    {
        var path = CreateHistory();
        var lines = File.ReadAllLines(path);
        if (problem == "malformed_tail") lines[^1] = "malformed tail";
        else if (problem != "no_final_newline")
        {
            var record = JsonNode.Parse(lines[^1])!;
            if (problem == "missing_ordinal") record.AsObject().Remove("ordinal");
            if (problem == "descending_ordinal") record["ordinal"] = 2;
            if (problem == "max_ordinal") record["ordinal"] = long.MaxValue;
            if (problem == "second_metadata") record["type"] = "session_meta";
            lines[^1] = record.ToJsonString(_json);
        }
        File.WriteAllText(path, string.Join("\n", lines) + (problem == "no_final_newline" ? "" : "\n"), new UTF8Encoding(false));
        var original = File.ReadAllBytes(path);

        Assert.Null(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Recovery));
    }

    [Fact]
    public void LongProviderWithoutHeaderCapacity_FallsBack_ThenReservedGenerationCanPatch()
    {
        var path = CreateHistory(padding: 0);
        var original = File.ReadAllBytes(path);
        var longProvider = new string('中', 150);
        Assert.Null(FixedWidthRoutingService.TryPrepare(path, LogicalId, longProvider, Recovery));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(Directory.Exists(Recovery));

        FixedWidthRoutingService.ReserveHeaderSpace(path, longProvider);
        var reserved = File.ReadAllBytes(path);
        Assert.True(HeaderLength(reserved) >= HeaderLength(original) + 512);
        Assert.Equal(original[HeaderLength(original)..], reserved[HeaderLength(reserved)..]);
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, longProvider + "续", Recovery));
        FixedWidthRoutingService.Apply(patch);
        Assert.Equal(longProvider + "续", Header(File.ReadAllBytes(path))["payload"]!["model_provider"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    public void HeaderReserve_MovesLargeTailExactlyAndPreservesBomAndNewline(string newline, bool bom)
    {
        var path = CreateHistory(padding: 0, newline: newline, bom: bom);
        File.AppendAllText(path, new string('x', 180_000) + newline, new UTF8Encoding(false));
        var original = File.ReadAllBytes(path);
        var headerLength = HeaderLength(original);

        FixedWidthRoutingService.ReserveHeaderSpace(path, "new-provider");

        var reserved = File.ReadAllBytes(path);
        var newHeaderLength = HeaderLength(reserved);
        Assert.True(newHeaderLength > headerLength);
        Assert.Equal(original[headerLength..], reserved[newHeaderLength..]);
        Assert.Equal(bom, reserved.AsSpan().StartsWith(new byte[] { 239, 187, 191 }));
        Assert.Equal(Encoding.UTF8.GetBytes(newline), reserved[(newHeaderLength - newline.Length)..newHeaderLength]);
        var metadata = Header(reserved);
        Assert.Equal("new-provider", metadata["payload"]!["model_provider"]!.GetValue<string>());
        Assert.Equal("原始中文指令与 emoji 🌧", metadata["payload"]!["base_instructions"]!.GetValue<string>());
    }

    [Fact]
    public void ForkWithInheritedMetadataId_KeepsSourceIdentityAndTargetsLogicalSettingsId()
    {
        var path = CreateHistory();
        var bytes = File.ReadAllBytes(path);
        var headerLength = HeaderLength(bytes);
        var header = Header(bytes);
        header["payload"]!["id"] = ParentId;
        var changedHeader = Encoding.UTF8.GetBytes(header.ToJsonString(_json) + "\n");
        var rewritten = changedHeader.Concat(bytes[headerLength..]).ToArray();
        File.WriteAllBytes(path, rewritten);
        FixedWidthRoutingService.ReserveHeaderSpace(path, "provider-a");
        var original = File.ReadAllBytes(path);
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "provider-b", Recovery));

        FixedWidthRoutingService.Apply(patch);

        var after = File.ReadAllBytes(path);
        Assert.Equal(ParentId, Header(after)["payload"]!["id"]!.GetValue<string>());
        var appended = JsonNode.Parse(Encoding.UTF8.GetString(after[original.Length..]))!;
        Assert.Equal(LogicalId, appended["payload"]!["thread_id"]!.GetValue<string>());
        Assert.Equal(original[patch.HeaderLength..], after[patch.HeaderLength..original.Length]);
    }

    [Fact]
    public void DescendantHistoryBaseCutoff_RemainsSameValidOrdinalAndByteBoundary()
    {
        var path = CreateHistory();
        var original = File.ReadAllBytes(path);
        var cutoff = original.Length;
        var patch = Assert.IsType<RoutingPatch>(FixedWidthRoutingService.TryPrepare(path, LogicalId, "target-provider", Recovery));
        var child = new JsonObject { ["history_base"] = new JsonObject { ["thread_id"] = LogicalId, ["end_ordinal_exclusive"] = 8, ["end_byte_offset"] = cutoff } };

        FixedWidthRoutingService.Apply(patch);

        var after = File.ReadAllBytes(path);
        Assert.Equal(cutoff, child["history_base"]!["end_byte_offset"]!.GetValue<int>());
        Assert.Equal((byte)'\n', after[cutoff - 1]);
        Assert.Equal(original[patch.HeaderLength..], after[patch.HeaderLength..cutoff]);
        var inherited = Encoding.UTF8.GetString(after[..cutoff]).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(new long[] { 0, 4, 7 }, inherited.Select(record => record["ordinal"]!.GetValue<long>()));
        var appended = JsonNode.Parse(Encoding.UTF8.GetString(after[cutoff..]))!;
        Assert.Equal(child["history_base"]!["end_ordinal_exclusive"]!.GetValue<int>(), appended["ordinal"]!.GetValue<int>());
    }

    private string CreateHistory(string newline = "\n", bool bom = false, int padding = 600)
    {
        var path = Path.Combine(_root, "rollout-2026-10-09T00-00-00-" + LogicalId + ".jsonl");
        var header = new JsonObject
        {
            ["ordinal"] = 0,
            ["type"] = "session_meta",
            ["payload"] = new JsonObject
            {
                ["id"] = LogicalId,
                ["history_mode"] = "paginated",
                ["model_provider"] = "provider-a",
                ["base_instructions"] = "原始中文指令与 emoji 🌧",
                ["history_base"] = new JsonObject { ["thread_id"] = ParentId, ["end_ordinal_exclusive"] = 0, ["end_byte_offset"] = 123 },
                ["dynamic_tools"] = new JsonArray(new JsonObject { ["name"] = "保留工具", ["input_schema"] = new JsonObject { ["type"] = "object" } })
            }
        };
        var settings = new JsonObject
        {
            ["ordinal"] = 4, ["type"] = "event_msg", ["timestamp"] = "2026-10-09T00:00:00Z",
            ["payload"] = new JsonObject { ["type"] = "thread_settings_applied", ["thread_id"] = ParentId, ["thread_settings"] = Settings() }
        };
        var message = new JsonObject
        {
            ["ordinal"] = 7, ["type"] = "response_item",
            ["payload"] = new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "原有聊天内容保持字节不变" }) }
        };
        File.WriteAllText(path, header.ToJsonString(_json) + new string(' ', padding) + newline
            + settings.ToJsonString(_json) + newline + message.ToJsonString(_json) + newline, new UTF8Encoding(bom));
        return path;
    }

    private static JsonObject Settings() => new()
    {
        ["model"] = "gpt-5.4", ["model_provider_id"] = "provider-a", ["approval_policy"] = "on-request",
        ["cwd"] = "C:/合成项目", ["sandbox_policy"] = new JsonObject { ["type"] = "workspace-write", ["writable_roots"] = new JsonArray("C:/合成项目") },
        ["model_reasoning_effort"] = "high", ["model_reasoning_summary"] = "auto",
        ["unknown_future_field"] = new JsonObject { ["preserved"] = true }
    };

    private static int HeaderLength(byte[] bytes) => Array.IndexOf(bytes, (byte)'\n') + 1;
    private static JsonNode Header(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        return JsonNode.Parse(Encoding.UTF8.GetString(bytes, offset, HeaderLength(bytes) - offset))!;
    }

    public void Dispose() => Directory.Delete(_root, true);
}

using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAuthSwitcher.Core.Services;

public sealed record ReasoningPatch(string Path, long OriginalLength, string RecoveryPath, int RecordCount);

/// <summary>
/// Detaches opaque reasoning from replay without changing message/tool records,
/// physical byte offsets, ordinals or thread identities. Only use with Codex closed.
/// </summary>
public static class ReasoningCompatibilityService
{
    private sealed record Segment(long Offset, byte[] Before, byte[] After);
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Returns null for every non-reasoning record, including opaque compaction:
    // a compaction can be the ONLY remaining context and must never be discarded.
    public static byte[]? DetachRecord(byte[] line)
    {
        var bom = line.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        var end = line.Length;
        while (end > bom && line[end - 1] is 10 or 13) end--;
        if (end == bom) return null;
        var first = bom;
        while (first < end && line[first] is 32 or 9) first++;
        if (first == end) return null;
        using var document = JsonDocument.Parse(line.AsMemory(bom, end - bom));
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type) || !root.TryGetProperty("payload", out var payload)) return null;
        JsonNode? replacement = null;
        if (type.GetString() == "response_item" && IsOpaqueReasoning(payload))
        {
            replacement = JsonNode.Parse(root.GetRawText())!;
            var summaries = payload.TryGetProperty("summary", out var summary) && summary.ValueKind == JsonValueKind.Array
                ? summary.EnumerateArray().Where(item => item.TryGetProperty("text", out _))
                    .Select(item => item.GetProperty("text").GetString()) : [];
            // A native display-only event is ignored by model-context replay.
            // Keep the visible summary; the native item_completed UI event is untouched.
            replacement["type"] = "event_msg";
            replacement["payload"] = new JsonObject { ["type"] = "agent_reasoning", ["text"] = string.Join("\n", summaries) };
        }
        else if (type.GetString() == "compacted" && payload.TryGetProperty("replacement_history", out var history)
                 && history.ValueKind == JsonValueKind.Array && history.EnumerateArray().Any(IsOpaqueReasoning))
        {
            replacement = JsonNode.Parse(root.GetRawText())!;
            var items = replacement["payload"]!["replacement_history"]!.AsArray();
            for (var i = items.Count - 1; i >= 0; i--)
                if (IsOpaqueReasoning(history[i])) items.RemoveAt(i);
        }
        if (replacement is null) return null;
        var encoded = JsonSerializer.SerializeToUtf8Bytes(replacement, Json);
        if (encoded.Length > end - bom)
            throw new IOException("Reasoning compatibility record cannot preserve its original byte boundary.");
        var result = new byte[line.Length];
        line.AsSpan(0, bom).CopyTo(result);
        encoded.CopyTo(result.AsSpan(bom));
        result.AsSpan(bom + encoded.Length, end - bom - encoded.Length).Fill((byte)' ');
        line.AsSpan(end).CopyTo(result.AsSpan(end));
        return result;
    }

    private static bool IsOpaqueReasoning(JsonElement item) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("type", out var type) && type.GetString() == "reasoning"
        && item.TryGetProperty("encrypted_content", out var encrypted)
        && encrypted.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(encrypted.GetString());

    public static List<ReasoningPatch> Prepare(IEnumerable<string> paths, string recoveryDirectory) =>
        Prepare(paths.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(path => path, _ => long.MaxValue,
            StringComparer.OrdinalIgnoreCase), recoveryDirectory);

    public static List<ReasoningPatch> Prepare(IReadOnlyDictionary<string, long> paths, string recoveryDirectory)
    {
        var patches = new List<ReasoningPatch>();
        foreach (var (path, limit) in paths)
        {
            using var input = OpenDecoded(path);
            var segments = new List<Segment>();
            long offset = 0;
            foreach (var line in ReadLines(input))
            {
                if (offset < limit && offset + line.Length > limit)
                    throw new IOException("Inherited history ends inside a JSONL record.");
                var after = offset < limit ? DetachRecord(line) : null;
                if (after is not null) segments.Add(new Segment(offset, line, after));
                offset += line.Length;
            }
            if (limit != long.MaxValue && limit > offset)
                throw new IOException("Inherited history byte boundary exceeds its parent file.");
            if (segments.Count == 0) continue;
            Directory.CreateDirectory(recoveryDirectory);
            var backup = System.IO.Path.Combine(recoveryDirectory, Guid.NewGuid().ToString("N") + ".reasoning.gz");
            using (var output = File.Create(backup))
            using (var compressed = new GZipStream(output, CompressionLevel.Optimal))
                JsonSerializer.Serialize(compressed, segments);
            patches.Add(new ReasoningPatch(path, offset, backup, segments.Count));
        }
        return patches;
    }

    public static void Apply(ReasoningPatch patch)
    {
        var segments = ReadPatch(patch);
        if (IsCompressed(patch.Path)) { RewriteCompressed(patch, segments, restore: false); return; }
        using var file = new FileStream(patch.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length != patch.OriginalLength) throw new IOException("Conversation changed after reasoning repair preparation.");
        foreach (var segment in segments)
            if (!Read(file, segment).AsSpan().SequenceEqual(segment.Before))
                throw new IOException("Reasoning record changed after repair preparation.");
        foreach (var segment in segments) { file.Position = segment.Offset; file.Write(segment.After); }
        file.Flush(true);
    }

    public static void ValidateRestore(ReasoningPatch patch)
    {
        using var file = OpenSeekable(patch.Path);
        ValidateRestore(file, patch, ReadPatch(patch));
    }

    public static void Restore(ReasoningPatch patch)
    {
        var segments = ReadPatch(patch);
        if (IsCompressed(patch.Path)) { RewriteCompressed(patch, segments, restore: true); return; }
        using (var unchanged = new FileStream(patch.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            if (segments.All(segment => Read(unchanged, segment).AsSpan().SequenceEqual(segment.Before))) return;
        using var file = new FileStream(patch.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        ValidateRestore(file, patch, segments);
        foreach (var segment in segments) { file.Position = segment.Offset; file.Write(segment.Before); }
        // Never truncate independently appended messages.
        file.Flush(true);
    }

    private static void ValidateRestore(Stream file, ReasoningPatch patch, List<Segment> segments)
    {
        if (file.Length < patch.OriginalLength) throw new IOException("Conversation was truncated after reasoning repair.");
        foreach (var segment in segments)
        {
            var current = Read(file, segment);
            var prefix = 0;
            while (prefix < current.Length && current[prefix] == segment.After[prefix]) prefix++;
            if (!current.AsSpan(prefix).SequenceEqual(segment.Before.AsSpan(prefix)))
                throw new IOException("Reasoning record changed independently; recovery will not overwrite it.");
        }
    }

    private static List<Segment> ReadPatch(ReasoningPatch patch)
    {
        using var file = File.OpenRead(patch.RecoveryPath);
        using var compressed = new GZipStream(file, CompressionMode.Decompress);
        var segments = JsonSerializer.Deserialize<List<Segment>>(compressed) ?? throw new IOException("Missing reasoning recovery records.");
        long end = 0;
        foreach (var segment in segments)
        {
            if (segment.Offset < end || segment.Before.Length == 0 || segment.Before.Length != segment.After.Length)
                throw new IOException("Invalid reasoning recovery byte boundaries.");
            end = checked(segment.Offset + segment.Before.Length);
            if (end > patch.OriginalLength) throw new IOException("Invalid reasoning recovery file length.");
        }
        if (segments.Count != patch.RecordCount) throw new IOException("Invalid reasoning recovery record count.");
        return segments;
    }

    private static byte[] Read(Stream file, Segment segment)
    {
        file.Position = segment.Offset;
        var data = new byte[segment.Before.Length];
        file.ReadExactly(data);
        return data;
    }

    public static Stream OpenDecoded(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!IsCompressed(path)) return file;
        try { return new ZstdSharp.DecompressionStream(file, leaveOpen: false); }
        catch { file.Dispose(); throw; }
    }

    private static bool IsCompressed(string path) => path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase);

    private static Stream OpenSeekable(string path)
    {
        if (!IsCompressed(path)) return OpenDecoded(path);
        using var input = OpenDecoded(path);
        var memory = new MemoryStream();
        try { input.CopyTo(memory); memory.Position = 0; return memory; }
        catch { memory.Dispose(); throw; }
    }

    private static void RewriteCompressed(ReasoningPatch patch, List<Segment> segments, bool restore)
    {
        using var content = OpenSeekable(patch.Path);
        if (restore)
        {
            if (segments.All(segment => Read(content, segment).AsSpan().SequenceEqual(segment.Before))) return;
            ValidateRestore(content, patch, segments);
        }
        else
        {
            if (content.Length != patch.OriginalLength || segments.Any(segment =>
                    !Read(content, segment).AsSpan().SequenceEqual(segment.Before)))
                throw new IOException("Compressed conversation changed after reasoning repair preparation.");
        }
        foreach (var segment in segments)
        {
            content.Position = segment.Offset;
            content.Write(restore ? segment.Before : segment.After);
        }
        var staged = patch.Path + "." + Guid.NewGuid().ToString("N") + ".reasoningtmp";
        try
        {
            using (var output = File.Create(staged))
            {
                using (var compressed = new ZstdSharp.CompressionStream(output, 3, leaveOpen: true))
                {
                    content.Position = 0;
                    content.CopyTo(compressed);
                }
                output.Flush(true);
            }
            File.Move(staged, patch.Path, overwrite: true);
        }
        finally { File.Delete(staged); }
    }

    internal static IEnumerable<byte[]> ReadLines(Stream input)
    {
        var buffer = new byte[65536];
        using var pending = new MemoryStream();
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            var start = 0;
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] != 10) continue;
                pending.Write(buffer, start, i - start + 1);
                yield return pending.ToArray();
                pending.SetLength(0);
                start = i + 1;
            }
            pending.Write(buffer, start, count - start);
        }
        if (pending.Length > 0) yield return pending.ToArray();
    }
}

using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexAuthSwitcher.Core.Services;

public sealed record RoutingPatch(
    string Path,
    long OriginalLength,
    string HeaderBeforePath,
    string HeaderAfterPath,
    string AppendPath,
    int HeaderLength,
    long ExpectedLength);

// Codex forks refer to immutable byte and ordinal boundaries in their parents.
// Keep those boundaries intact by changing only a fixed-size metadata header.
public static class FixedWidthRoutingService
{
    private const int HeaderReserveBytes = 512;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public static RoutingPatch? TryPrepare(string path, string canonicalId, string provider, string recoveryDir)
    {
        if (string.IsNullOrWhiteSpace(canonicalId) || string.IsNullOrWhiteSpace(provider)) return null;
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var originalLength = source.Length;
        if (originalLength == 0) return null;
        source.Position = originalLength - 1;
        if (source.ReadByte() != '\n') return null;
        source.Position = 0;
        var headerBefore = ReadHeader(source);
        if (headerBefore.Length == 0) return null;
        string[] artifacts = [];
        var prepared = false;

        try
        {
            var header = ParseHeader(headerBefore);
            if (header is null || ReadString(header, "type") != "session_meta"
                || header["payload"] is not JsonObject metadata
                || string.IsNullOrWhiteSpace(ReadString(metadata, "id"))
                || ReadString(metadata, "history_mode") != "paginated"
                || !ReadOrdinal(header, out var lastOrdinal)) return null;

            JsonObject? latestSettingsRecord = null;
            using (var reader = new StreamReader(source, StrictUtf8, detectEncodingFromByteOrderMarks: false,
                       bufferSize: 64 * 1024, leaveOpen: true))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (JsonNode.Parse(line) is not JsonObject record
                        || !ReadOrdinal(record, out var ordinal) || ordinal <= lastOrdinal
                        || ReadString(record, "type") == "session_meta") return null;
                    lastOrdinal = ordinal;
                    if (ReadString(record, "type") == "event_msg" && record["payload"] is JsonObject payload
                        && ReadString(payload, "type") == "thread_settings_applied") latestSettingsRecord = record;
                }
            }
            if (latestSettingsRecord?["payload"] is not JsonObject settingsPayload
                || settingsPayload["thread_settings"] is not JsonObject settings
                || !IsCompleteSettings(settings) || lastOrdinal == long.MaxValue) return null;

            metadata["model_provider"] = provider;
            var headerAfter = EncodeHeader(header, headerBefore, reserve: false);
            if (headerAfter is null) return null;
            settings["model_provider_id"] = provider;
            settingsPayload["thread_id"] = canonicalId;
            latestSettingsRecord["ordinal"] = checked(lastOrdinal + 1);
            latestSettingsRecord["timestamp"] = DateTimeOffset.UtcNow.ToString("O");
            var newline = HeaderNewline(headerBefore);
            var append = StrictUtf8.GetBytes(latestSettingsRecord.ToJsonString(CompactJson) + newline);
            var expectedLength = checked(originalLength + append.Length);

            Directory.CreateDirectory(recoveryDir);
            var prefix = System.IO.Path.Combine(recoveryDir, Guid.NewGuid().ToString("N") + ".routing");
            var beforePath = prefix + ".before";
            var afterPath = prefix + ".after";
            var appendPath = prefix + ".append";
            artifacts = [beforePath, afterPath, appendPath];
            File.WriteAllBytes(beforePath, headerBefore);
            File.WriteAllBytes(afterPath, headerAfter);
            File.WriteAllBytes(appendPath, append);
            prepared = true;
            return new RoutingPatch(System.IO.Path.GetFullPath(path), originalLength, beforePath, afterPath,
                appendPath, headerBefore.Length, expectedLength);
        }
        catch (Exception error) when (error is JsonException or DecoderFallbackException or InvalidOperationException
                                     or FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (!prepared)
                foreach (var artifact in artifacts)
                    try { File.Delete(artifact); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Apply(RoutingPatch patch)
    {
        var (before, after, append) = ReadPatch(patch);
        using var live = new FileStream(patch.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (live.Length != patch.OriginalLength || !ReadPrefix(live, patch.HeaderLength).AsSpan().SequenceEqual(before))
            throw new IOException("Conversation changed after routing preparation. No routing patch was applied.");
        live.Position = 0;
        live.Write(after);
        live.Position = patch.OriginalLength;
        live.Write(append);
        live.Flush(flushToDisk: true);
    }

    public static void Restore(RoutingPatch patch)
    {
        var (before, after, append) = ReadPatch(patch);
        // A failed write-open can leave a prepared patch completely untouched.
        // Recognize that state without requiring a write handle, so a reader's
        // share lock does not turn a harmless failed switch into recovery work.
        using (var unchanged = new FileStream(patch.Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (unchanged.Length == patch.OriginalLength
                && ReadPrefix(unchanged, patch.HeaderLength).AsSpan().SequenceEqual(before)) return;
        }
        using var live = new FileStream(patch.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        ValidateRestore(live, patch, before, after, append);
        live.SetLength(patch.OriginalLength);
        live.Position = 0;
        live.Write(before);
        live.Flush(flushToDisk: true);
    }

    public static void ValidateRestore(RoutingPatch patch)
    {
        var (before, after, append) = ReadPatch(patch);
        using var live = new FileStream(patch.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateRestore(live, patch, before, after, append);
    }

    private static void ValidateRestore(FileStream live, RoutingPatch patch, byte[] before, byte[] after, byte[] append)
    {
        if (live.Length < patch.OriginalLength || live.Length > patch.ExpectedLength)
            throw new IOException("Conversation has additional content. Its routing patch cannot be safely restored.");
        var header = ReadPrefix(live, patch.HeaderLength);
        if (!IsPartialHeaderWrite(header, before, after))
            throw new IOException("Conversation metadata changed after switching. Its routing patch cannot be safely restored.");
        var appendedLength = checked((int)(live.Length - patch.OriginalLength));
        live.Position = patch.OriginalLength;
        if (!ReadPrefix(live, appendedLength).AsSpan().SequenceEqual(append.AsSpan(0, appendedLength)))
            throw new IOException("Conversation has additional content. Its routing patch cannot be safely restored.");
    }

    // This operates only on a staged fallback generation, before it becomes live.
    public static void ReserveHeaderSpace(string staged, string provider)
    {
        using var file = new FileStream(staged, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var headerBefore = ReadHeader(file);
        var header = ParseHeader(headerBefore);
        if (header is null || ReadString(header, "type") != "session_meta"
            || header["payload"] is not JsonObject metadata || ReadString(metadata, "history_mode") != "paginated")
            throw new IOException("Only staged paginated conversation metadata can reserve routing space.");
        metadata["model_provider"] = provider;
        var headerAfter = EncodeHeader(header, headerBefore, reserve: true)!;
        var delta = headerAfter.Length - headerBefore.Length;
        var originalLength = file.Length;
        file.SetLength(checked(originalLength + delta));
        var buffer = new byte[64 * 1024];
        var remaining = originalLength - headerBefore.Length;
        while (delta > 0 && remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            var offset = headerBefore.Length + remaining - count;
            file.Position = offset;
            file.ReadExactly(buffer.AsSpan(0, count));
            file.Position = offset + delta;
            file.Write(buffer.AsSpan(0, count));
            remaining -= count;
        }
        file.Position = 0;
        file.Write(headerAfter);
        file.Flush(flushToDisk: true);
    }

    private static bool IsCompleteSettings(JsonObject settings) =>
        !string.IsNullOrWhiteSpace(ReadString(settings, "model"))
        && !string.IsNullOrWhiteSpace(ReadString(settings, "model_provider_id"))
        && settings["approval_policy"] is not null
        && (settings["sandbox_policy"] is not null || !string.IsNullOrWhiteSpace(ReadString(settings, "cwd")));

    private static (byte[] Before, byte[] After, byte[] Append) ReadPatch(RoutingPatch patch)
    {
        var before = File.ReadAllBytes(patch.HeaderBeforePath);
        var after = File.ReadAllBytes(patch.HeaderAfterPath);
        var append = File.ReadAllBytes(patch.AppendPath);
        if (patch.HeaderLength <= 0 || before.Length != patch.HeaderLength || after.Length != patch.HeaderLength
            || patch.OriginalLength < patch.HeaderLength || append.Length == 0
            || patch.ExpectedLength != checked(patch.OriginalLength + append.Length))
            throw new IOException("Invalid conversation routing recovery data.");
        return (before, after, append);
    }

    private static bool IsPartialHeaderWrite(byte[] current, byte[] before, byte[] after)
    {
        var prefix = 0;
        while (prefix < current.Length && current[prefix] == after[prefix]) prefix++;
        return current.AsSpan(prefix).SequenceEqual(before.AsSpan(prefix));
    }

    private static byte[] ReadPrefix(Stream file, int length)
    {
        var bytes = new byte[length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] ReadHeader(Stream file)
    {
        using var header = new MemoryStream();
        int value;
        while ((value = file.ReadByte()) >= 0)
        {
            header.WriteByte((byte)value);
            if (value == '\n') break;
        }
        return header.ToArray();
    }

    private static JsonObject? ParseHeader(byte[] bytes)
    {
        var bomLength = HasBom(bytes) ? 3 : 0;
        return JsonNode.Parse(StrictUtf8.GetString(bytes, bomLength, bytes.Length - bomLength)) as JsonObject;
    }

    private static byte[]? EncodeHeader(JsonObject header, byte[] original, bool reserve)
    {
        var encoded = StrictUtf8.GetBytes(header.ToJsonString(CompactJson));
        var newline = StrictUtf8.GetBytes(HeaderNewline(original));
        var bomLength = HasBom(original) ? 3 : 0;
        var capacity = original.Length - bomLength - newline.Length;
        if (!reserve && encoded.Length > capacity) return null;
        if (reserve) capacity = Math.Max(capacity, checked(encoded.Length + HeaderReserveBytes));
        var output = new byte[checked(bomLength + capacity + newline.Length)];
        if (bomLength != 0) original.AsSpan(0, bomLength).CopyTo(output);
        encoded.CopyTo(output.AsSpan(bomLength));
        output.AsSpan(bomLength + encoded.Length, capacity - encoded.Length).Fill((byte)' ');
        newline.CopyTo(output.AsSpan(bomLength + capacity));
        return output;
    }

    private static bool HasBom(byte[] value) => value.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF });
    private static string HeaderNewline(byte[] value) => value.Length >= 2 && value[^2] == '\r' ? "\r\n" : "\n";

    private static string? ReadString(JsonObject record, string key) =>
        record[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool ReadOrdinal(JsonObject record, out long ordinal)
    {
        ordinal = 0;
        return record["ordinal"] is JsonValue value && value.TryGetValue<long>(out ordinal) && ordinal >= 0;
    }
}

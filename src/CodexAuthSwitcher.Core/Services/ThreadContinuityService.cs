using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Services;

/// <summary>
/// Changes routing metadata, never conversation content or thread identity.
/// Call only after Codex has exited. All databases are backed up through SQLite
/// (including WAL data) and all changed rollouts are backed up before any writes.
/// </summary>
internal static class ThreadContinuityService
{
    private sealed record FileChange(string Path, string Staged, string Backup, bool Existed);
    private sealed record OffsetChange(long Start, long End, long Delta);
    private sealed record RolloutChange(string Id, long OriginalLength, List<OffsetChange> Offsets, bool IsPaginated);

    private sealed class Database : IDisposable
    {
        public required string Path { get; init; }
        public required string Backup { get; init; }
        public required SqliteConnection Connection { get; init; }
        public SqliteTransaction? Transaction { get; set; }
        public bool Committed { get; set; }
        public void Dispose()
        {
            Transaction?.Dispose();
            Connection.Dispose();
        }
    }

    public static void EnsureNoInterruptedSwitch(string backupsPath)
    {
        if (!Directory.Exists(backupsPath)) return;
        foreach (var path in Directory.EnumerateFiles(backupsPath, "continuity-journal.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.GetProperty("status").GetString() is "applying" or "recovery-required")
                throw new IOException($"An interrupted switch needs recovery before continuing. Restore the files listed in {path} with Codex closed.");
        }
    }

    public static int Switch(string codexHome, string configText, string authText, string backupPath, Action? beforeApply = null)
    {
        codexHome = NormalizePath(codexHome, Environment.CurrentDirectory);
        var provider = TomlOverlayService.TryReadScalar(configText, "model_provider") ?? "openai";
        TomlOverlayService.Validate(configText);
        using var authValidation = JsonDocument.Parse(authText);
        var sqliteHome = ResolveSqliteHome(codexHome, configText);
        var files = new List<FileChange>();
        var databases = new List<Database>();
        var applied = new List<FileChange>();
        var rolloutChanges = new List<RolloutChange>();
        var journalPath = System.IO.Path.Combine(backupPath, "continuity-journal.json");
        var backupDirectory = System.IO.Path.Combine(backupPath, "continuity");
        Directory.CreateDirectory(backupDirectory);
        var changedThreads = 0;
        try
        {
            var statePath = System.IO.Path.Combine(sqliteHome, "state_5.sqlite");
            var historyPath = System.IO.Path.Combine(sqliteHome, "thread_history_1.sqlite");
            // Fail explicitly on unrecognized state versions instead of silently
            // updating an inactive DB and reporting a successful seamless switch.
            if (Directory.Exists(sqliteHome) && Directory.EnumerateFiles(sqliteHome, "state_*.sqlite")
                .Any(path => !string.Equals(path, statePath, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Unsupported Codex state database version. No switch was applied.");

            if (!File.Exists(statePath) && File.Exists(historyPath))
                throw new IOException("The Codex history cache exists without its state database. Its rollout ownership cannot be verified. No switch was applied.");

            var state = OpenDatabase(statePath, backupDirectory, databases);
            var history = OpenDatabase(historyPath, backupDirectory, databases);
            // SQLite owns the identity/path association. A fork can retain its
            // source session_meta ID, and the directory can contain old copies.
            var rollouts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var archivedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var activeIds = new List<string>();
            var replacementPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            if (state is not null)
            {
                using var query = Command(state, "SELECT id, rollout_path, archived FROM threads");
                using var reader = query.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    var path = reader.IsDBNull(1) ? null : NormalizePath(reader.GetString(1), codexHome);
                    // Either the archive flag or archive directory is sufficient.
                    // Some older records can disagree after moves/restores.
                    if (reader.GetInt64(2) != 0 || (path is not null && IsArchivedPath(codexHome, path)))
                    {
                        if (path is not null) archivedPaths.Add(path);
                        continue;
                    }
                    activeIds.Add(id);
                    if (path is null || !File.Exists(path)) continue;
                    EnsureLocalRollout(codexHome, path);
                    if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Unsupported rollout format: {path}. No switch was applied.");
                    if (!rollouts.TryAdd(path, id))
                        throw new IOException($"Multiple indexed threads reference the same rollout: {path}. No switch was applied.");
                }
            }

            var sessionsRoot = System.IO.Path.Combine(codexHome, "sessions");
            // Only legacy homes without a state DB fall back to enumeration.
            // Never mix indexed paths with unreferenced files/copies.
            if (state is null && Directory.Exists(sessionsRoot))
            {
                foreach (var path in Directory.EnumerateFiles(sessionsRoot, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = false,
                    AttributesToSkip = FileAttributes.ReparsePoint
                }))
                {
                    if (path.EndsWith(".jsonl.zst", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Compressed Codex rollouts are not supported by this switcher version. No switch was applied.");
                    if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) rollouts.Add(path, null);
                }
            }

            if (rollouts.Keys.Any(archivedPaths.Contains))
                throw new IOException("An active and an archived thread reference the same rollout. No switch was applied.");
            var legacyRolloutIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (path, canonicalId) in rollouts.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                EnsureLocalRollout(codexHome, path);
                var staged = path + "." + Guid.NewGuid().ToString("N") + ".switchtmp";
                try
                {
                    var change = StageRollout(path, staged, provider, canonicalId);
                    if (change is null) continue;
                    // An unchanged duplicate is still ambiguous: never bind a
                    // changed copy to a cache owned by another physical file.
                    if (!legacyRolloutIds.Add(change.Id))
                        throw new IOException($"Duplicate session identity in local rollouts: {change.Id}. No switch was applied.");
                    if (change.Offsets.Count == 0) continue;
                    if (change.IsPaginated)
                    {
                        // A new immutable rollout generation preserves every old
                        // cache and history_base reference, including archives.
                        var logicalId = Guid.Parse(canonicalId!).ToString();
                        var timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture);
                        var name = $"rollout-{timestamp}-{logicalId}_{Guid.NewGuid()}.jsonl";
                        var replacement = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name);
                        if (File.Exists(replacement)) throw new IOException("The replacement rollout already exists.");
                        files.Add(new FileChange(replacement, staged, path, false));
                        replacementPaths.Add(canonicalId!, replacement);
                        continue;
                    }
                    var fileBackup = System.IO.Path.Combine(backupDirectory, $"rollout-{files.Count}.jsonl");
                    File.Copy(path, fileBackup);
                    files.Add(new FileChange(path, staged, fileBackup, true));
                    rolloutChanges.Add(change);
                }
                finally
                {
                    if (!files.Any(file => file.Staged == staged)) File.Delete(staged);
                }
            }

            if (state is not null)
            {
                using var update = Command(state, "UPDATE threads SET model_provider = $provider, rollout_path = COALESCE($path, rollout_path) WHERE id = $id AND archived = 0 AND (model_provider IS NOT $provider OR $path IS NOT NULL)");
                update.Parameters.AddWithValue("$provider", provider);
                var idParameter = update.Parameters.Add("$id", SqliteType.Text);
                var pathParameter = update.Parameters.Add("$path", SqliteType.Text);
                foreach (var id in activeIds)
                {
                    idParameter.Value = id;
                    pathParameter.Value = replacementPaths.TryGetValue(id, out var replacement) ? replacement : DBNull.Value;
                    changedThreads += update.ExecuteNonQuery();
                }
            }
            // Legacy byte rewrites move both the projection checkpoint and
            // persisted turn boundaries. Both belong to the physical rollout,
            // not necessarily to the logical thread after thread/revert.
            if (history is not null)
            {
                var turnColumns = ReadTableColumns(history, "thread_turns");
                foreach (var change in rolloutChanges)
                {
                    using var query = Command(history, "SELECT next_rollout_byte_offset FROM thread_history_projection_state WHERE thread_id = $id");
                    query.Parameters.AddWithValue("$id", change.Id);
                    var value = query.ExecuteScalar();
                    if (value is not null)
                    {
                        using var update = Command(history, "UPDATE thread_history_projection_state SET next_rollout_byte_offset = $offset WHERE thread_id = $id");
                        update.Parameters.AddWithValue("$id", change.Id);
                        update.Parameters.AddWithValue("$offset", ShiftOffset(change, Convert.ToInt64(value)));
                        update.ExecuteNonQuery();
                    }
                    foreach (var column in new[] { "rollout_byte_offset", "rollout_end_byte_offset" })
                    {
                        if (!turnColumns.Contains(column)) continue;
                        var offsets = new List<(string TurnId, long Offset)>();
                        using (var turns = Command(history, $"SELECT turn_id, {column} FROM thread_turns WHERE thread_id = $id AND {column} IS NOT NULL"))
                        {
                            turns.Parameters.AddWithValue("$id", change.Id);
                            using var reader = turns.ExecuteReader();
                            while (reader.Read()) offsets.Add((reader.GetString(0), ShiftOffset(change, reader.GetInt64(1))));
                        }
                        foreach (var (turnId, offset) in offsets)
                        {
                            using var update = Command(history, $"UPDATE thread_turns SET {column} = $offset WHERE thread_id = $id AND turn_id = $turn");
                            update.Parameters.AddWithValue("$id", change.Id);
                            update.Parameters.AddWithValue("$turn", turnId);
                            update.Parameters.AddWithValue("$offset", offset);
                            update.ExecuteNonQuery();
                        }
                    }
                }
            }

            StageText(System.IO.Path.Combine(codexHome, "config.toml"), configText, backupDirectory, files);
            StageText(System.IO.Path.Combine(codexHome, "auth.json"), authText, backupDirectory, files);
            beforeApply?.Invoke();
            WriteJournal("applying");
            foreach (var file in files)
            {
                File.Move(file.Staged, file.Path, overwrite: true);
                applied.Add(file);
            }
            foreach (var database in databases)
            {
                database.Transaction!.Commit();
                database.Committed = true;
            }
            WriteJournal("complete");
            return Math.Max(changedThreads, rolloutChanges.Count);
        }
        catch (Exception error)
        {
            var errors = new List<Exception> { error };
            foreach (var database in databases)
            {
                try
                {
                    database.Transaction?.Dispose();
                    database.Transaction = null;
                    if (database.Committed)
                    {
                        using var backup = OpenConnection(database.Backup, SqliteOpenMode.ReadOnly);
                        backup.BackupDatabase(database.Connection);
                    }
                }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            foreach (var file in applied.AsEnumerable().Reverse())
            {
                try
                {
                    if (file.Existed)
                    {
                        var temporary = file.Path + "." + Guid.NewGuid().ToString("N") + ".restoretmp";
                        try { File.Copy(file.Backup, temporary); File.Move(temporary, file.Path, true); }
                        finally { File.Delete(temporary); }
                    }
                    else File.Delete(file.Path);
                }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            if (File.Exists(journalPath))
            {
                try { WriteJournal(errors.Count == 1 ? "rolled-back" : "recovery-required"); }
                catch (Exception journalError) { errors.Add(journalError); }
            }
            throw new IOException(errors.Count == 1
                ? $"Switch failed: {error.Message} Original files were retained or restored. Backup: {backupPath}"
                : $"Switch recovery is required. Keep Codex closed and restore the backup: {backupPath}",
                new AggregateException(errors));
        }
        finally
        {
            foreach (var database in databases) database.Dispose();
            foreach (var file in files)
            {
                try { File.Delete(file.Staged); } catch (IOException) { }
            }
        }

        void WriteJournal(string status) => TextFileService.WriteUtf8NoBom(journalPath, JsonSerializer.Serialize(new
        {
            status,
            files = files.Select(file => new { target = file.Path, backup = file.Backup, existed = file.Existed }),
            databases = databases.Select(database => new { target = database.Path, backup = database.Backup })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ResolveSqliteHome(string codexHome, string configText)
    {
        var configured = TomlOverlayService.TryReadScalar(configText, "sqlite_home");
        if (!string.IsNullOrWhiteSpace(configured)) return NormalizePath(configured, codexHome);
        var fromEnvironment = Environment.GetEnvironmentVariable("CODEX_SQLITE_HOME");
        return string.IsNullOrWhiteSpace(fromEnvironment) ? codexHome : NormalizePath(fromEnvironment, Environment.CurrentDirectory);
    }

    private static string NormalizePath(string path, string basePath)
    {
        // Rust persists canonical Windows paths with a verbatim prefix. Compare
        // them in the same form as the paths discovered by .NET enumeration.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path, basePath));
    }

    private static SqliteConnection OpenConnection(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 3
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static Database? OpenDatabase(string path, string backups, List<Database> databases)
    {
        if (!File.Exists(path)) return null;
        var connection = OpenConnection(path, SqliteOpenMode.ReadWrite);
        var database = new Database
        {
            Path = path,
            Backup = System.IO.Path.Combine(backups, System.IO.Path.GetFileName(path)),
            Connection = connection
        };
        databases.Add(database);
        using (var backup = OpenConnection(database.Backup, SqliteOpenMode.ReadWriteCreate))
            connection.BackupDatabase(backup);
        database.Transaction = connection.BeginTransaction();
        return database;
    }

    private static SqliteCommand Command(Database database, string sql)
    {
        var command = database.Connection.CreateCommand();
        command.Transaction = database.Transaction;
        command.CommandText = sql;
        return command;
    }

    private static HashSet<string> ReadTableColumns(Database database, string table)
    {
        using var query = Command(database, $"PRAGMA table_info({table})");
        using var reader = query.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns;
    }

    private static long ShiftOffset(RolloutChange change, long offset)
    {
        if (offset < 0 || offset > change.OriginalLength
            || change.Offsets.Any(item => item.Start < offset && offset < item.End))
            throw new IOException($"Invalid history byte offset for thread {change.Id}. No switch was applied.");
        return checked(offset + change.Offsets.Where(item => item.End <= offset).Sum(item => item.Delta));
    }

    private static string? ReadPhysicalRolloutId(string path, string? canonicalId)
    {
        // Codex's canonical grammar is rollout-{yyyy-MM-ddTHH-mm-ss}-
        // {thread UUID}[_{immutable rollout UUID}].jsonl. Noncanonical
        // filenames remain supported only through the legacy identity fallback.
        var name = System.IO.Path.GetFileName(path);
        if (!name.StartsWith("rollout-", StringComparison.Ordinal)
            || !name.EndsWith(".jsonl", StringComparison.Ordinal)) return null;
        var core = name[8..^6];
        if (core.Length <= 20 || core[19] != '-'
            || !DateTime.TryParseExact(core[..19], "yyyy-MM-dd'T'HH-mm-ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) return null;
        var ids = core[20..].Split('_');
        if (ids.Length is < 1 or > 2 || !Guid.TryParse(ids[0], out var threadId)
            || !Guid.TryParse(ids[^1], out var rolloutId)) return null;
        if (canonicalId is not null && (!Guid.TryParse(canonicalId, out var expected) || expected != threadId))
            throw new IOException($"The indexed thread identity does not match its rollout filename: {path}. No switch was applied.");
        return rolloutId.ToString("D");
    }

    private static void StageText(string path, string text, string backups, List<FileChange> files)
    {
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".switchtmp";
        var backup = System.IO.Path.Combine(backups, System.IO.Path.GetFileName(path));
        var existed = File.Exists(path);
        files.Add(new FileChange(path, staged, backup, existed));
        if (existed) File.Copy(path, backup);
        File.WriteAllText(staged, text, new UTF8Encoding(false));
    }

    private static void EnsureLocalRollout(string codexHome, string path)
    {
        var relative = System.IO.Path.GetRelativePath(codexHome, path);
        if (!relative.StartsWith("sessions" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Rollout is outside the local session library: {path}");
        for (var current = path; !string.Equals(current, codexHome, StringComparison.OrdinalIgnoreCase); current = System.IO.Path.GetDirectoryName(current)!)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Linked rollout paths are not supported: {path}");
        }
    }

    private static bool IsArchivedPath(string codexHome, string path) =>
        System.IO.Path.GetRelativePath(codexHome, path).StartsWith(
            "archived_sessions" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static RolloutChange? StageRollout(string path, string staged, string provider, string? canonicalId)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = File.Create(staged);
        var offsets = new List<OffsetChange>();
        string? id = null;
        var sawSessionMeta = false;
        var isPaginated = false;
        long position = 0;
        foreach (var line in ReadLines(input))
        {
            var prefixLength = position == 0 && line.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
            var length = line.Length;
            while (length > prefixLength && (line[length - 1] == 10 || line[length - 1] == 13)) length--;
            var content = line.AsMemory(prefixLength, length - prefixLength);
            if (content.Length == 0) { output.Write(line); position += line.Length; continue; }
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var type = root.GetProperty("type").GetString();
            string? field = null;
            if (type == "session_meta")
            {
                if (root.GetProperty("payload").TryGetProperty("history_mode", out var mode)
                    && mode.GetString() == "paginated")
                {
                    if (canonicalId is null)
                        throw new IOException("Paginated history requires its Codex state database. No switch was applied.");
                    if (!Guid.TryParseExact(canonicalId, "D", out _))
                        throw new IOException("Paginated history requires a valid indexed thread UUID.");
                    isPaginated = true;
                }
                id ??= ReadPhysicalRolloutId(path, canonicalId)
                    ?? canonicalId ?? root.GetProperty("payload").GetProperty("id").GetString();
                sawSessionMeta = true;
                // Retain all identities, including embedded source metadata.
                // Only provider routing changes; cache updates use the physical
                // rollout ID, with a legacy logical-ID fallback.
                field = "model_provider";
            }
            else if (type == "event_msg"
                && root.GetProperty("payload").TryGetProperty("type", out var eventType)
                && eventType.GetString() == "thread_settings_applied")
                field = "model_provider_id";
            var updated = line;
            if (field is not null)
            {
                var payload = root.GetProperty("payload");
                var settings = field == "model_provider_id" ? payload.GetProperty("thread_settings") : payload;
                if (!settings.TryGetProperty(field, out var current) || current.GetString() != provider)
                {
                    var node = JsonNode.Parse(content.Span)!;
                    var target = field == "model_provider_id" ? node["payload"]!["thread_settings"]! : node["payload"]!;
                    target[field] = provider;
                    var json = JsonSerializer.SerializeToUtf8Bytes(node);
                    updated = new byte[prefixLength + json.Length + line.Length - length];
                    line.AsSpan(0, prefixLength).CopyTo(updated);
                    json.CopyTo(updated, prefixLength);
                    line.AsSpan(length).CopyTo(updated.AsSpan(prefixLength + json.Length));
                    offsets.Add(new OffsetChange(position, position + line.Length, updated.Length - line.Length));
                }
            }
            output.Write(updated);
            position += line.Length;
        }
        if (!sawSessionMeta || string.IsNullOrWhiteSpace(id)) throw new IOException($"Missing session identity in {path}");
        return new RolloutChange(id, position, offsets, isPaginated);
    }

    // Preserve UTF-8 bytes and CRLF/LF exactly for every untouched JSONL record.
    private static IEnumerable<byte[]> ReadLines(Stream input)
    {
        var buffer = new byte[65536];
        using var pending = new MemoryStream();
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            var start = 0;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] != 10) continue;
                pending.Write(buffer, start, index - start + 1);
                yield return pending.ToArray();
                pending.SetLength(0);
                start = index + 1;
            }
            pending.Write(buffer, start, count - start);
        }
        if (pending.Length > 0) yield return pending.ToArray();
    }
}

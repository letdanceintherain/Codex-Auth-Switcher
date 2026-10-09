using System.Globalization;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace CodexAuthSwitcher.Core.Services;

/// <summary>
/// Changes routing metadata, never conversation content or thread identity.
/// Call only after Codex has exited. All databases are backed up through SQLite
/// (including WAL data). Paginated routing keeps byte boundaries stable and
/// records only its original header and append operation for recovery.
/// </summary>
internal static class ThreadContinuityService
{
    private sealed record FileChange(string Path, string Staged, string Backup, bool Existed,
        long? ExpectedLength = null, string? ExpectedHash = null);
    private sealed record OffsetChange(long Start, long End, long Delta);
    private sealed record RolloutChange(string Id, long OriginalLength, List<OffsetChange> Offsets, bool IsPaginated);
    private sealed record RolloutFingerprint(string Hash, bool HasTargetRouting);

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

    public static bool NeedsSynchronization(string codexHome, string currentConfig, string targetConfig)
    {
        codexHome = NormalizePath(codexHome, Environment.CurrentDirectory);
        var sqliteHome = ResolveSqliteHome(codexHome, targetConfig);
        ValidateDatabaseVersion(sqliteHome);
        var statePath = System.IO.Path.Combine(sqliteHome, "state_5.sqlite");
        var provider = TomlOverlayService.TryReadScalar(targetConfig, "model_provider") ?? "openai";
        if (!File.Exists(statePath))
        {
            if (File.Exists(System.IO.Path.Combine(sqliteHome, "thread_history_1.sqlite")))
                throw new IOException("The Codex history cache exists without its state database. No switch was applied.");
            // Legacy homes have no index to query. Scan them only on a provider change.
            return Directory.Exists(System.IO.Path.Combine(codexHome, "sessions"))
                && (TomlOverlayService.TryReadScalar(currentConfig, "model_provider") ?? "openai") != provider;
        }
        using var state = OpenConnection(statePath, SqliteOpenMode.ReadOnly);
        using var query = state.CreateCommand();
        query.CommandText = "SELECT rollout_path FROM threads WHERE archived = 0 AND model_provider IS NOT $provider";
        query.Parameters.AddWithValue("$provider", provider);
        using var reader = query.ExecuteReader();
        while (reader.Read())
            if (reader.IsDBNull(0) || !IsArchivedPath(codexHome, NormalizePath(reader.GetString(0), codexHome))) return true;
        return false;
    }

    private static void ValidateDatabaseVersion(string sqliteHome)
    {
        if (Directory.Exists(sqliteHome) && Directory.EnumerateFiles(sqliteHome, "state_*.sqlite")
            .Any(path => !string.Equals(System.IO.Path.GetFileName(path), "state_5.sqlite", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Unsupported Codex state database version. No switch was applied.");
    }

    public static string? RecoverInterruptedSwitch(string codexHome, string backupPath, Action beforeRestore)
    {
        var journalPath = System.IO.Path.Combine(backupPath, "continuity-journal.json");
        if (!File.Exists(journalPath)) return null; // Preparation never changed live files.
        var journal = JsonNode.Parse(File.ReadAllText(journalPath))!;
        var status = journal["status"]!.GetValue<string>();
        if (status is "complete" or "rolled-back") return status;
        if (status is not ("applying" or "recovery-required"))
            throw new IOException($"Unknown switch recovery status: {journalPath}");

        codexHome = NormalizePath(codexHome, Environment.CurrentDirectory);
        backupPath = NormalizePath(backupPath, Environment.CurrentDirectory);
        var configPath = System.IO.Path.Combine(codexHome, "config.toml");
        var files = journal["files"]!.AsArray().Select(node => (
            Target: NormalizePath(node!["target"]!.GetValue<string>(), codexHome),
            Backup: NormalizePath(node["backup"]!.GetValue<string>(), backupPath),
            Existed: node["existed"]!.GetValue<bool>(),
            ExpectedLength: node["expectedLength"]?.GetValue<long>(),
            ExpectedHash: node["expectedHash"]?.GetValue<string>())).ToList();
        var databases = journal["databases"]!.AsArray().Select(node => (
            Target: NormalizePath(node!["target"]!.GetValue<string>(), codexHome),
            Backup: NormalizePath(node["backup"]!.GetValue<string>(), backupPath))).ToList();
        var patches = journal["routingPatches"]?.AsArray().Select(node => new RoutingPatch(
            NormalizePath(node!["target"]!.GetValue<string>(), codexHome),
            node["originalLength"]!.GetValue<long>(),
            NormalizePath(node["headerBefore"]!.GetValue<string>(), backupPath),
            NormalizePath(node["headerAfter"]!.GetValue<string>(), backupPath),
            NormalizePath(node["append"]!.GetValue<string>(), backupPath),
            node["headerLength"]!.GetValue<int>(),
            node["expectedLength"]!.GetValue<long>())).ToList() ?? [];
        var configBackup = files.FirstOrDefault(file => string.Equals(file.Target, configPath, StringComparison.OrdinalIgnoreCase));
        var config = File.ReadAllText(configBackup.Existed ? configBackup.Backup : configPath);
        var sqliteHome = ResolveSqliteHome(codexHome, config);

        // Validate every source and target before restoring any of them. New
        // paginated generations can be deleted, but existing histories cannot.
        foreach (var file in files)
        {
            var isConfig = string.Equals(file.Target, configPath, StringComparison.OrdinalIgnoreCase);
            var isAuth = string.Equals(file.Target, System.IO.Path.Combine(codexHome, "auth.json"), StringComparison.OrdinalIgnoreCase);
            if (!isConfig && !isAuth)
            {
                var relative = System.IO.Path.GetRelativePath(codexHome, file.Target);
                if (!relative.StartsWith("sessions" + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !file.Target.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Recovery target is outside the active conversation library.");
                if (File.Exists(file.Target)) EnsureLocalRollout(codexHome, file.Target);
            }
            if (!file.Existed) continue;
            ValidateRecoverySource(file.Backup, backupPath);
            if (isConfig) TomlOverlayService.Validate(File.ReadAllText(file.Backup));
            if (isAuth) using (JsonDocument.Parse(File.ReadAllText(file.Backup))) { }
        }
        foreach (var database in databases)
        {
            if (!new[] { "state_5.sqlite", "thread_history_1.sqlite" }.Any(name =>
                    string.Equals(database.Target, System.IO.Path.Combine(sqliteHome, name), StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Recovery database does not match the configured Codex database directory.");
            ValidateRecoverySource(database.Backup, backupPath);
            using var source = OpenConnection(database.Backup, SqliteOpenMode.ReadOnly);
        }
        foreach (var patch in patches)
        {
            EnsureLocalRollout(codexHome, patch.Path);
            if (!patch.Path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Recovery routing target is not a local rollout.");
            ValidateRecoverySource(patch.HeaderBeforePath, backupPath);
            ValidateRecoverySource(patch.HeaderAfterPath, backupPath);
            ValidateRecoverySource(patch.AppendPath, backupPath);
        }
        beforeRestore();
        // Stop Codex before inspecting live patch state: a normal exit can
        // flush final records. Reject additional content before restoring DBs.
        foreach (var patch in patches) FixedWidthRoutingService.ValidateRestore(patch);
        foreach (var file in files.Where(file => !file.Existed))
            ValidateNewGeneration(file.Target, file.ExpectedLength, file.ExpectedHash);
        foreach (var database in databases)
        {
            using var source = OpenConnection(database.Backup, SqliteOpenMode.ReadOnly);
            using var target = OpenConnection(database.Target, SqliteOpenMode.ReadWriteCreate);
            source.BackupDatabase(target);
        }
        foreach (var file in files.AsEnumerable().Reverse())
        {
            if (!file.Existed) { File.Delete(file.Target); continue; }
            var staged = System.IO.Path.Combine(backupPath, "recover.switchtmp");
            File.Copy(file.Backup, staged, overwrite: true);
            File.Move(staged, file.Target, overwrite: true);
        }
        foreach (var patch in patches.AsEnumerable().Reverse()) FixedWidthRoutingService.Restore(patch);
        journal["status"] = "rolled-back";
        TextFileService.WriteUtf8NoBom(journalPath, journal.ToJsonString());
        return "rolled-back";
    }

    private static void ValidateRecoverySource(string path, string backupPath)
    {
        var relative = System.IO.Path.GetRelativePath(backupPath, path);
        if (System.IO.Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar) || !File.Exists(path))
            throw new IOException($"The switch recovery source is missing or outside its recovery point: {path}");
    }

    public static int Switch(string codexHome, string configText, string authText, string backupPath,
        Action? beforeApply = null, bool synchronizeThreads = true)
    {
        codexHome = NormalizePath(codexHome, Environment.CurrentDirectory);
        var provider = TomlOverlayService.TryReadScalar(configText, "model_provider") ?? "openai";
        TomlOverlayService.Validate(configText);
        using var authValidation = JsonDocument.Parse(authText);
        var sqliteHome = ResolveSqliteHome(codexHome, configText);
        var files = new List<FileChange>();
        var databases = new List<Database>();
        var applied = new List<FileChange>();
        var patches = new List<RoutingPatch>();
        var appliedPatches = new List<RoutingPatch>();
        var rolloutChanges = new List<RolloutChange>();
        var journalPath = System.IO.Path.Combine(backupPath, "continuity-journal.json");
        var backupDirectory = System.IO.Path.Combine(backupPath, "continuity");
        Directory.CreateDirectory(backupDirectory);
        var changedThreads = 0;
        try
        {
            if (synchronizeThreads)
            {
                var statePath = System.IO.Path.Combine(sqliteHome, "state_5.sqlite");
                var historyPath = System.IO.Path.Combine(sqliteHome, "thread_history_1.sqlite");
                // Fail explicitly on unrecognized state versions instead of silently
                // updating an inactive DB and reporting a successful seamless switch.
                ValidateDatabaseVersion(sqliteHome);

                if (!File.Exists(statePath) && File.Exists(historyPath))
                    throw new IOException("The Codex history cache exists without its state database. Its rollout ownership cannot be verified. No switch was applied.");

                var state = OpenDatabase(statePath, backupDirectory, databases);
                // SQLite owns the identity/path association. A fork can retain its
                // source session_meta ID, and the directory can contain old copies.
                var rollouts = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var archivedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var indexedOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var activeIds = new List<string>();
                var changedIds = new HashSet<string>(StringComparer.Ordinal);
                var replacementPaths = new Dictionary<string, string>(StringComparer.Ordinal);
                if (state is not null)
                {
                    using var query = Command(state, "SELECT id, rollout_path, archived, model_provider FROM threads");
                    using var reader = query.ExecuteReader();
                    while (reader.Read())
                    {
                        var id = reader.GetString(0);
                        var path = reader.IsDBNull(1) ? null : NormalizePath(reader.GetString(1), codexHome);
                        if (path is not null) indexedOwners.TryAdd(path, id);
                        // Either the archive flag or archive directory is sufficient.
                        // Some older records can disagree after moves/restores.
                        if (reader.GetInt64(2) != 0 || (path is not null && IsArchivedPath(codexHome, path)))
                        {
                            if (path is not null) archivedPaths.Add(path);
                            continue;
                        }
                        if (reader.IsDBNull(3) || reader.GetString(3) != provider)
                        {
                            activeIds.Add(id);
                            changedIds.Add(id);
                        }
                        if (path is null || !File.Exists(path)) continue;
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
                    if (canonicalId is not null && !changedIds.Contains(canonicalId)) continue;
                    EnsureLocalRollout(codexHome, path);
                    if (!path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Unsupported rollout format: {path}. No switch was applied.");
                    if (canonicalId is not null)
                    {
                        var physicalId = ReadPhysicalRolloutId(path, canonicalId);
                        var patch = physicalId is null ? null
                            : FixedWidthRoutingService.TryPrepare(path, canonicalId, provider, backupDirectory);
                        if (patch is not null)
                        {
                            patches.Add(patch);
                            continue;
                        }
                        var reusable = FindReusableGeneration(codexHome, path, canonicalId, provider, archivedPaths, indexedOwners);
                        if (reusable is not null)
                        {
                            replacementPaths.Add(canonicalId, reusable);
                            continue;
                        }
                    }
                    var staged = System.IO.Path.Combine(backupDirectory, Guid.NewGuid().ToString("N") + ".switchtmp");
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
                            FixedWidthRoutingService.ReserveHeaderSpace(staged, provider);
                            // A new immutable rollout generation preserves every old
                            // cache and history_base reference, including archives.
                            var logicalId = Guid.Parse(canonicalId!).ToString();
                            var timestamp = DateTime.Now.ToString("yyyy-MM-ddTHH-mm-ss", CultureInfo.InvariantCulture);
                            var name = $"rollout-{timestamp}-{logicalId}_{Guid.NewGuid()}.jsonl";
                            var replacement = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name);
                            if (File.Exists(replacement)) throw new IOException("The replacement rollout already exists.");
                            using var prepared = File.OpenRead(staged);
                            files.Add(new FileChange(replacement, staged, path, false,
                                prepared.Length, Convert.ToHexString(SHA256.HashData(prepared))));
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
                    if (activeIds.Count > 0) BackupDatabase(state);
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
                var history = rolloutChanges.Count > 0 ? OpenDatabase(historyPath, backupDirectory, databases) : null;
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
                            var originalOffset = Convert.ToInt64(value);
                            var nextOffset = ShiftOffset(change, originalOffset);
                            if (nextOffset != originalOffset)
                            {
                                BackupDatabase(history);
                                using var update = Command(history, "UPDATE thread_history_projection_state SET next_rollout_byte_offset = $offset WHERE thread_id = $id");
                                update.Parameters.AddWithValue("$id", change.Id);
                                update.Parameters.AddWithValue("$offset", nextOffset);
                                update.ExecuteNonQuery();
                            }
                        }
                        foreach (var column in new[] { "rollout_byte_offset", "rollout_end_byte_offset" })
                        {
                            if (!turnColumns.Contains(column)) continue;
                            var offsets = new List<(string TurnId, long Offset)>();
                            using (var turns = Command(history, $"SELECT turn_id, {column} FROM thread_turns WHERE thread_id = $id AND {column} IS NOT NULL"))
                            {
                                turns.Parameters.AddWithValue("$id", change.Id);
                                using var reader = turns.ExecuteReader();
                                while (reader.Read())
                                {
                                    var originalOffset = reader.GetInt64(1);
                                    var nextOffset = ShiftOffset(change, originalOffset);
                                    if (nextOffset != originalOffset) offsets.Add((reader.GetString(0), nextOffset));
                                }
                            }
                            foreach (var (turnId, offset) in offsets)
                            {
                                BackupDatabase(history);
                                using var update = Command(history, $"UPDATE thread_turns SET {column} = $offset WHERE thread_id = $id AND turn_id = $turn");
                                update.Parameters.AddWithValue("$id", change.Id);
                                update.Parameters.AddWithValue("$turn", turnId);
                                update.Parameters.AddWithValue("$offset", offset);
                                update.ExecuteNonQuery();
                            }
                        }
                    }
                }
            }

            StageText(System.IO.Path.Combine(codexHome, "config.toml"), configText, backupDirectory, files);
            StageText(System.IO.Path.Combine(codexHome, "auth.json"), authText, backupDirectory, files);
            beforeApply?.Invoke();
            WriteJournal("applying");
            foreach (var patch in patches)
            {
                // A patch may fail after writing only part of its header/tail.
                // Include it in recovery before its first write.
                appliedPatches.Add(patch);
                FixedWidthRoutingService.Apply(patch);
            }
            foreach (var file in files)
            {
                File.Move(file.Staged, file.Path, overwrite: true);
                applied.Add(file);
            }
            foreach (var database in databases)
            {
                database.Transaction!.Commit();
                database.Committed = File.Exists(database.Backup);
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
                    else
                    {
                        ValidateNewGeneration(file.Path, file.ExpectedLength, file.ExpectedHash);
                        File.Delete(file.Path);
                    }
                }
                catch (Exception restoreError) { errors.Add(restoreError); }
            }
            foreach (var patch in appliedPatches.AsEnumerable().Reverse())
            {
                try { FixedWidthRoutingService.Restore(patch); }
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
            files = files.Select(file => new
            {
                target = file.Path, backup = System.IO.Path.GetRelativePath(backupPath, file.Backup),
                existed = file.Existed, expectedLength = file.ExpectedLength, expectedHash = file.ExpectedHash
            }),
            routingPatches = patches.Select(patch => new
            {
                target = patch.Path,
                originalLength = patch.OriginalLength,
                headerBefore = System.IO.Path.GetRelativePath(backupPath, patch.HeaderBeforePath),
                headerAfter = System.IO.Path.GetRelativePath(backupPath, patch.HeaderAfterPath),
                append = System.IO.Path.GetRelativePath(backupPath, patch.AppendPath),
                headerLength = patch.HeaderLength,
                expectedLength = patch.ExpectedLength
            }),
            databases = databases.Where(database => File.Exists(database.Backup))
                .Select(database => new { target = database.Path, backup = System.IO.Path.GetRelativePath(backupPath, database.Backup) })
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateNewGeneration(string path, long? expectedLength, string? expectedHash)
    {
        // Older journals have no digest. New immutable generations are deleted
        // during recovery only while still identical to their prepared content.
        if (expectedLength is null || expectedHash is null || !File.Exists(path)) return;
        using var current = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (current.Length != expectedLength || Convert.ToHexString(SHA256.HashData(current)) != expectedHash)
            throw new IOException("The new conversation generation has changed. Recovery will retain its additional content.");
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
        database.Transaction = connection.BeginTransaction();
        return database;
    }

    private static void BackupDatabase(Database database)
    {
        if (File.Exists(database.Backup)) return;
        // A separate read connection includes committed WAL data while our write
        // transaction prevents another writer from changing it before the update.
        using var source = OpenConnection(database.Path, SqliteOpenMode.ReadOnly);
        using var backup = OpenConnection(database.Backup, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(backup);
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
        if (File.Exists(path))
        {
            var current = File.ReadAllText(path);
            if (System.IO.Path.GetExtension(path) == ".toml" ? TomlOverlayService.Equivalent(current, text)
                : AuthComparisonService.Equivalent(current, text)) return;
        }
        var staged = System.IO.Path.Combine(backups, Guid.NewGuid().ToString("N") + ".switchtmp");
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

    private static string? FindReusableGeneration(string codexHome, string path, string canonicalId, string provider,
        HashSet<string> archivedPaths, Dictionary<string, string> indexedOwners)
    {
        if (!Guid.TryParseExact(canonicalId, "D", out _) || !HasPaginatedHeader(path)) return null;
        // Search only canonical files for this logical conversation. Unindexed
        // arbitrary copies and archived generations never become a new route.
        var candidates = Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(path)!, $"rollout-*-{canonicalId}*.jsonl")
            .Where(candidate => !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase)
                && !archivedPaths.Contains(candidate)
                && (!indexedOwners.TryGetValue(candidate, out var owner) || owner == canonicalId))
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        RolloutFingerprint? desired = null;
        foreach (var candidate in candidates)
        {
            try
            {
                EnsureLocalRollout(codexHome, candidate);
                if (ReadPhysicalRolloutId(candidate, canonicalId) is null || !HasPaginatedHeader(candidate, provider)) continue;
                desired ??= FingerprintRollout(path, canonicalId, provider);
                var existing = FingerprintRollout(candidate, canonicalId, provider);
                if (existing.HasTargetRouting && existing.Hash == desired.Hash) return candidate;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                // A bad optional candidate cannot prevent switching an otherwise
                // valid indexed history. The source is validated by StageRollout.
            }
        }
        return null;
    }

    private static bool HasPaginatedHeader(string path, string? provider = null)
    {
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var line = ReadLines(input).FirstOrDefault();
        if (line is null) return false;
        var content = TrimLine(line, true);
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        return root.TryGetProperty("type", out var type) && type.GetString() == "session_meta"
            && root.TryGetProperty("payload", out var payload)
            && payload.TryGetProperty("history_mode", out var mode) && mode.GetString() == "paginated"
            && (provider is null || payload.TryGetProperty("model_provider", out var current) && current.GetString() == provider);
    }

    private static RolloutFingerprint FingerprintRollout(string path, string canonicalId, string provider)
    {
        // Only provider fields and routing-record formatting may differ. Every
        // other record is hashed byte-for-byte, including ordinals/newlines.
        // A shorter/stale conversation or different history_base cannot match.
        using var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var hasTargetRouting = true;
        var sawMeta = false;
        var first = true;
        Span<byte> length = stackalloc byte[8];
        foreach (var line in ReadLines(input))
        {
            var content = TrimLine(line, first);
            first = false;
            byte[] normalized = line;
            if (content.Length > 0)
            {
                using var document = JsonDocument.Parse(content);
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                var field = type == "session_meta" ? "model_provider"
                    : type == "event_msg" && root.GetProperty("payload").TryGetProperty("type", out var eventType)
                        && eventType.GetString() == "thread_settings_applied" ? "model_provider_id" : null;
                if (type == "session_meta") sawMeta = true;
                if (field is not null)
                {
                    var node = JsonNode.Parse(content.Span)!;
                    var settings = field == "model_provider" ? node["payload"]! : node["payload"]!["thread_settings"]!;
                    hasTargetRouting &= settings[field]?.GetValue<string>() == provider;
                    settings[field] = provider;
                    normalized = JsonSerializer.SerializeToUtf8Bytes(SortJson(node));
                }
            }
            BinaryPrimitives.WriteInt64LittleEndian(length, normalized.Length);
            hash.AppendData(length);
            hash.AppendData(normalized);
        }
        if (!sawMeta) throw new IOException("Missing session identity in reusable history.");
        // Validate the indexed filename before treating an old cache as reusable.
        ReadPhysicalRolloutId(path, canonicalId);
        return new RolloutFingerprint(Convert.ToHexString(hash.GetHashAndReset()), hasTargetRouting);
    }

    private static JsonNode? SortJson(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, SortJson(pair.Value)))),
        JsonArray array => new JsonArray(array.Select(SortJson).ToArray()),
        _ => node?.DeepClone()
    };

    private static ReadOnlyMemory<byte> TrimLine(byte[] line, bool first)
    {
        var prefix = first && line.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? 3 : 0;
        var end = line.Length;
        while (end > prefix && (line[end - 1] == 10 || line[end - 1] == 13)) end--;
        return line.AsMemory(prefix, end - prefix);
    }

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

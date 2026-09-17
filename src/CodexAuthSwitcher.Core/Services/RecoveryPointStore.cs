namespace CodexAuthSwitcher.Core.Services;

/// <summary>One completed recovery point, plus the operation currently being prepared.</summary>
internal sealed class RecoveryPointStore(string backupsPath, string codexHome)
{
    public string PendingPath => Path.Combine(backupsPath, "pending");
    private string LatestPath => Path.Combine(backupsPath, "latest");
    private string PreviousPath => Path.Combine(backupsPath, "previous");
    private string MigrationPath => Path.Combine(backupsPath, "recovery-v2");

    public static bool NeedsAttention(string root) => Directory.Exists(Path.Combine(root, "pending"))
        || Directory.Exists(Path.Combine(root, "previous"))
        || (!File.Exists(Path.Combine(root, "recovery-v2")) && LegacyJournals(root).Any());

    public void Prepare(Action beforeRestore)
    {
        Directory.CreateDirectory(backupsPath);
        if (!File.Exists(MigrationPath))
        {
            // Inspect old timestamp journals once on upgrade. Old backups and
            // user-created checkpoints are left in place, never treated as ours to prune.
            var interrupted = LegacyJournals(backupsPath).Where(path =>
            {
                using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.GetProperty("status").GetString() is "applying" or "recovery-required";
            }).ToArray();
            if (interrupted.Length > 1)
                throw new IOException("Multiple old switches need recovery. Their order must be resolved before switching.");
            foreach (var journal in interrupted)
                ThreadContinuityService.RecoverInterruptedSwitch(codexHome, Path.GetDirectoryName(journal)!, beforeRestore);
            TextFileService.WriteUtf8NoBom(MigrationPath, "2");
        }
        if (Directory.Exists(PendingPath))
        {
            var status = ThreadContinuityService.RecoverInterruptedSwitch(codexHome, PendingPath, beforeRestore);
            if (status == "complete") Complete();
            else DeleteOwnedDirectory(PendingPath);
        }
        if (Directory.Exists(PreviousPath))
        {
            if (!Directory.Exists(LatestPath)) Directory.Move(PreviousPath, LatestPath);
            else DeleteOwnedDirectory(PreviousPath);
        }
    }

    public string Complete()
    {
        DeleteOwnedDirectory(PreviousPath);
        if (Directory.Exists(LatestPath)) Directory.Move(LatestPath, PreviousPath);
        Directory.Move(PendingPath, LatestPath);
        DeleteOwnedDirectory(PreviousPath);
        return LatestPath;
    }

    private static IEnumerable<string> LegacyJournals(string root) => !Directory.Exists(root) ? [] :
        Directory.EnumerateDirectories(root)
            .Where(path => char.IsDigit(Path.GetFileName(path)[0]))
            .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
            .Select(path => Path.Combine(path, "continuity-journal.json"))
            .Where(File.Exists).OrderBy(path => path, StringComparer.Ordinal);

    private void DeleteOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        if (path != PendingPath && path != LatestPath && path != PreviousPath)
            throw new IOException("Unexpected recovery directory.");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Recovery directory must not be a link: {path}");
        Directory.Delete(path, recursive: true);
    }
}

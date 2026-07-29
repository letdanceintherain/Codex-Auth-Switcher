using System.Text;
using System.Text.Json;

namespace CodexAuthSwitcher.Core.Services;

internal static class TextFileService
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteUtf8NoBom(string path, string content)
    {
        var temporaryPath = WriteTemporaryFile(path, content);
        try
        {
            ReplaceFromTemporary(temporaryPath, path);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
        }
    }

    public static void WriteConfigAndAuthTransactional(
        string configPath,
        string configContent,
        string authPath,
        string authContent)
    {
        TomlOverlayService.Validate(configContent);
        using (JsonDocument.Parse(authContent))
        {
        }

        var originalConfig = File.Exists(configPath) ? File.ReadAllText(configPath) : null;
        var originalAuth = File.Exists(authPath) ? File.ReadAllText(authPath) : null;
        string? configTemporaryPath = null;
        string? authTemporaryPath = null;
        var configCommitted = false;
        var authCommitted = false;

        try
        {
            configTemporaryPath = WriteTemporaryFile(configPath, configContent);
            authTemporaryPath = WriteTemporaryFile(authPath, authContent);
            ReplaceFromTemporary(configTemporaryPath, configPath);
            configCommitted = true;
            ReplaceFromTemporary(authTemporaryPath, authPath);
            authCommitted = true;
        }
        catch (Exception writeError)
        {
            var rollbackErrors = new List<Exception>();
            if (authCommitted)
            {
                TryRestore(authPath, originalAuth, rollbackErrors);
            }

            if (configCommitted)
            {
                TryRestore(configPath, originalConfig, rollbackErrors);
            }

            if (rollbackErrors.Count > 0)
            {
                throw new AggregateException(
                    "The Codex auth switch failed and one or more files could not be rolled back. Use the newest auth-switcher backup before restarting Codex.",
                    new[] { writeError }.Concat(rollbackErrors));
            }

            throw new IOException("The Codex auth switch failed. The original config.toml and auth.json were restored.", writeError);
        }
        finally
        {
            DeleteIfExists(configTemporaryPath);
            DeleteIfExists(authTemporaryPath);
        }
    }

    private static string WriteTemporaryFile(string targetPath, string content)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = Path.Combine(
            directory ?? Environment.CurrentDirectory,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temporaryPath, content, Utf8NoBom);
        return temporaryPath;
    }

    private static void ReplaceFromTemporary(string temporaryPath, string targetPath)
    {
        File.Move(temporaryPath, targetPath, overwrite: true);
    }

    private static void TryRestore(string path, string? originalContent, ICollection<Exception> errors)
    {
        try
        {
            if (originalContent is null)
            {
                DeleteIfExists(path);
                return;
            }

            WriteUtf8NoBom(path, originalContent);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

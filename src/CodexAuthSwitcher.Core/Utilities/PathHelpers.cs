using System.Text.RegularExpressions;

namespace CodexAuthSwitcher.Core.Utilities;

internal static class PathHelpers
{
    public static string SanitizeProfileName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("Profile name cannot be empty.");
        }

        var invalid = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        var cleaned = Regex.Replace(trimmed, $"[{invalid}]", "-");
        cleaned = Regex.Replace(cleaned, @"\s+", "-");
        cleaned = cleaned.Trim('-', '.');
        return cleaned.Length == 0 ? "profile" : cleaned;
    }

    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }
    }
}

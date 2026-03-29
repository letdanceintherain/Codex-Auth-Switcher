using System.Text.RegularExpressions;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.Core.Services;

public static class TomlOverlayService
{
    public static string ApplyApiOverlay(string baseConfigText, ApiProfileSpec spec)
    {
        var effectiveProvider = spec.GetEffectiveProviderId();
        var text = RemoveSection(baseConfigText, "model_providers.openai");
        text = SetScalar(text, "model_provider", effectiveProvider);
        text = SetScalar(text, "model", spec.Model);
        text = SetScalar(text, "model_reasoning_effort", spec.ReasoningEffort);
        text = SetScalar(text, "disable_response_storage", spec.DisableResponseStorage);
        text = SetScalar(text, "preferred_auth_method", "apikey");
        text = SetScalar(text, "model_auto_compact_token_limit", spec.ModelAutoCompactTokenLimit);
        if (spec.UseOpenAiThreadView)
        {
            return text;
        }

        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "name", effectiveProvider);
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "base_url", spec.BaseUrl);
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "wire_api", spec.WireApi);
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "requires_openai_auth", spec.RequiresOpenAiAuth);
        return text;
    }

    public static string? TryReadScalar(string text, string key)
    {
        return TryReadLiteral(text, $@"(?m)^{Regex.Escape(key)}\s*=\s*(?<value>.+?)\s*$");
    }

    public static string? TryReadSectionScalar(string text, string section, string key)
    {
        var sectionMatch = Regex.Match(
            text,
            $@"(?ms)^\[{Regex.Escape(section)}\]\s*$\r?\n(?<body>.*?)(?=^\[|\z)");
        if (!sectionMatch.Success)
        {
            return null;
        }

        return TryReadLiteral(sectionMatch.Groups["body"].Value, $@"(?m)^{Regex.Escape(key)}\s*=\s*(?<value>.+?)\s*$");
    }

    public static string SetScalar(string text, string key, object value)
    {
        var literal = ToTomlLiteral(value);
        var line = $"{key} = {literal}";
        var pattern = $@"(?m)^(?<!\s*\[){Regex.Escape(key)}\s*=.*$";
        if (Regex.IsMatch(text, pattern))
        {
            return new Regex(pattern).Replace(text, line, 1);
        }

        var sectionHeader = Regex.Match(text, @"(?m)^\[");
        if (sectionHeader.Success)
        {
            return text.Insert(sectionHeader.Index, line + Environment.NewLine);
        }

        var trimmed = text.TrimEnd('\r', '\n');
        return trimmed.Length == 0
            ? line + Environment.NewLine
            : trimmed + Environment.NewLine + line + Environment.NewLine;
    }

    public static string RemoveSection(string text, string section)
    {
        var pattern = $@"(?ms)^\[{Regex.Escape(section)}\]\s*$\r?\n.*?(?=^\[|\z)";
        var match = Regex.Match(text, pattern);
        if (!match.Success)
        {
            return text;
        }

        var updated = text.Remove(match.Index, match.Length);
        return Regex.Replace(updated, @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
    }

    public static string SetSectionScalar(string text, string section, string key, object value)
    {
        var literal = ToTomlLiteral(value);
        var line = $"{key} = {literal}";
        var escapedSection = Regex.Escape(section);
        var sectionPattern = $@"(?ms)(^\[{escapedSection}\]\s*$\r?\n)(?<body>.*?)(?=^\[|\z)";
        var match = Regex.Match(text, sectionPattern);
        if (!match.Success)
        {
            var trimmed = text.TrimEnd('\r', '\n');
            var addition = $"[{section}]{Environment.NewLine}{line}{Environment.NewLine}";
            return trimmed.Length == 0
                ? addition
                : trimmed + Environment.NewLine + Environment.NewLine + addition;
        }

        var body = match.Groups["body"].Value;
        var keyPattern = $@"(?m)^{Regex.Escape(key)}\s*=.*$";
        string updatedBody;
        if (Regex.IsMatch(body, keyPattern))
        {
            updatedBody = new Regex(keyPattern).Replace(body, line, 1);
        }
        else
        {
            var bodyTrimmed = body.TrimEnd('\r', '\n');
            updatedBody = bodyTrimmed.Length == 0
                ? line + Environment.NewLine
                : bodyTrimmed + Environment.NewLine + line + Environment.NewLine;
        }

        return text[..match.Index] + match.Groups[1].Value + updatedBody + text[(match.Index + match.Length)..];
    }

    private static string ToTomlLiteral(object value)
    {
        return value switch
        {
            bool boolValue => boolValue ? "true" : "false",
            int intValue => intValue.ToString(),
            long longValue => longValue.ToString(),
            _ => "\"" + value.ToString()!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        };
    }

    private static string? TryReadLiteral(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        if (!match.Success)
        {
            return null;
        }

        var rawValue = match.Groups["value"].Value.Trim();
        if (rawValue.StartsWith("\"", StringComparison.Ordinal) && rawValue.EndsWith("\"", StringComparison.Ordinal) && rawValue.Length >= 2)
        {
            return rawValue[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return rawValue;
    }
}

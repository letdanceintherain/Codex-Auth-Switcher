using System.Text.RegularExpressions;
using CodexAuthSwitcher.Core.Models;
using Tomlyn.Parsing;

namespace CodexAuthSwitcher.Core.Services;

public static class TomlOverlayService
{
    private static readonly string[] ManagedTopLevelKeys =
    [
        "model_provider",
        "model",
        "model_reasoning_effort",
        "model_auto_compact_token_limit",
        "openai_base_url"
    ];

    public static string ApplyApiOverlay(string baseConfigText, ApiProfileSpec spec)
    {
        var effectiveProvider = spec.GetEffectiveProviderId();
        var text = RemoveSection(baseConfigText, "model_providers.openai");
        text = RemoveScalar(text, "preferred_auth_method");
        text = RemoveScalar(text, "disable_response_storage");
        text = SetScalar(text, "model_provider", effectiveProvider);
        text = SetScalar(text, "model", spec.Model);
        text = SetScalar(text, "model_reasoning_effort", spec.ReasoningEffort);
        text = SetScalar(text, "model_auto_compact_token_limit", spec.ModelAutoCompactTokenLimit);
        if (spec.UseOpenAiThreadView)
        {
            text = SetScalar(text, "openai_base_url", NormalizeBaseUrl(spec.BaseUrl));
            Validate(text);
            return text;
        }

        text = RemoveScalar(text, "openai_base_url");
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "name", spec.GetDisplayProvider());
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "base_url", NormalizeBaseUrl(spec.BaseUrl));
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "wire_api", spec.WireApi);
        text = SetSectionScalar(text, $"model_providers.{effectiveProvider}", "requires_openai_auth", spec.RequiresOpenAiAuth);
        Validate(text);
        return text;
    }

    public static string ApplyChatGptOverlay(string currentConfigText, string snapshotConfigText)
    {
        var text = RemoveSection(currentConfigText, "model_providers.openai");
        text = RemoveScalar(text, "preferred_auth_method");
        text = RemoveScalar(text, "disable_response_storage");

        foreach (var key in ManagedTopLevelKeys)
        {
            var rawValue = TryReadRawScalar(snapshotConfigText, key);
            text = rawValue is null
                ? RemoveScalar(text, key)
                : SetRawScalar(text, key, rawValue);
        }

        Validate(text);
        return text;
    }

    public static string ExtractManagedOverlay(string configText)
    {
        Validate(configText);
        var newline = DetectNewline(configText);
        var lines = ManagedTopLevelKeys
            .Select(key => (Key: key, RawValue: TryReadRawScalar(configText, key)))
            .Where(item => item.RawValue is not null)
            .Select(item => $"{item.Key} = {item.RawValue}");
        var overlay = string.Join(newline, lines);
        return overlay.Length == 0 ? string.Empty : overlay + newline;
    }

    public static void Validate(string text)
    {
        var document = SyntaxParser.Parse(text, "config.toml", validate: true);
        if (!document.HasErrors)
        {
            return;
        }

        var diagnostics = string.Join(Environment.NewLine, document.Diagnostics.Select(item => item.ToString()));
        throw new InvalidOperationException($"The generated Codex config.toml is invalid:{Environment.NewLine}{diagnostics}");
    }

    public static string? TryReadScalar(string text, string key)
    {
        var rawValue = TryReadRawScalar(text, key);
        return rawValue is null ? null : ParseLiteral(rawValue);
    }

    public static string? TryReadSectionScalar(string text, string section, string key)
    {
        var sectionSpan = FindSection(text, section);
        if (sectionSpan is null)
        {
            return null;
        }

        var span = sectionSpan.Value;
        var rawValue = TryReadRawValue(text[span.BodyStart..span.EndIndex], key);
        return rawValue is null ? null : ParseLiteral(rawValue);
    }

    public static string SetScalar(string text, string key, object value)
    {
        return SetRawScalar(text, key, ToTomlLiteral(value));
    }

    public static string RemoveScalar(string text, string key)
    {
        var sectionIndex = FindFirstSectionIndex(text);
        var prefix = sectionIndex >= 0 ? text[..sectionIndex] : text;
        var suffix = sectionIndex >= 0 ? text[sectionIndex..] : string.Empty;
        var pattern = $@"(?m)^[ \t]*{Regex.Escape(key)}[ \t]*=.*(?:\r?\n|$)";
        var updatedPrefix = Regex.Replace(prefix, pattern, string.Empty);
        return NormalizeSpacing(updatedPrefix + suffix, DetectNewline(text));
    }

    public static string RemoveSection(string text, string section)
    {
        var sectionSpan = FindSection(text, section);
        if (sectionSpan is null)
        {
            return text;
        }

        var span = sectionSpan.Value;
        var updated = text.Remove(span.HeaderIndex, span.EndIndex - span.HeaderIndex);
        return NormalizeSpacing(updated, DetectNewline(text));
    }

    public static string SetSectionScalar(string text, string section, string key, object value)
    {
        var newline = DetectNewline(text);
        var literal = ToTomlLiteral(value);
        var line = $"{key} = {literal}";
        var sectionSpan = FindSection(text, section);
        if (sectionSpan is null)
        {
            var trimmed = text.TrimEnd('\r', '\n');
            var addition = $"[{section}]{newline}{line}{newline}";
            return trimmed.Length == 0
                ? addition
                : trimmed + newline + newline + addition;
        }

        var span = sectionSpan.Value;
        var body = text[span.BodyStart..span.EndIndex];
        var keyPattern = $@"(?m)^[ \t]*{Regex.Escape(key)}[ \t]*=.*$";
        string updatedBody;
        if (Regex.IsMatch(body, keyPattern))
        {
            updatedBody = new Regex(keyPattern).Replace(body, line, 1);
        }
        else
        {
            var bodyTrimmed = body.TrimEnd('\r', '\n');
            updatedBody = bodyTrimmed.Length == 0
                ? line + newline
                : bodyTrimmed + newline + line + newline;
        }

        return text[..span.BodyStart] + updatedBody + text[span.EndIndex..];
    }

    private static string SetRawScalar(string text, string key, string rawValue)
    {
        var newline = DetectNewline(text);
        var line = $"{key} = {rawValue}";
        var sectionIndex = FindFirstSectionIndex(text);
        var prefix = sectionIndex >= 0 ? text[..sectionIndex] : text;
        var suffix = sectionIndex >= 0 ? text[sectionIndex..] : string.Empty;
        var pattern = $@"(?m)^[ \t]*{Regex.Escape(key)}[ \t]*=.*$";
        if (Regex.IsMatch(prefix, pattern))
        {
            var updatedPrefix = new Regex(pattern).Replace(prefix, line, 1);
            return updatedPrefix + suffix;
        }

        var trimmedPrefix = prefix.TrimEnd('\r', '\n');
        var updated = trimmedPrefix.Length == 0
            ? line + newline
            : trimmedPrefix + newline + line + newline;
        if (suffix.Length > 0 && !updated.EndsWith(newline + newline, StringComparison.Ordinal))
        {
            updated += newline;
        }

        return updated + suffix;
    }

    private static string? TryReadRawScalar(string text, string key)
    {
        var sectionIndex = FindFirstSectionIndex(text);
        var prefix = sectionIndex >= 0 ? text[..sectionIndex] : text;
        return TryReadRawValue(prefix, key);
    }

    private static string? TryReadRawValue(string text, string key)
    {
        var match = Regex.Match(text, $@"(?m)^[ \t]*{Regex.Escape(key)}[ \t]*=[ \t]*(?<value>.+?)[ \t]*$");
        return match.Success ? match.Groups["value"].Value.Trim() : null;
    }

    private static int FindFirstSectionIndex(string text)
    {
        var sectionHeader = Regex.Match(text, @"(?m)^[ \t]*\[");
        return sectionHeader.Success ? sectionHeader.Index : -1;
    }

    private static SectionSpan? FindSection(string text, string section)
    {
        var headerPattern = $@"(?m)^[ \t]*\[{Regex.Escape(section)}\][ \t]*(?:\r?\n|$)";
        var headerMatch = Regex.Match(text, headerPattern);
        if (!headerMatch.Success)
        {
            return null;
        }

        var bodyStart = headerMatch.Index + headerMatch.Length;
        var nextHeaderMatch = new Regex(@"(?m)^[ \t]*\[", RegexOptions.None, TimeSpan.FromSeconds(1)).Match(text, bodyStart);
        var endIndex = nextHeaderMatch.Success ? nextHeaderMatch.Index : text.Length;
        return new SectionSpan(headerMatch.Index, headerMatch.Length, bodyStart, endIndex);
    }

    private static string ToTomlLiteral(object value)
    {
        return value switch
        {
            bool boolValue => boolValue ? "true" : "false",
            int intValue => intValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            long longValue => longValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "\"" + value.ToString()!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        };
    }

    private static string ParseLiteral(string rawValue)
    {
        rawValue = StripInlineComment(rawValue);
        if (rawValue.StartsWith('"') && rawValue.EndsWith('"') && rawValue.Length >= 2)
        {
            return rawValue[1..^1]
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        if (rawValue.StartsWith('\'') && rawValue.EndsWith('\'') && rawValue.Length >= 2)
        {
            return rawValue[1..^1];
        }

        return rawValue;
    }

    private static string StripInlineComment(string rawValue)
    {
        var inBasicString = false;
        var inLiteralString = false;
        var escaped = false;
        for (var index = 0; index < rawValue.Length; index++)
        {
            var character = rawValue[index];
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (character == '\\' && inBasicString)
            {
                escaped = true;
                continue;
            }

            if (character == '"' && !inLiteralString)
            {
                inBasicString = !inBasicString;
                continue;
            }

            if (character == '\'' && !inBasicString)
            {
                inLiteralString = !inLiteralString;
                continue;
            }

            if (character == '#' && !inBasicString && !inLiteralString)
            {
                return rawValue[..index].TrimEnd();
            }
        }

        return rawValue.Trim();
    }

    private static string NormalizeBaseUrl(string value)
    {
        return value.Trim().TrimEnd('/');
    }

    private static string DetectNewline(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string NormalizeSpacing(string text, string newline)
    {
        var normalized = Regex.Replace(text, @"(?:\r?\n){3,}", newline + newline);
        return normalized.TrimStart('\r', '\n');
    }

    private readonly record struct SectionSpan(int HeaderIndex, int HeaderLength, int BodyStart, int EndIndex);
}

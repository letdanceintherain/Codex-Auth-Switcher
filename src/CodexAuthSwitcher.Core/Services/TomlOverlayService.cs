using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

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
        var provider = spec.GetEffectiveProviderId();
        if (provider is "ollama" or "lmstudio")
            throw new InvalidOperationException("This provider ID is reserved by Codex. Choose a custom provider name.");
        if (spec.WireApi != "responses")
            throw new InvalidOperationException("Codex API profiles require the responses wire API.");
        var text = RemoveSection(baseConfigText, "model_providers.openai");
        text = RemoveScalar(text, "preferred_auth_method");
        text = RemoveScalar(text, "disable_response_storage");
        text = SetScalar(text, "model_provider", provider);
        text = SetScalar(text, "model", spec.Model);
        text = SetScalar(text, "model_reasoning_effort", spec.ReasoningEffort);
        text = SetScalar(text, "model_auto_compact_token_limit", spec.ModelAutoCompactTokenLimit);
        if (provider == "openai")
        {
            text = SetScalar(text, "openai_base_url", spec.BaseUrl.Trim().TrimEnd('/'));
        }
        else
        {
            text = RemoveScalar(text, "openai_base_url");
            var section = ProviderSection(provider);
            // Clear competing auth mechanisms, preserving custom headers/timeouts.
            foreach (var key in new[] { "env_key", "env_key_instructions", "experimental_bearer_token" })
                text = RemoveSectionScalar(text, section, key);
            text = RemoveSection(text, section + ".auth");
            text = SetSectionScalar(text, section, "name", spec.GetDisplayProvider());
            text = SetSectionScalar(text, section, "base_url", spec.BaseUrl.Trim().TrimEnd('/'));
            text = SetSectionScalar(text, section, "wire_api", spec.WireApi);
            text = SetSectionScalar(text, section, "requires_openai_auth", true);
        }
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
            var raw = TryReadRawScalar(snapshotConfigText, key);
            text = raw is null ? RemoveScalar(text, key) : SetRawScalar(text, key, raw);
        }
        // A login captured while API routing was selected must return to OpenAI.
        text = SetScalar(text, "model_provider", "openai");
        text = RemoveScalar(text, "openai_base_url");
        Validate(text);
        return text;
    }

    public static string ExtractManagedOverlay(string configText)
    {
        Validate(configText);
        return string.Concat(ManagedTopLevelKeys.Select(key =>
            TryReadRawScalar(configText, key) is { } value ? $"{key} = {value}{Newline(configText)}" : ""));
    }

    public static void Validate(string text) => Parse(text);
    public static string ProviderSection(string provider) =>
        "model_providers." + (BareKeySyntax.IsBareKey(provider) ? provider : Literal(provider));
    public static string? TryReadScalar(string text, string key) =>
        ReadValue(FindKey(Parse(text).KeyValues, key)?.Value, text);
    public static string? TryReadSectionScalar(string text, string section, string key) =>
        ReadValue(FindTable(Parse(text), section)?.Items is { } items ? FindKey(items, key)?.Value : null, text);
    public static string SetScalar(string text, string key, object value) => SetRawScalar(text, key, Literal(value));
    public static string RemoveScalar(string text, string key) =>
        FindKey(Parse(text).KeyValues, key) is { } item ? RemoveNode(text, item) : text;

    public static string RemoveSection(string text, string section)
    {
        var parts = TableParts(section);
        foreach (var table in Parse(text).Tables.Where(table =>
                     Parts(table.Name).Take(parts.Length).SequenceEqual(parts)).OrderByDescending(table => table.Span.Offset))
            text = RemoveNode(text, table);
        return text;
    }

    public static string SetSectionScalar(string text, string section, string key, object value)
    {
        var document = Parse(text);
        var table = FindTable(document, section);
        if (table is null)
            return text + (text.Length == 0 ? "" : Newline(text)) + $"[{section}]{Newline(text)}{key} = {Literal(value)}{Newline(text)}";
        if (FindKey(table.Items, key) is { } item)
            return ReplaceNode(text, item.Value!, Literal(value));
        var end = table.Span.Offset + table.Span.Length;
        var separator = end > 0 && text[end - 1] != '\n' ? Newline(text) : "";
        return text.Insert(end, $"{separator}{key} = {Literal(value)}{Newline(text)}");
    }

    private static string RemoveSectionScalar(string text, string section, string key) =>
        FindTable(Parse(text), section)?.Items is { } items && FindKey(items, key) is { } item
            ? RemoveNode(text, item) : text;
    private static string SetRawScalar(string text, string key, string value)
    {
        var document = Parse(text);
        if (FindKey(document.KeyValues, key) is { } item) return ReplaceNode(text, item.Value!, value);
        var index = document.Tables.FirstOrDefault()?.Span.Offset ?? text.Length;
        var separator = index > 0 && text[index - 1] != '\n' ? Newline(text) : "";
        return text.Insert(index, $"{separator}{key} = {value}{Newline(text)}");
    }
    private static string? TryReadRawScalar(string text, string key) =>
        FindKey(Parse(text).KeyValues, key)?.Value is { } value ? Slice(text, value) : null;
    private static string? ReadValue(ValueSyntax? value, string text) => value switch
    {
        null => null,
        StringValueSyntax str => str.Value!,
        _ => Slice(text, value)
    };
    private static KeyValueSyntax? FindKey(IEnumerable<KeyValueSyntax> items, string key) =>
        items.FirstOrDefault(item => Parts(item.Key).SequenceEqual(new[] { key }));
    private static TableSyntaxBase? FindTable(DocumentSyntax document, string section) =>
        document.Tables.FirstOrDefault(table => Parts(table.Name).SequenceEqual(TableParts(section)));
    private static string[] TableParts(string section) => Parts(Parse($"[{section}]\n").Tables.First().Name).ToArray();
    private static IEnumerable<string> Parts(KeySyntax? key)
    {
        if (key is null) yield break;
        yield return KeyText(key.Key);
        foreach (var item in key.DotKeys) yield return KeyText(item.Key);
    }
    private static string KeyText(BareKeyOrStringValueSyntax? key) => key switch
    {
        BareKeySyntax bare => bare.Key!.Text!,
        StringValueSyntax str => str.Value!,
        _ => throw new InvalidOperationException("Unsupported TOML key.")
    };
    private static DocumentSyntax Parse(string text)
    {
        var document = SyntaxParser.Parse(text, "config.toml", validate: true);
        if (document.HasErrors)
            throw new InvalidOperationException("Invalid Codex config.toml: " + string.Join(Environment.NewLine, document.Diagnostics));
        return document;
    }
    private static string Literal(object value) => value switch
    {
        bool flag => flag ? "true" : "false",
        int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        long number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => JsonSerializer.Serialize(value.ToString())
    };
    private static string Slice(string text, SyntaxNodeBase node) => text.Substring(node.Span.Offset, node.Span.Length);
    private static string ReplaceNode(string text, SyntaxNodeBase node, string value) =>
        text.Remove(node.Span.Offset, node.Span.Length).Insert(node.Span.Offset, value);
    private static string RemoveNode(string text, SyntaxNodeBase node) => ReplaceNode(text, node, "");
    private static string Newline(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
}

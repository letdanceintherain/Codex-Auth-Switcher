using System.Runtime.InteropServices;
using CodexAuthSwitcher.Core.Models;

namespace CodexAuthSwitcher.Core.Services;

public sealed class CodexRuntimeEnvironmentService : ICodexRuntimeEnvironmentService
{
    private const string OpenAiBaseUrlVariableName = "OPENAI_BASE_URL";
    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;
    private static readonly nint HwndBroadcast = new(0xffff);

    public string? GetOpenAiBaseUrl()
    {
        return NormalizeBaseUrl(Environment.GetEnvironmentVariable(OpenAiBaseUrlVariableName))
            ?? NormalizeBaseUrl(Environment.GetEnvironmentVariable(OpenAiBaseUrlVariableName, EnvironmentVariableTarget.User));
    }

    public void ApplyForProfile(ApiProfileSpec? spec)
    {
        // Version 1.0 persisted this variable for compatibility mode. Current
        // Codex reads openai_base_url from config.toml, so remove the legacy
        // global override to keep all routing state in the managed config.
        SetOpenAiBaseUrl(null);
    }

    private static void SetOpenAiBaseUrl(string? value)
    {
        var normalized = NormalizeBaseUrl(value);
        Environment.SetEnvironmentVariable(OpenAiBaseUrlVariableName, normalized);

        try
        {
            Environment.SetEnvironmentVariable(OpenAiBaseUrlVariableName, normalized, EnvironmentVariableTarget.User);
            BroadcastEnvironmentChange();
        }
        catch
        {
            // The direct restart path still inherits the process-level override.
        }
    }

    private static string? NormalizeBaseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().TrimEnd('/');
    }

    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout(HwndBroadcast, WmSettingChange, nint.Zero, "Environment", SmtoAbortIfHung, 2000, out _);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeout(
        nint hWnd,
        uint msg,
        nint wParam,
        string lParam,
        uint flags,
        uint timeout,
        out nint result);
}

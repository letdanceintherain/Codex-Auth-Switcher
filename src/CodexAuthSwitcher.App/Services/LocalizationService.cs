using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using CodexAuthSwitcher.App.Models;

namespace CodexAuthSwitcher.App.Services;

public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<AppLanguage, IReadOnlyDictionary<string, string>> Resources =
        new Dictionary<AppLanguage, IReadOnlyDictionary<string, string>>
        {
            [AppLanguage.ZhCn] = BuildChineseResources(),
            [AppLanguage.EnUs] = BuildEnglishResources()
        };

    private string? _settingsPath;
    private AppLanguage _currentLanguage = AppLanguage.ZhCn;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? LanguageChanged;

    public AppLanguage CurrentLanguage
    {
        get => _currentLanguage;
        private set
        {
            if (_currentLanguage == value)
            {
                return;
            }

            _currentLanguage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsChineseSelected));
            OnPropertyChanged(nameof(IsEnglishSelected));
            OnPropertyChanged("Item[]");
            LanguageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsChineseSelected => CurrentLanguage == AppLanguage.ZhCn;
    public bool IsEnglishSelected => CurrentLanguage == AppLanguage.EnUs;

    public string this[string key] => Translate(key);

    public void Initialize(string codexHomePath)
    {
        var defaultLanguage = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.ZhCn
            : AppLanguage.EnUs;

        _settingsPath = Path.Combine(codexHomePath, "auth-switcher", "settings.json");
        var loadedLanguage = defaultLanguage;
        if (File.Exists(_settingsPath))
        {
            try
            {
                var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(_settingsPath));
                loadedLanguage = ParseLanguage(settings?.Language, defaultLanguage);
            }
            catch
            {
                loadedLanguage = defaultLanguage;
            }
        }

        CurrentLanguage = loadedLanguage;
    }

    public void SetLanguage(AppLanguage language)
    {
        if (CurrentLanguage == language)
        {
            return;
        }

        CurrentLanguage = language;
        SaveSettings();
    }

    public string Translate(string key)
    {
        if (Resources.TryGetValue(CurrentLanguage, out var set) && set.TryGetValue(key, out var value))
        {
            return value;
        }

        if (Resources[AppLanguage.EnUs].TryGetValue(key, out var fallback))
        {
            return fallback;
        }

        return key;
    }

    public string Format(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, Translate(key), args);
    }

    private void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(_settingsPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var settings = new UiSettings
        {
            Language = CurrentLanguage == AppLanguage.ZhCn ? "zh-CN" : "en-US"
        };
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(_settingsPath, json);
    }

    private static AppLanguage ParseLanguage(string? raw, AppLanguage fallback)
    {
        return raw?.ToLowerInvariant() switch
        {
            "zh" or "zh-cn" => AppLanguage.ZhCn,
            "en" or "en-us" => AppLanguage.EnUs,
            _ => fallback
        };
    }

    private static IReadOnlyDictionary<string, string> BuildChineseResources()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Window.Main.Title"] = "Codex 认证切换器",
            ["Window.ApiEditor.CreateTitle"] = "新建 API 配置",
            ["Window.ApiEditor.EditTitle"] = "编辑 API 配置",
            ["Window.Snapshot.Title"] = "保存 ChatGPT 快照",
            ["Window.RunningCodex.Title"] = "Codex 正在运行",
            ["Header.Title"] = "Codex 认证切换器",
            ["Header.Subtitle"] = "只切换 config.toml 和 auth.json，不碰本地线程库。",
            ["Common.Chinese"] = "中文",
            ["Common.English"] = "English",
            ["Common.Refresh"] = "刷新",
            ["Common.CaptureChatGpt"] = "保存 ChatGPT 快照",
            ["Common.NewApiProfile"] = "新建 API 配置",
            ["Common.Edit"] = "编辑",
            ["Common.Delete"] = "删除",
            ["Common.SwitchToSelected"] = "切换到选中配置",
            ["Common.Cancel"] = "取消",
            ["Common.Save"] = "保存",
            ["Common.Create"] = "创建",
            ["Common.Update"] = "更新",
            ["Common.Name"] = "名称",
            ["Common.Kind"] = "类型",
            ["Common.Identity"] = "身份",
            ["Common.Details"] = "详情",
            ["Common.CurrentAuth"] = "当前认证",
            ["Common.ThreadLibrary"] = "线程库",
            ["Common.Untracked"] = "未收录",
            ["Common.Unknown"] = "未知",
            ["Common.Current"] = "当前",
            ["Common.None"] = "无",
            ["Language.Current"] = "界面语言",
            ["Card.CodexHome"] = "Codex 目录",
            ["Card.CurrentAuth"] = "当前认证",
            ["Card.CurrentProfile"] = "当前配置",
            ["Card.DesktopCodex"] = "桌面版 Codex",
            ["Card.Running"] = "运行中: {0}",
            ["Card.Provider"] = "Provider: {0}",
            ["Card.Model"] = "Model: {0}",
            ["Section.Profiles"] = "配置列表",
            ["Section.Profiles.Subtitle"] = "管理 ChatGPT 快照和 API 配置。",
            ["Section.SelectedProfile"] = "选中配置",
            ["Section.SelectedProfile.Subtitle"] = "每次切换前都会自动备份当前 live 的 config.toml 和 auth.json。",
            ["Section.CurrentProfileKind"] = "当前配置类型",
            ["Section.ThreadLibraryValue"] = "不会被修改（sessions / session_index / state_5.sqlite）",
            ["ProfileKind.ChatGpt"] = "ChatGPT 快照",
            ["ProfileKind.Api"] = "API 配置",
            ["Profile.Subtitle.ChatGpt.Email"] = "账号邮箱: {0}",
            ["Profile.Subtitle.ChatGpt.Account"] = "账号 ID: {0}",
            ["Profile.Subtitle.ChatGpt.Generic"] = "恢复完整的 config.toml 和 auth.json",
            ["Profile.Subtitle.Api"] = "{0} @ {1}",
            ["Profile.Detail.ChatGpt"] = "最近更新: {0}",
            ["Profile.Detail.ChatGpt.AutoCaptured"] = "最近更新: {0} · 自动收录",
            ["Profile.Detail.Api"] = "{0} · {1}",
            ["LiveIdentity.ChatGpt"] = "ChatGPT 账号",
            ["LiveIdentity.Api"] = "API 配置",
            ["LiveIdentity.ChatGpt.Secondary"] = "账号 ID: {0}",
            ["LiveIdentity.Api.Secondary"] = "{0} · {1}",
            ["AuthMode.chatgpt"] = "ChatGPT",
            ["AuthMode.apikey"] = "API Key",
            ["AuthMode.unknown"] = "未知",
            ["Status.Ready"] = "就绪",
            ["Status.LoadedProfiles"] = "已加载 {0} 个配置。",
            ["Status.AutoCapturedChatGpt"] = "已自动收录新 ChatGPT 账号“{0}”。",
            ["Status.ApiUnmanaged"] = "当前 API 配置未纳入管理，仍可通过界面新建或编辑 API 配置。",
            ["Status.SavedChatGpt"] = "已保存 ChatGPT 快照“{0}”。",
            ["Status.SavedApi"] = "已保存 API 配置“{0}”。",
            ["Status.UpdatedApi"] = "已更新 API 配置“{0}”。",
            ["Status.DeletedProfile"] = "已删除配置“{0}”。",
            ["Status.SwitchedRestarted"] = "已切换到“{0}”，并通过 {1} 重启 Codex。",
            ["Status.SwitchedRestartFailed"] = "已切换到“{0}”，但 Codex 自动重启失败。",
            ["Dialog.Delete.Title"] = "删除配置",
            ["Dialog.Delete.Message"] = "确定要删除配置“{0}”吗？",
            ["Dialog.Error.Title"] = "Codex 认证切换器",
            ["Dialog.EditProfileError.Title"] = "编辑配置",
            ["Dialog.EditProfileError.Message"] = "无法读取这个 API 配置。",
            ["Dialog.CodexRunning.Title"] = "Codex 正在运行",
            ["Dialog.CodexRunning.Info"] = "请先关闭桌面版 Codex，再重新尝试切换。",
            ["Dialog.RestartFailed.Title"] = "重启失败",
            ["Dialog.RestartFailed.Message"] = "配置切换已经完成，但 Codex 没有自动重启。你可以手动重新打开。",
            ["Dialog.Snapshot.Header"] = "为这次登录起一个配置名，后续就能恢复相同的 config 和 auth。",
            ["Dialog.Snapshot.Name"] = "配置名称",
            ["Dialog.Snapshot.ValidationTitle"] = "快照名称",
            ["Dialog.Snapshot.ValidationMessage"] = "请输入配置名称。",
            ["Dialog.ApiEditor.CreateHeader"] = "创建一个可复用的 API 登录配置，API key 会加密保存。",
            ["Dialog.ApiEditor.EditHeader"] = "更新这个已保存 API 登录的提供方、模型和 token 设置。",
            ["Dialog.ApiEditor.ProfileName"] = "配置名称",
            ["Dialog.ApiEditor.Provider"] = "Provider",
            ["Dialog.ApiEditor.BaseUrl"] = "Base URL",
            ["Dialog.ApiEditor.Model"] = "Model",
            ["Dialog.ApiEditor.ReasoningEffort"] = "推理强度",
            ["Dialog.ApiEditor.WireApi"] = "Wire API",
            ["Dialog.ApiEditor.RequiresOpenAiAuth"] = "需要 OpenAI 认证",
            ["Dialog.ApiEditor.UseOpenAiThreadView"] = "沿用本地对话框视图",
            ["Dialog.ApiEditor.DisableResponseStorage"] = "禁用响应存储",
            ["Dialog.ApiEditor.TokenLimit"] = "自动压缩 token 上限",
            ["Dialog.ApiEditor.ApiKey"] = "API Key",
            ["Dialog.ApiEditor.RequiresOpenAiAuth.Checkbox"] = "开启 requires_openai_auth",
            ["Dialog.ApiEditor.UseOpenAiThreadView.Checkbox"] = "使用 openai provider 空间复用桌面端线程视图",
            ["Dialog.ApiEditor.DisableResponseStorage.Checkbox"] = "开启 disable_response_storage",
            ["Dialog.ApiEditor.ValidationTitle"] = "API 配置",
            ["Dialog.ApiEditor.ValidationName"] = "请输入配置名称。",
            ["Dialog.ApiEditor.ValidationApiKey"] = "请输入 API key。",
            ["Dialog.ApiEditor.ValidationTokenLimit"] = "自动压缩 token 上限必须是正整数。",
            ["Dialog.RunningCodex.Header"] = "桌面版应用当前仍在运行。请选择接下来的处理方式。",
            ["Dialog.RunningCodex.Recommended"] = "推荐",
            ["Dialog.RunningCodex.RecommendedBody"] = "自动关闭应用、切换配置，然后重新启动 Codex。",
            ["Dialog.RunningCodex.ManualClose"] = "手动关闭",
            ["Dialog.RunningCodex.AutoClose"] = "自动关闭并重启"
        };
    }

    private static IReadOnlyDictionary<string, string> BuildEnglishResources()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Window.Main.Title"] = "Codex Auth Switcher",
            ["Window.ApiEditor.CreateTitle"] = "Create API Profile",
            ["Window.ApiEditor.EditTitle"] = "Edit API Profile",
            ["Window.Snapshot.Title"] = "Save ChatGPT Snapshot",
            ["Window.RunningCodex.Title"] = "Codex Is Running",
            ["Header.Title"] = "Codex Auth Switcher",
            ["Header.Subtitle"] = "Only changes config.toml and auth.json, never the local thread library.",
            ["Common.Chinese"] = "中文",
            ["Common.English"] = "English",
            ["Common.Refresh"] = "Refresh",
            ["Common.CaptureChatGpt"] = "Capture ChatGPT",
            ["Common.NewApiProfile"] = "New API Profile",
            ["Common.Edit"] = "Edit",
            ["Common.Delete"] = "Delete",
            ["Common.SwitchToSelected"] = "Switch To Selected",
            ["Common.Cancel"] = "Cancel",
            ["Common.Save"] = "Save",
            ["Common.Create"] = "Create",
            ["Common.Update"] = "Update",
            ["Common.Name"] = "Name",
            ["Common.Kind"] = "Kind",
            ["Common.Identity"] = "Identity",
            ["Common.Details"] = "Details",
            ["Common.CurrentAuth"] = "Current Auth",
            ["Common.ThreadLibrary"] = "Thread Library",
            ["Common.Untracked"] = "Untracked",
            ["Common.Unknown"] = "Unknown",
            ["Common.Current"] = "Current",
            ["Common.None"] = "None",
            ["Language.Current"] = "Language",
            ["Card.CodexHome"] = "Codex Home",
            ["Card.CurrentAuth"] = "Current Auth",
            ["Card.CurrentProfile"] = "Current Profile",
            ["Card.DesktopCodex"] = "Desktop Codex",
            ["Card.Running"] = "Running: {0}",
            ["Card.Provider"] = "Provider: {0}",
            ["Card.Model"] = "Model: {0}",
            ["Section.Profiles"] = "Profiles",
            ["Section.Profiles.Subtitle"] = "Manage ChatGPT snapshots and API profiles.",
            ["Section.SelectedProfile"] = "Selected Profile",
            ["Section.SelectedProfile.Subtitle"] = "The live config.toml and auth.json are backed up before each switch.",
            ["Section.CurrentProfileKind"] = "Current Profile Kind",
            ["Section.ThreadLibraryValue"] = "Will not be modified (sessions / session_index / state_5.sqlite)",
            ["ProfileKind.ChatGpt"] = "ChatGPT Snapshot",
            ["ProfileKind.Api"] = "API Profile",
            ["Profile.Subtitle.ChatGpt.Email"] = "Email: {0}",
            ["Profile.Subtitle.ChatGpt.Account"] = "Account ID: {0}",
            ["Profile.Subtitle.ChatGpt.Generic"] = "Restore the full config.toml and auth.json",
            ["Profile.Subtitle.Api"] = "{0} @ {1}",
            ["Profile.Detail.ChatGpt"] = "Last updated: {0}",
            ["Profile.Detail.ChatGpt.AutoCaptured"] = "Last updated: {0} · Auto-captured",
            ["Profile.Detail.Api"] = "{0} · {1}",
            ["LiveIdentity.ChatGpt"] = "ChatGPT Account",
            ["LiveIdentity.Api"] = "API Profile",
            ["LiveIdentity.ChatGpt.Secondary"] = "Account ID: {0}",
            ["LiveIdentity.Api.Secondary"] = "{0} · {1}",
            ["AuthMode.chatgpt"] = "ChatGPT",
            ["AuthMode.apikey"] = "API Key",
            ["AuthMode.unknown"] = "Unknown",
            ["Status.Ready"] = "Ready",
            ["Status.LoadedProfiles"] = "Loaded {0} profile(s).",
            ["Status.AutoCapturedChatGpt"] = "Auto-captured a new ChatGPT account as '{0}'.",
            ["Status.ApiUnmanaged"] = "The current API configuration is not managed yet. You can create or edit an API profile from the UI.",
            ["Status.SavedChatGpt"] = "Saved ChatGPT snapshot '{0}'.",
            ["Status.SavedApi"] = "Saved API profile '{0}'.",
            ["Status.UpdatedApi"] = "Updated API profile '{0}'.",
            ["Status.DeletedProfile"] = "Deleted profile '{0}'.",
            ["Status.SwitchedRestarted"] = "Switched to '{0}' and restarted Codex via {1}.",
            ["Status.SwitchedRestartFailed"] = "Switched to '{0}', but restarting Codex failed.",
            ["Dialog.Delete.Title"] = "Delete Profile",
            ["Dialog.Delete.Message"] = "Delete profile '{0}'?",
            ["Dialog.Error.Title"] = "Codex Auth Switcher",
            ["Dialog.EditProfileError.Title"] = "Edit Profile",
            ["Dialog.EditProfileError.Message"] = "Unable to read this API profile.",
            ["Dialog.CodexRunning.Title"] = "Codex Is Running",
            ["Dialog.CodexRunning.Info"] = "Please close the desktop Codex app first, then try the switch again.",
            ["Dialog.RestartFailed.Title"] = "Restart Failed",
            ["Dialog.RestartFailed.Message"] = "The configuration switch completed, but Codex did not restart automatically. You can reopen Codex manually.",
            ["Dialog.Snapshot.Header"] = "Give this login a profile name so we can restore the exact config and auth later.",
            ["Dialog.Snapshot.Name"] = "Profile Name",
            ["Dialog.Snapshot.ValidationTitle"] = "Snapshot Name",
            ["Dialog.Snapshot.ValidationMessage"] = "Please enter a profile name.",
            ["Dialog.ApiEditor.CreateHeader"] = "Create a reusable API login with encrypted key storage.",
            ["Dialog.ApiEditor.EditHeader"] = "Update the provider, model, and token settings for this saved API login.",
            ["Dialog.ApiEditor.ProfileName"] = "Profile Name",
            ["Dialog.ApiEditor.Provider"] = "Provider",
            ["Dialog.ApiEditor.BaseUrl"] = "Base URL",
            ["Dialog.ApiEditor.Model"] = "Model",
            ["Dialog.ApiEditor.ReasoningEffort"] = "Reasoning Effort",
            ["Dialog.ApiEditor.WireApi"] = "Wire API",
            ["Dialog.ApiEditor.RequiresOpenAiAuth"] = "OpenAI Auth Required",
            ["Dialog.ApiEditor.UseOpenAiThreadView"] = "Local Thread View Compatibility",
            ["Dialog.ApiEditor.DisableResponseStorage"] = "Disable Response Storage",
            ["Dialog.ApiEditor.TokenLimit"] = "Auto Compact Token Limit",
            ["Dialog.ApiEditor.ApiKey"] = "API Key",
            ["Dialog.ApiEditor.RequiresOpenAiAuth.Checkbox"] = "Enable requires_openai_auth",
            ["Dialog.ApiEditor.UseOpenAiThreadView.Checkbox"] = "Use openai provider space for the desktop thread view",
            ["Dialog.ApiEditor.DisableResponseStorage.Checkbox"] = "Enable disable_response_storage",
            ["Dialog.ApiEditor.ValidationTitle"] = "API Profile",
            ["Dialog.ApiEditor.ValidationName"] = "Profile name is required.",
            ["Dialog.ApiEditor.ValidationApiKey"] = "API key is required.",
            ["Dialog.ApiEditor.ValidationTokenLimit"] = "Auto compact token limit must be a positive integer.",
            ["Dialog.RunningCodex.Header"] = "The desktop app is currently open. Choose how you want to continue.",
            ["Dialog.RunningCodex.Recommended"] = "Recommended",
            ["Dialog.RunningCodex.RecommendedBody"] = "Auto close the app, switch the profile, then launch Codex again.",
            ["Dialog.RunningCodex.ManualClose"] = "Manual Close",
            ["Dialog.RunningCodex.AutoClose"] = "Auto Close and Restart"
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

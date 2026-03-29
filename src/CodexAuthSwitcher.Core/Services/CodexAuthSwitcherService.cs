using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Utilities;

namespace CodexAuthSwitcher.Core.Services;

public sealed class CodexAuthSwitcherService
{
    private readonly ProfileStore _profileStore;
    private readonly LiveAuthInspector _liveAuthInspector;
    private readonly ICodexRuntimeEnvironmentService _runtimeEnvironment;

    public CodexAuthSwitcherService(string codexHomePath)
    {
        _runtimeEnvironment = new CodexRuntimeEnvironmentService();
        _liveAuthInspector = new LiveAuthInspector(_runtimeEnvironment);
        _profileStore = new ProfileStore(codexHomePath, new ProtectedSecretStore(), _liveAuthInspector);
        ConfigPath = Path.Combine(_profileStore.CodexHomePath, "config.toml");
        AuthPath = Path.Combine(_profileStore.CodexHomePath, "auth.json");
    }

    public CodexAuthSwitcherService(
        ProfileStore profileStore,
        LiveAuthInspector liveAuthInspector,
        ICodexRuntimeEnvironmentService? runtimeEnvironment = null)
    {
        _profileStore = profileStore;
        _liveAuthInspector = liveAuthInspector;
        _runtimeEnvironment = runtimeEnvironment ?? new CodexRuntimeEnvironmentService();
        ConfigPath = Path.Combine(_profileStore.CodexHomePath, "config.toml");
        AuthPath = Path.Combine(_profileStore.CodexHomePath, "auth.json");
    }

    public string ConfigPath { get; }
    public string AuthPath { get; }

    public CodexEnvironment GetEnvironment()
    {
        EnsureLiveFilesExist();
        var configText = File.ReadAllText(ConfigPath);
        var authText = File.ReadAllText(AuthPath);
        var liveIdentity = _liveAuthInspector.Inspect(configText, authText);
        return new CodexEnvironment
        {
            CodexHomePath = _profileStore.CodexHomePath,
            ConfigPath = ConfigPath,
            AuthPath = AuthPath,
            CurrentAuthMode = liveIdentity?.AuthMode ?? ReadCurrentAuthMode(authText),
            ModelProvider = TomlOverlayService.TryReadScalar(configText, "model_provider") ?? "(default)",
            Model = TomlOverlayService.TryReadScalar(configText, "model") ?? "(default)",
            LiveIdentity = liveIdentity,
            CurrentProfile = _profileStore.ReadCurrentProfileState()
        };
    }

    public IdentitySyncResult EnsureCurrentIdentityTracked()
    {
        EnsureLiveFilesExist();
        var configText = File.ReadAllText(ConfigPath);
        var authText = File.ReadAllText(AuthPath);
        var liveIdentity = _liveAuthInspector.Inspect(configText, authText);
        if (liveIdentity is null)
        {
            _profileStore.DeleteCurrentProfileState();
            return new IdentitySyncResult();
        }

        var existing = _profileStore.FindProfileByIdentityFingerprint(liveIdentity.IdentityFingerprint);
        if (existing is not null)
        {
            _profileStore.WriteCurrentProfileState(existing);
            return new IdentitySyncResult
            {
                LiveIdentity = liveIdentity,
                MatchedProfileName = existing.Name,
                MatchedProfileKind = existing.Kind,
                CurrentProfileUpdated = true,
                IsManaged = true
            };
        }

        if (liveIdentity.Kind == ProfileKind.ChatGptSnapshot)
        {
            var generatedName = _profileStore.CreateUniqueProfileName(liveIdentity);
            _profileStore.SaveChatGptSnapshot(generatedName, configText, authText, liveIdentity, isAutoCaptured: true);
            var created = _profileStore.FindProfileByName(generatedName);
            if (created is not null)
            {
                _profileStore.WriteCurrentProfileState(created);
            }
            else
            {
                _profileStore.WriteCurrentProfileState(generatedName, ProfileKind.ChatGptSnapshot);
            }

            return new IdentitySyncResult
            {
                LiveIdentity = liveIdentity,
                MatchedProfileName = generatedName,
                MatchedProfileKind = ProfileKind.ChatGptSnapshot,
                CreatedProfile = true,
                CurrentProfileUpdated = true,
                IsManaged = true
            };
        }

        _profileStore.DeleteCurrentProfileState();
        return new IdentitySyncResult
        {
            LiveIdentity = liveIdentity,
            IsManaged = false
        };
    }

    public IReadOnlyList<ProfileSummary> GetProfiles()
    {
        return _profileStore.GetProfiles();
    }

    public ApiProfileSpec? LoadApiProfile(string profileName)
    {
        return _profileStore.LoadApiProfile(profileName);
    }

    public void CaptureCurrentChatGptSnapshot(string profileName)
    {
        EnsureLiveFilesExist();
        var configText = File.ReadAllText(ConfigPath);
        var authText = File.ReadAllText(AuthPath);
        var liveIdentity = _liveAuthInspector.Inspect(configText, authText);
        if (!string.Equals(liveIdentity?.AuthMode, "chatgpt", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Current live auth is not a ChatGPT login, so it cannot be captured as a ChatGPT snapshot.");
        }

        var storedProfileName = PathHelpers.SanitizeProfileName(profileName);
        _profileStore.SaveChatGptSnapshot(storedProfileName, configText, authText, liveIdentity, isAutoCaptured: false);
        var summary = _profileStore.FindProfileByName(storedProfileName);
        if (summary is not null)
        {
            _profileStore.WriteCurrentProfileState(summary);
        }
        else
        {
            _profileStore.WriteCurrentProfileState(storedProfileName, ProfileKind.ChatGptSnapshot);
        }
    }

    public void SaveApiProfile(ApiProfileSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Name))
        {
            throw new InvalidOperationException("Profile name is required.");
        }

        if (string.IsNullOrWhiteSpace(spec.ApiKey))
        {
            throw new InvalidOperationException("API key is required.");
        }

        _profileStore.SaveApiProfile(spec);
    }

    public void DeleteProfile(string profileName)
    {
        _profileStore.DeleteProfile(profileName);
        if (string.Equals(_profileStore.ReadCurrentProfileState()?.ProfileName, profileName, StringComparison.OrdinalIgnoreCase))
        {
            _profileStore.DeleteCurrentProfileState();
        }
    }

    public SwitchResult SwitchProfile(string profileName)
    {
        EnsureLiveFilesExist();
        var currentConfigText = File.ReadAllText(ConfigPath);
        var backupPath = _profileStore.BackupLiveFiles(ConfigPath, AuthPath);
        var (configText, authText, kind) = _profileStore.LoadProfileFilesForSwitch(profileName, currentConfigText);
        TextFileService.WriteUtf8NoBom(ConfigPath, configText);
        TextFileService.WriteUtf8NoBom(AuthPath, authText);
        _runtimeEnvironment.ApplyForProfile(kind == ProfileKind.ApiKey ? _profileStore.LoadApiProfile(profileName) : null);

        var summary = _profileStore.FindProfileByName(profileName);
        if (summary is not null)
        {
            _profileStore.WriteCurrentProfileState(summary);
        }
        else
        {
            _profileStore.WriteCurrentProfileState(profileName, kind);
        }

        return new SwitchResult
        {
            AppliedProfileName = profileName,
            ProfileKind = kind,
            BackupPath = backupPath,
            RestartRequired = true
        };
    }

    private void EnsureLiveFilesExist()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException("Codex config.toml was not found.", ConfigPath);
        }

        if (!File.Exists(AuthPath))
        {
            throw new FileNotFoundException("Codex auth.json was not found.", AuthPath);
        }
    }

    private static string ReadCurrentAuthMode(string authText)
    {
        try
        {
            using var document = JsonDocument.Parse(authText);
            var root = document.RootElement;
            if (root.TryGetProperty("auth_mode", out var authModeElement))
            {
                var value = authModeElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value!;
                }
            }

            if (root.TryGetProperty("OPENAI_API_KEY", out var apiKeyElement) && !string.IsNullOrWhiteSpace(apiKeyElement.GetString()))
            {
                return "apikey";
            }
        }
        catch
        {
        }

        return "unknown";
    }
}

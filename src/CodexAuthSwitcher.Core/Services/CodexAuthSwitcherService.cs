using System.Text.Json;
using CodexAuthSwitcher.Core.Models;
using CodexAuthSwitcher.Core.Utilities;

namespace CodexAuthSwitcher.Core.Services;

public sealed class CodexAuthSwitcherService
{
    private readonly ProfileStore _profileStore;
    private readonly LiveAuthInspector _liveAuthInspector;
    private readonly ICodexRuntimeEnvironmentService _runtimeEnvironment;
    private readonly Func<bool> _hasRunningCodex;

    public CodexAuthSwitcherService(string codexHomePath)
    {
        _hasRunningCodex = CodexDesktopProcessService.HasRunningCodex;
        _runtimeEnvironment = new CodexRuntimeEnvironmentService();
        _liveAuthInspector = new LiveAuthInspector(_runtimeEnvironment);
        _profileStore = new ProfileStore(codexHomePath, new ProtectedSecretStore(), _liveAuthInspector);
        ConfigPath = Path.Combine(_profileStore.CodexHomePath, "config.toml");
        AuthPath = Path.Combine(_profileStore.CodexHomePath, "auth.json");
    }

    public CodexAuthSwitcherService(
        ProfileStore profileStore,
        LiveAuthInspector liveAuthInspector,
        ICodexRuntimeEnvironmentService? runtimeEnvironment = null,
        Func<bool>? hasRunningCodex = null)
    {
        _hasRunningCodex = hasRunningCodex ?? (() => false);
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
        EnsureConfigExists();
        var configText = File.ReadAllText(ConfigPath);
        var authText = File.Exists(AuthPath) ? File.ReadAllText(AuthPath) : "{}";
        var liveIdentity = _liveAuthInspector.Inspect(configText, authText);
        return new CodexEnvironment
        {
            CodexHomePath = _profileStore.CodexHomePath,
            ConfigPath = ConfigPath,
            AuthPath = AuthPath,
            CurrentAuthMode = liveIdentity?.AuthMode ?? ReadCurrentAuthMode(authText),
            ModelProvider = TomlOverlayService.TryReadScalar(configText, "model_provider") ?? "openai",
            Model = TomlOverlayService.TryReadScalar(configText, "model") ?? "(default)",
            LiveIdentity = liveIdentity,
            CurrentProfile = _profileStore.ReadCurrentProfileState()
        };
    }

    public IdentitySyncResult EnsureCurrentIdentityTracked()
    {
        EnsureConfigExists();
        if (!File.Exists(AuthPath))
        {
            _profileStore.DeleteCurrentProfileState();
            return new IdentitySyncResult();
        }

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
            if (existing.Kind == ProfileKind.ChatGptSnapshot)
            {
                _profileStore.SaveChatGptSnapshot(
                    existing.Name,
                    configText,
                    authText,
                    liveIdentity,
                    existing.IsAutoCaptured);
                existing = _profileStore.FindProfileByName(existing.Name) ?? existing;
            }

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
        EnsureConfigExists();
        EnsureFileCredentialStore(File.ReadAllText(ConfigPath));
        if (!File.Exists(AuthPath))
        {
            throw new FileNotFoundException("Codex auth.json was not found. Sign in with ChatGPT using file credential storage before capturing the account.", AuthPath);
        }

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

        if (spec.GetEffectiveProviderId() is "ollama" or "lmstudio")
            throw new InvalidOperationException("This provider ID is reserved by Codex. Choose a custom provider name.");
        if (spec.WireApi != "responses")
            throw new InvalidOperationException("Codex API profiles require the responses wire API.");
        spec.RequiresOpenAiAuth = true;
        TomlOverlayService.Validate(TomlOverlayService.ApplyApiOverlay(string.Empty, spec));
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
        if (_hasRunningCodex())
            throw new InvalidOperationException("Close Codex Desktop and Codex CLI tasks before switching accounts or synchronizing conversation routing.");
        EnsureConfigExists();
        _profileStore.EnsureLayout();
        using var switchLock = File.Open(Path.Combine(_profileStore.RootPath, "switch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ThreadContinuityService.EnsureNoInterruptedSwitch(_profileStore.BackupsPath);
        var currentConfigText = File.ReadAllText(ConfigPath);
        EnsureFileCredentialStore(currentConfigText);
        var (configText, authText, kind) = _profileStore.LoadProfileFilesForSwitch(profileName, currentConfigText);
        var backupPath = _profileStore.BackupLiveFiles(ConfigPath, AuthPath);
        var synchronizedThreads = ThreadContinuityService.Switch(_profileStore.CodexHomePath, configText, authText, backupPath, () =>
        {
            if (_hasRunningCodex())
                throw new InvalidOperationException("Codex started while preparing the switch. Close it and try again.");
        });
        _runtimeEnvironment.ApplyForProfile(kind == ProfileKind.ApiKey ? _profileStore.LoadApiProfile(profileName) : null);

        try
        {
            var summary = _profileStore.FindProfileByName(profileName);
            if (summary is not null)
            {
                _profileStore.WriteCurrentProfileState(summary);
            }
            else
            {
                _profileStore.WriteCurrentProfileState(profileName, kind);
            }
        }
        catch
        {
            // The live auth switch is authoritative. Refresh will reconstruct
            // this convenience state from the active identity if needed.
        }

        return new SwitchResult
        {
            AppliedProfileName = profileName,
            ProfileKind = kind,
            BackupPath = backupPath,
            SynchronizedThreads = synchronizedThreads,
            RestartRequired = true
        };
    }

    private void EnsureConfigExists()
    {
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException("Codex config.toml was not found.", ConfigPath);
        }
    }

    private static void EnsureFileCredentialStore(string configText)
    {
        var credentialStore = TomlOverlayService.TryReadScalar(configText, "cli_auth_credentials_store") ?? "file";
        if (!string.Equals(credentialStore, "file", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Codex is configured with cli_auth_credentials_store = '{credentialStore}'. " +
                "Codex Auth Switcher only performs deterministic switches when credentials are stored in auth.json. " +
                "Set cli_auth_credentials_store = \"file\" and sign in again before switching profiles.");
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
